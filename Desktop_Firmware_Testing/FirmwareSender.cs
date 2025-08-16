using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace Desktop_Firmware_Testing
{
    // Khung EMC: ['E','M','C', CMD, len_L, len_H, payload..., cks]
    // cks = (CMD + len_L + len_H + sum(payload)) & 0xFF
    // 0xFB (INIT) payload 11B: [addr(1), loadAddr(4, LE), fwSize(4, LE), frameSize(2, LE)]
    // 0xFB ACK: EMC 0xFC payload [addr, frameIdx(LE)] với frameIdx==0
    // 0xFC (DATA) payload: [addr, data..., frameIdx(2, LE)]
    // 0xFC ACK: EMC 0xFC payload [addr, frameIdx(LE)] khớp với frame đã gửi
    public sealed class FirmwareSender
    {
        private readonly SerialPort _port;
        private readonly int _blockSize;          // <= 512
        private readonly int _maxRetries;
        private readonly int _ackTimeoutMs;
        private readonly byte _cabinetAddr;       // 1..6
        private readonly uint _loadAddress;       // theo yêu cầu: 4 bytes, thường 0

        public event EventHandler<string>? LogEmitted;
        public event EventHandler<ProgressInfo>? ProgressChanged;
        public record struct ProgressInfo(double Percent, long SentBytes, long TotalBytes);

        // Giữ tương thích code cũ: addr=1, loadAddress=0
        public FirmwareSender(SerialPort port, int blockSize = 512, int maxRetries = 5, int ackTimeoutMs = 2000)
            : this(port, blockSize, maxRetries, ackTimeoutMs, cabinetAddr: 1, loadAddress: 0) { }

        public FirmwareSender(SerialPort port, int blockSize, int maxRetries, int ackTimeoutMs,
                              byte cabinetAddr, uint loadAddress)
        {
            if (port == null) throw new ArgumentNullException(nameof(port));
            if (blockSize <= 0 || blockSize > 512) throw new ArgumentOutOfRangeException(nameof(blockSize), "blockSize phải 1..512");
            if (cabinetAddr < 1 || cabinetAddr > 6) throw new ArgumentOutOfRangeException(nameof(cabinetAddr), "Địa chỉ tủ 1..6");

            _port = port;
            _blockSize = blockSize;
            _maxRetries = Math.Max(0, maxRetries);
            _ackTimeoutMs = Math.Max(1, ackTimeoutMs);
            _cabinetAddr = cabinetAddr;
            _loadAddress = loadAddress;
        }

        public bool SendAsync(string filePath, CancellationToken ct)
        {
            if (!_port.IsOpen) throw new InvalidOperationException("SerialPort chưa mở");
            if (!File.Exists(filePath)) throw new FileNotFoundException("Không tìm thấy file firmware", filePath);

            var fileBytes = File.ReadAllBytes(filePath);
            long total = fileBytes.LongLength;
            if ((ulong)total > uint.MaxValue) throw new InvalidOperationException("Firmware > 4GB không được hỗ trợ (fwSize 4 bytes).");

            Log($"INIT: file={Path.GetFileName(filePath)}, size={total}B, chunk={_blockSize}, addr={_cabinetAddr}, load=0x{_loadAddress:X8}");

            // 1) Gửi lệnh khởi tạo EMC 0xFB
            if (!SendInitAndWaitAck((uint)total, ct))
            {
                Log("INIT thất bại");
                return false;
            }

            // 2) Gửi các frame dữ liệu EMC 0xFC
            long sent = 0;
            ushort frameIdx = 0; // bắt đầu từ 0
            using var ms = new MemoryStream(fileBytes);
            var buf = new byte[_blockSize];

            int read;
            while ((read = ms.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();

                // payload DATA = [addr, data..., frameIdx_LE]
                var payload = new byte[1 + read + 2];
                int p = 0;
                payload[p++] = _cabinetAddr;
                Buffer.BlockCopy(buf, 0, payload, p, read);
                p += read;
                WriteLE16(payload, p, frameIdx);

                if (!SendFrameWaitAck_Data(0xFC, payload, ct, frameIdx))
                {
                    Log($"FRAME {frameIdx} thất bại");
                    return false;
                }

                sent += read;
                frameIdx++;
                double percent = total == 0 ? 100.0 : (sent * 100.0 / total);
                ProgressChanged?.Invoke(this, new ProgressInfo(percent, sent, total));
            }

            Log("ĐÃ GỬI HẾT DỮ LIỆU");
            ProgressChanged?.Invoke(this, new ProgressInfo(100, total, total));
            return true;
        }

        // Gửi EMC 0xFB và chờ ACK EMC 0xFC với frameIdx==0
        private bool SendInitAndWaitAck(uint fwSize, CancellationToken ct)
        {
            // payload: [addr(1), loadAddr(4 LE), fwSize(4 LE), frameSize(2 LE)] => 11 bytes
            var payload = new byte[11];
            int p = 0;
            payload[p++] = _cabinetAddr;
            WriteLE32(payload, p, _loadAddress); p += 4;
            WriteLE32(payload, p, fwSize); p += 4;
            WriteLE16(payload, p, (ushort)_blockSize);

            int tries = 0;
            while (tries++ <= _maxRetries)
            {
                ct.ThrowIfCancellationRequested();

                var tx = BuildFrame(0xFB, payload);
                try { _port.DiscardInBuffer(); } catch { }
                Log($"-> INIT TRY {tries}: {ToHex(tx)}");
                _port.Write(tx, 0, tx.Length);

                var rx = ReadEmcFrame(_ackTimeoutMs, ct);
                if (rx == null)
                {
                    Log("<- INIT TIMEOUT/NO FRAME");
                    continue;
                }

                Log($"<- INIT RESP: {ToHex(rx.Value.Raw)}");

                if (rx.Value.Cmd == 0xFC && rx.Value.Payload.Length >= 3)
                {
                    byte addr = rx.Value.Payload[0];
                    ushort idx = ReadLE16(rx.Value.Payload, rx.Value.Payload.Length - 2);
                    if (addr == _cabinetAddr && idx == 0)
                    {
                        Log("ACK INIT OK (0xFC, idx=0)");
                        return true;
                    }
                    Log($"ACK INIT sai (addr={addr}, idx={idx}), kỳ vọng (addr={_cabinetAddr}, idx=0)");
                }
                else
                {
                    Log("RESP INIT không hợp lệ");
                }
            }
            return false;
        }

        // Gửi EMC 0xFC data frame và chờ ACK EMC 0xFC với cùng frameIdx
        private bool SendFrameWaitAck_Data(byte cmd, ReadOnlySpan<byte> payload, CancellationToken ct, ushort frameIdx)
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

                if (rx.Value.Cmd == 0xFC && rx.Value.Payload.Length >= 3)
                {
                    byte addr = rx.Value.Payload[0];
                    ushort idx = ReadLE16(rx.Value.Payload, rx.Value.Payload.Length - 2);
                    if (addr == _cabinetAddr && idx == frameIdx)
                    {
                        Log($"ACK DATA OK (addr={addr}, idx={idx})");
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

            int x;

            // 'E'
            while (ReadOne(out x))
            {
                if (x == 0x45) break;
            }
            if (x != 0x45) return null;
            // 'M'
            if (!ReadOne(out x) || x != 0x4D) return null;
            // 'C'
            if (!ReadOne(out x) || x != 0x43) return null;

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

            if (ComputeChecksum(cmd, lenL, lenH, payload) != cks) return null;

            var raw = new byte[3 + 1 + 2 + len + 1];
            raw[0] = 0x45; raw[1] = 0x4D; raw[2] = 0x43;
            raw[3] = cmd; raw[4] = lenL; raw[5] = lenH;
            if (len > 0) Buffer.BlockCopy(payload, 0, raw, 6, len);
            raw[^1] = cks;

            return new EmcFrame(cmd, payload, raw);
        }

        private static void WriteLE16(byte[] buf, int offset, ushort v)
        {
            buf[offset] = (byte)(v & 0xFF);
            buf[offset + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteLE32(byte[] buf, int offset, uint v)
        {
            buf[offset] = (byte)(v & 0xFF);
            buf[offset + 1] = (byte)((v >> 8) & 0xFF);
            buf[offset + 2] = (byte)((v >> 16) & 0xFF);
            buf[offset + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static ushort ReadLE16(byte[] buf, int offset)
        {
            return (ushort)(buf[offset] | (buf[offset + 1] << 8));
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
