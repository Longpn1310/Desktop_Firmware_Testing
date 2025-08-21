using System;
using System.Net.Sockets;

namespace Desktop_Firmware_Testing
{
    public sealed class TcpEmcTransport : IEmcTransport
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _recvTimeoutMs;
        private readonly int _sendTimeoutMs;

        private TcpClient? _client;
        private NetworkStream? _stream;

        public TcpEmcTransport(string host, int port, int recvTimeoutMs = 50, int sendTimeoutMs = 2000)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _recvTimeoutMs = recvTimeoutMs;
            _sendTimeoutMs = sendTimeoutMs;
        }

        public bool IsOpen => _client != null && _client.Connected && _stream != null;
        public int BytesToRead => (_client != null && _client.Connected) ? _client.Available : 0;

        public void Open()
        {
            if (_client?.Connected == true) return;

            _client = new TcpClient();
            _client.ReceiveTimeout = _recvTimeoutMs;
            _client.SendTimeout = _sendTimeoutMs;
            _client.NoDelay = true;
            _client.Connect(_host, _port);

            _stream = _client.GetStream();
            _stream.ReadTimeout = _recvTimeoutMs;
            _stream.WriteTimeout = _sendTimeoutMs;
        }

        public void Close()
        {
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        public void DiscardInBuffer()
        {
            if (_client == null || _stream == null) return;
            try
            {
                var buf = new byte[4096];
                while (_client.Available > 0)
                {
                    int n = _stream.Read(buf, 0, Math.Min(buf.Length, _client.Available));
                    if (n <= 0) break;
                }
            }
            catch { }
        }

        public void DiscardOutBuffer() { /* not applicable for TCP */ }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_client == null || _stream == null) return 0;
            if (_client.Available <= 0) return 0;
            try { return _stream.Read(buffer, offset, count); }
            catch { return 0; }
        }

        public int ReadByte()
        {
            if (_client == null || _stream == null) return -1;
            if (_client.Available <= 0) return -1;
            try { return _stream.ReadByte(); }
            catch { return -1; }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (_stream == null) throw new InvalidOperationException("Chưa kết nối TCP");
            _stream.Write(buffer, offset, count);
            _stream.Flush();
        }

        public void Dispose() => Close();
        public override string ToString() => $"Tcp({_host}:{_port})";
    }
}
