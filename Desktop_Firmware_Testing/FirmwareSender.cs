using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Desktop_Firmware_Testing
{
    // Khung: [H1,H2,H3, CMD, len_L, len_H, payload..., cks]
    // cks = (CMD + len_L + len_H + sum(payload)) & 0xFF
    // Firmware: CMD 0xFB (INIT), 0xFC (DATA). Header có thể EMC hoặc MCE.
    // Ping/Pong: header 'M','C','E', cmd=0x04, payload [data, 0x01].
    public sealed class FirmwareSender
    {
        public enum FrameHeader { EMC, MCE }

        private readonly IEmcTransport _io;
        private readonly int _blockSize;     // <= 512
        private readonly int _maxRetries;
        private readonly int _ackTimeoutMs;
        private readonly byte _cabinetAddr;  // 1..6
        private readonly uint _loadAddress;  // 4 bytes

        public event EventHandler<string>? LogEmitted;
        public event EventHandler<ProgressInfo>? ProgressChanged;
        public readonly record struct ProgressInfo(double Percent, long SentBytes, long TotalBytes);

        // Giữ tương thích
        public FirmwareSender(System.IO.Ports.SerialPort port, int blockSize = 512, int maxRetries = 5, int ackTimeoutMs = 2000)
            : this(new SerialEmcTransport(port), blockSize, maxRetries, ackTimeoutMs, cabinetAddr: 1, loadAddress: 0) { }

        public FirmwareSender(IEmcTransport transport, int blockSize, int maxRetries, int ackTimeoutMs,
                              byte cabinetAddr, uint loadAddress)
        {
            _io = transport ?? throw new ArgumentNullException(nameof(transport));
            if (blockSize <= 0 || blockSize > 512) throw new ArgumentOutOfRangeException(nameof(blockSize));
            if (cabinetAddr < 1 || cabinetAddr > 6) throw new ArgumentOutOfRangeException(nameof(cabinetAddr));

            _blockSize = blockSize;
            _maxRetries = Math.Max(0, maxRetries);
            _ackTimeoutMs = Math.Max(1, ackTimeoutMs);
            _cabinetAddr = cabinetAddr;
            _loadAddress = loadAddress;
        }

        // ====== Ping/Pong MCE (cmd=0x04, payload [data,0x01]) ======
        public bool Ping(byte data, int timeoutMs, CancellationToken ct)
        {
            var payload = new byte[] { data, 0x01 };
            int tries = 0;
            while (tries++ <= _maxRetries)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

                var tx = BuildFrame(FrameHeader.MCE, 0x04, payload);
                TrySafe(_io.DiscardInBuffer);
                Log($"-> PING TRY {tries}: {ToHex(tx)}");
                _io.Write(tx, 0, tx.Length);

                var rx = ReadFrame(FrameHeader.MCE, timeoutMs, ct);
                if (rx == null) { Log("<- PING TIMEOUT/NO FRAME"); continue; }

                Log($"<- PONG: {ToHex(rx.Value.Raw)}");
                if (rx.Value.Cmd == 0x04 && rx.Value.Payload.Length >= 2 &&
                    rx.Value.Payload[0] == data && rx.Value.Payload[1] == 0x01)
                {
                    Log("PING OK");
                    return true;
                }
                Log("PONG không hợp lệ");
            }
            return false;
        }

        // ====== Gửi firmware với header EMC (API cũ) ======
        public bool SendAsync(string filePath, CancellationToken ct) =>
            SendAsyncCore(filePath, ct, FrameHeader.EMC);

        // ====== Gửi firmware với header MCE ======
        public bool SendAsyncMce(string filePath, CancellationToken ct) =>
            SendAsyncCore(filePath, ct, FrameHeader.MCE);

        private bool SendAsyncCore(string filePath, CancellationToken ct, FrameHeader header)
        {
            if (!_io.IsOpen) throw new InvalidOperationException("Transport chưa mở");
            if (!File.Exists(filePath)) throw new FileNotFoundException("Không tìm thấy file firmware", filePath);

            var fileBytes = File.ReadAllBytes(filePath);
            long total = fileBytes.LongLength;
            if ((ulong)total > uint.MaxValue) throw new InvalidOperationException("Firmware > 4GB không hỗ trợ.");

            Log($"[{header}] INIT: file={Path.GetFileName(filePath)}, size={total}B, chunk={_blockSize}, addr={_cabinetAddr}, load=0x{_loadAddress:X8}");

            if (!SendInitAndWaitAck((uint)total, ct, header))
            {
                Log("INIT thất bại");
                return false;
            }

            long sent = 0;
            ushort frameIdx = 0;
            using var ms = new MemoryStream(fileBytes);
            var buf = new byte[_blockSize];

            int read;
            while ((read = ms.Read(buf, 0, buf.Length)) > 0)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

                var payload = new byte[1 + read + 2];
                int p = 0;
                payload[p++] = _cabinetAddr;
                Buffer.BlockCopy(buf, 0, payload, p, read);
                p += read;
                WriteLE16(payload, p, frameIdx);

                if (!SendFrameWaitAck_Data(0xFC, payload, ct, frameIdx, header))
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

        // INIT: 0xFB -> chờ ACK 0xFC idx=0
        private bool SendInitAndWaitAck(uint fwSize, CancellationToken ct, FrameHeader header)
        {
            var payload = new byte[11];
            int p = 0;
            payload[p++] = _cabinetAddr;
            WriteLE32(payload, p, _loadAddress); p += 4;
            WriteLE32(payload, p, fwSize); p += 4;
            WriteLE16(payload, p, (ushort)_blockSize);

            int tries = 0;
            while (tries++ <= _maxRetries)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

                var tx = BuildFrame(header, 0xFB, payload);
                TrySafe(_io.DiscardInBuffer);
                Log($"-> [{header}] INIT TRY {tries}: {ToHex(tx)}");
                _io.Write(tx, 0, tx.Length);

                var rx = ReadFrame(header, _ackTimeoutMs, ct);
                if (rx == null) { Log("<- INIT TIMEOUT/NO FRAME"); continue; }

                Log($"<- [{header}] INIT RESP: {ToHex(rx.Value.Raw)}");

                if (rx.Value.Cmd == 0xFC && rx.Value.Payload.Length >= 3)
                {
                    byte addr = rx.Value.Payload[0];
                    ushort idx = ReadLE16(rx.Value.Payload, rx.Value.Payload.Length - 2);
                    if (addr == _cabinetAddr && idx == 0) { Log("ACK INIT OK"); return true; }
                    Log($"ACK INIT sai (addr={addr}, idx={idx})");
                }
                else Log("RESP INIT không hợp lệ");
            }
            return false;
        }

        // DATA: 0xFC -> chờ ACK 0xFC cùng frameIdx
        private bool SendFrameWaitAck_Data(byte cmd, ReadOnlySpan<byte> payload, CancellationToken ct, ushort frameIdx, FrameHeader header)
        {
            int tries = 0;
            while (tries++ <= _maxRetries)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

                var tx = BuildFrame(header, cmd, payload);
                TrySafe(_io.DiscardInBuffer);

                Log($"-> [{header}] FRAME {frameIdx} TRY {tries}: {ToHex(tx)}");
                _io.Write(tx, 0, tx.Length);

                var rx = ReadFrame(header, _ackTimeoutMs, ct);
                if (rx == null) { Log("<- TIMEOUT/NO FRAME"); continue; }

                Log($"<- [{header}] RESP: {ToHex(rx.Value.Raw)}");

                if (rx.Value.Cmd == 0xFC && rx.Value.Payload.Length >= 3)
                {
                    byte addr = rx.Value.Payload[0];
                    ushort idx = ReadLE16(rx.Value.Payload, rx.Value.Payload.Length - 2);
                    if (addr == _cabinetAddr && idx == frameIdx) { Log("ACK DATA OK"); return true; }
                    Log($"ACK sai (addr={addr}, idx={idx})");
                }
                else Log("RESP không hợp lệ");
            }
            return false;
        }

        // ====== Reader/Builder theo header ======
        private EmcFrame? ReadFrame(FrameHeader header, int timeoutMs, CancellationToken ct) =>
            header == FrameHeader.EMC ? ReadFrameRaw(0x45, 0x4D, 0x43, timeoutMs, ct)  // 'E','M','C'
                                      : ReadFrameRaw(0x4D, 0x43, 0x45, timeoutMs, ct); // 'M','C','E'

        private static byte[] BuildFrame(FrameHeader header, byte cmd, ReadOnlySpan<byte> payload) =>
            header == FrameHeader.EMC ? BuildFrameRaw(0x45, 0x4D, 0x43, cmd, payload)
                                      : BuildFrameRaw(0x4D, 0x43, 0x45, cmd, payload);

        private EmcFrame? ReadFrameRaw(byte h1, byte h2, byte h3, int timeoutMs, CancellationToken ct)
        {
            long deadline = Environment.TickCount64 + timeoutMs;

            bool ReadOne(out int b)
            {
                while (Environment.TickCount64 < deadline)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (_io.BytesToRead > 0) { b = _io.ReadByte(); if (b >= 0) return true; }
                    else Thread.Sleep(2);
                }
                b = -1; return false;
            }

            int x;

            // H1
            while (ReadOne(out x)) { if (x == h1) break; }
            if (x != h1) return null;
            // H2
            if (!ReadOne(out x) || x != h2) return null;
            // H3
            if (!ReadOne(out x) || x != h3) return null;

            // cmd, len
            if (!ReadOne(out x)) return null; byte cmd = (byte)x;
            if (!ReadOne(out x)) return null; byte lenL = (byte)x;
            if (!ReadOne(out x)) return null; byte lenH = (byte)x;
            int len = lenL | (lenH << 8);

            var payload = new byte[len];
            int got = 0;
            while (got < len && Environment.TickCount64 < deadline)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                int n = _io.Read(payload, got, len - got);
                if (n > 0) got += n; else Thread.Sleep(2);
            }
            if (got != len) return null;

            if (!ReadOne(out x)) return null; byte cks = (byte)x;
            if (ComputeChecksum(cmd, lenL, lenH, payload) != cks) return null;

            var raw = new byte[3 + 1 + 2 + len + 1];
            raw[0] = h1; raw[1] = h2; raw[2] = h3;
            raw[3] = cmd; raw[4] = lenL; raw[5] = lenH;
            if (len > 0) Buffer.BlockCopy(payload, 0, raw, 6, len);
            raw[^1] = cks;

            return new EmcFrame(cmd, payload, raw);
        }

        private static byte[] BuildFrameRaw(byte h1, byte h2, byte h3, byte cmd, ReadOnlySpan<byte> payload)
        {
            int len = payload.Length;
            byte lenL = (byte)(len & 0xFF);
            byte lenH = (byte)((len >> 8) & 0xFF);
            byte cks = ComputeChecksum(cmd, lenL, lenH, payload);

            var frame = new byte[3 + 1 + 2 + len + 1];
            int i = 0;
            frame[i++] = h1; frame[i++] = h2; frame[i++] = h3;
            frame[i++] = cmd; frame[i++] = lenL; frame[i++] = lenH;
            if (len > 0) { payload.CopyTo(frame.AsSpan(i, len)); i += len; }
            frame[i] = cks;
            return frame;
        }

        // ====== Utils ======
        private static byte ComputeChecksum(byte cmd, byte lenL, byte lenH, ReadOnlySpan<byte> payload)
        {
            int sum = cmd + lenL + lenH;
            for (int i = 0; i < payload.Length; i++) sum += payload[i];
            return (byte)(sum & 0xFF);
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

        private static ushort ReadLE16(byte[] buf, int offset) =>
            (ushort)(buf[offset] | (buf[offset + 1] << 8));

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

        private static void TrySafe(Action a) { try { a(); } catch { } }
        private void Log(string msg) => LogEmitted?.Invoke(this, msg);

        private readonly record struct EmcFrame(byte Cmd, byte[] Payload, byte[] Raw);
    }
}
