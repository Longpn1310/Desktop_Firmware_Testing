using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace Desktop_Firmware_Testing
{
    // Frame dữ liệu:  ['E','M','C', 0xFC, len_L, len_H, addr, data..., frameIdx_L, frameIdx_H, cks]
    // payload = addr(1) + data(N) + frameIdx(2, LE);  len = N + 3
    // cks = (CMD + len_L + len_H + sum(payload)) & 0xFF
    // Phản hồi thành công: cùng định dạng EMC, CMD=0xFC, payload = [addr, frameIdx_L, frameIdx_H]
    public sealed class FirmwareSender
    {
        private readonly SerialPort _port;
        private readonly int _blockSize;
        private readonly int _maxRetries;
        private readonly int _ackTimeoutMs;
        private readonly byte _cabinetAddr;

        public event EventHandler<string>? LogEmitted;
        public event EventHandler<ProgressInfo>? ProgressChanged;
        public record struct ProgressInfo(double Percent, long SentBytes, long TotalBytes);

        // Cho phép gọi giống code cũ (mặc định addr=1)
        public FirmwareSender(SerialPort port, int blockSize = 1024, int maxRetries = 5, int ackTimeoutMs = 2000)
            : this(port, blockSize, maxRetries, ackTimeoutMs, cabinetAddr: 1) { }

        public FirmwareSender(SerialPort port, int blockSize, int maxRetries, int ackTimeoutMs,
                              byte cabinetAddr, uint loadAddress = 0 /*không dùng ở 0xFC*/)
        {
            _port = port;
            _blockSize = blockSize;
            _maxRetries = maxRetries;
            _ackTimeoutMs = ackTimeoutMs;
            _cabinetAddr = cabinetAddr;
            _ = loadAddress;
        }

        public bool SendAsync(string filePath, CancellationToken ct)
        {
            if (!_port.IsOpen) throw new InvalidOperationException("SerialPort chưa mở");

            var fileBytes = File.ReadAllBytes(filePath);
            long total = fileBytes.LongLength;
            Log($"File: {Path.GetFileName(filePath)}, size={total} bytes, chunk={_blockSize}, addr={_cabinetAddr}");

            long sent = 0;
            ushort frameIdx = 0; // 0-based
            using var ms = new MemoryStream(fileBytes);
            var buf = new byte[_blockSize];

            int read;
            while ((read = ms.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();

                // payload = [addr, data..., frameIdx_LE]
                var payload = new byte[1 + read + 2];
                int p = 0;
                payload[p++] = _cabinetAddr;
                Buffer.BlockCopy(buf, 0, payload, p, read);
                p += read;
                payload[p++] = (byte)(frameIdx & 0xFF);
                payload[p++] = (byte)((frameIdx >> 8) & 0xFF);

                if (!SendFrameWaitAck(0xFC, payload, ct, frameIdx))
                {
                    Log($"FRAME {frameIdx} thất bại");
                    return false;
                }

                sent += read;
                frameIdx++;
                double percent = total == 0 ? 100.0 : (sent * 100.0 / total);
                ProgressChanged?.Invoke(this, new ProgressInfo(percent, sent, total));
            }

            // Tuỳ thiết bị: có thể gửi thêm 1 khung rỗng để xác nhận hoàn tất. Không bắt buộc.
            Log("Gửi xong toàn bộ frame");
            ProgressChanged?.Invoke(this, new ProgressInfo(100, total, total));
            return true;
        }

        private bool SendFrameWaitAck(byte cmd, ReadOnlySpan<byte> payload, CancellationToken ct, ushort frameIdx)
        {
            int tries = 0;
            while (tries++ <= _maxRetries)
            {
                ct.ThrowIfCancellationRequested();

                var tx = BuildFrame(cmd, payload);
                try { _port.DiscardInBuffer(); } catch { }

                Log($"-> FRAME {frameIdx} TRY {tries}: {ToHex(tx)}");
                _port.Write(tx, 0, tx.Length);

                var rx = ReadEmcFrame(_ackTimeoutMs, ct);
                if (rx == null)
                {
                    Log("<- TIMEOUT/NO FRAME");
                    continue;
                }

                Log($"<- RESP: {ToHex(rx.Value.Raw)}");

                // Kiểm tra đúng CMD, addr, frameIdx
                if (rx.Value.Cmd == 0xFC && rx.Value.Payload.Length >= 3)
                {
                    byte addr = rx.Value.Payload[0];
                    ushort idx = (ushort)(rx.Value.Payload[^2] | (rx.Value.Payload[^1] << 8)); // LE
                    if (addr == _cabinetAddr && idx == frameIdx)
                    {
                        Log($"ACK EMC OK (addr={addr}, idx={idx})");
                        return true;
                    }
                    Log($"ACK sai (addr={addr}, idx={idx}), kỳ vọng (addr={_cabinetAddr}, idx={frameIdx})");
                }
                else
                {
                    Log("RESP không hợp lệ");
                }
            }
            return false;
        }

        private static byte[] BuildFrame(byte cmd, ReadOnlySpan<byte> payload)
        {
            int len = payload.Length;
            byte lenL = (byte)(len & 0xFF);
            byte lenH = (byte)((len >> 8) & 0xFF);
            byte cks = ComputeChecksum(cmd, lenL, lenH, payload);

            var frame = new byte[3 + 1 + 2 + len + 1];
            int i = 0;
            frame[i++] = 0x45; // 'E'
            frame[i++] = 0x4D; // 'M'
            frame[i++] = 0x43; // 'C'
            frame[i++] = cmd;
            frame[i++] = lenL;
            frame[i++] = lenH;
            if (len > 0)
            {
                payload.CopyTo(frame.AsSpan(i, len));
                i += len;
            }
            frame[i] = cks;
            return frame;
        }

        private static byte ComputeChecksum(byte cmd, byte lenL, byte lenH, ReadOnlySpan<byte> payload)
        {
            int sum = cmd + lenL + lenH;
            for (int i = 0; i < payload.Length; i++) sum += payload[i];
            return (byte)(sum & 0xFF);
        }

        // Đọc 1 frame EMC hoàn chỉnh trong timeout. Trả null nếu timeout/CRC sai.
        private EmcFrame? ReadEmcFrame(int timeoutMs, CancellationToken ct)
        {
            long deadline = Environment.TickCount64 + timeoutMs;

            bool ReadOne(out int b)
            {
                while (Environment.TickCount64 < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (_port.BytesToRead > 0)
                        {
                            b = _port.ReadByte();
                            return true;
                        }
                        Thread.Sleep(2);
                    }
                    catch (TimeoutException) { }
                }
                b = -1;
                return false;
            }

            // Tìm header 'E' 'M' 'C'
            int x;
            // tìm 'E'
            while (ReadOne(out x))
            {
                if (x == 0x45) break;
            }
            if (x != 0x45) return null;
            // 'M'
            if (!ReadOne(out x)) return null;
            if (x != 0x4D) return null;
            // 'C'
            if (!ReadOne(out x)) return null;       
            if (x != 0x43) return null;

            // cmd, len
            if (!ReadOne(out x)) return null; byte cmd = (byte)x;
            if (!ReadOne(out x)) return null; byte lenL = (byte)x;
            if (!ReadOne(out x)) return null; byte lenH = (byte)x;
            int len = lenL | (lenH << 8);

            var payload = new byte[len];
            int got = 0;
            while (got < len && Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    int n = _port.Read(payload, got, len - got);
                    if (n > 0) got += n; else Thread.Sleep(2);
                }
                catch (TimeoutException) { }
            }
            if (got != len) return null;

            if (!ReadOne(out x)) return null; byte cks = (byte)x;

            // kiểm tra checksum
            if (ComputeChecksum(cmd, lenL, lenH, payload) != cks) return null;

            // ghép raw để log
            var raw = new byte[3 + 1 + 2 + len + 1];
            raw[0] = 0x45; raw[1] = 0x4D; raw[2] = 0x43;
            raw[3] = cmd; raw[4] = lenL; raw[5] = lenH;
            if (len > 0) Buffer.BlockCopy(payload, 0, raw, 6, len);
            raw[^1] = cks;

            return new EmcFrame(cmd, payload, raw);
        }

        private static string ToHex(ReadOnlySpan<byte> data)
        {
            var sb = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append(data[i].ToString("X2"));
                if (i + 1 < data.Length) sb.Append(' ');
            }
            return sb.ToString();
        }

        private void Log(string msg) => LogEmitted?.Invoke(this, msg);

        private readonly record struct EmcFrame(byte Cmd, byte[] Payload, byte[] Raw);
    }
}
