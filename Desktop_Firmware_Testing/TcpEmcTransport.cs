using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop_Firmware_Testing
{
    /// <summary>
    /// TCP Transport (Server/Client)
    /// Server mode khi host: "" | "0.0.0.0" | "*" | "Any" | "Server"
    /// Client mode khi host là IP/hostname cụ thể
    /// </summary>
    public sealed class TcpEmcTransport : IEmcTransport, IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _recvTimeoutMs;
        private readonly int _sendTimeoutMs;
        private readonly int _connectTimeoutMs;

        private readonly object _lockObj = new object();

        // Server
        private TcpListener? _listener;
        private CancellationTokenSource? _ctsAccept;
        private Task? _acceptTask;

        // Active connection (server-accepted client hoặc client-kết nối)
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;

        // Read buffer (trả dữ liệu đã đệm trước)
        private byte[] _receiveBuffer = new byte[4096];
        private int _receiveBufferOffset = 0;
        private int _receiveBufferCount = 0;

        private readonly bool _isServerMode;

        public TcpEmcTransport(
            string host,
            int port,
            int recvTimeoutMs = 2000,
            int sendTimeoutMs = 2000,
            int connectTimeoutMs = 5000)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (recvTimeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(recvTimeoutMs));
            if (sendTimeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(sendTimeoutMs));
            if (connectTimeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(connectTimeoutMs));

            _host = host ?? string.Empty;
            _port = port;
            _recvTimeoutMs = recvTimeoutMs;
            _sendTimeoutMs = sendTimeoutMs;
            _connectTimeoutMs = connectTimeoutMs;

            _isServerMode =
                string.IsNullOrWhiteSpace(_host) ||
                _host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
                _host.Equals("*", StringComparison.OrdinalIgnoreCase) ||
                _host.Equals("Any", StringComparison.OrdinalIgnoreCase) ||
                _host.Equals("Server", StringComparison.OrdinalIgnoreCase);

            Log($"Created. Mode={(_isServerMode ? "SERVER" : "CLIENT")}, Host='{_host}', Port={_port}");
        }

        public bool IsOpen
        {
            get
            {
                lock (_lockObj)
                {
                    if (_tcpClient == null) return false;
                    var sock = _tcpClient.Client;
                    return sock != null && sock.Connected && IsSocketConnected(sock);
                }
            }
        }

        public int BytesToRead
        {
            get
            {
                lock (_lockObj)
                {
                    if (!IsOpen) return 0;
                    if (_receiveBufferCount > 0) return _receiveBufferCount;
                    try
                    {
                        return _tcpClient?.Available ?? 0; // dựa vào Socket.Available
                    }
                    catch { return 0; }
                }
            }
        }

        public void Open()
        {
            lock (_lockObj)
            {
                CloseInternal();
                if (_isServerMode) OpenAsServer();
                else OpenAsClient();
            }
        }

        private void OpenAsServer()
        {
            try
            {
                Log($"Starting TCP server on :{_port}");

                _listener = new TcpListener(IPAddress.Any, _port);
                // Cho phép reuse nhanh (tùy nền tảng)
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(50);

                // In ra IP nội bộ
                try
                {
                    var host = Dns.GetHostEntry(Dns.GetHostName());
                    foreach (var ip in host.AddressList.Where(i => i.AddressFamily == AddressFamily.InterNetwork))
                        Log($"Server accessible at: {ip}:{_port}");
                }
                catch { }

                _ctsAccept = new CancellationTokenSource();
                _acceptTask = Task.Run(() => AcceptLoopAsync(_ctsAccept.Token));

                // Chờ client đầu tiên theo timeout _connectTimeoutMs
                Log($"Waiting for first client within {_connectTimeoutMs} ms...");
                var start = Environment.TickCount64;
                while ((_tcpClient == null || !_tcpClient.Connected) &&
                       (Environment.TickCount64 - start) < (long)_connectTimeoutMs)
                {
                    if (_ctsAccept!.IsCancellationRequested) throw new OperationCanceledException();
                    Thread.Sleep(100);
                }
                if (_tcpClient == null || !_tcpClient.Connected)
                    throw new TimeoutException($"No client connected within {_connectTimeoutMs} ms");

                Log($"Client connected: {_tcpClient.Client?.RemoteEndPoint}");
            }
            catch
            {
                CloseInternal();
                throw;
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            if (_listener == null) return;

            while (!token.IsCancellationRequested)
            {
                try
                {
#if NET6_0_OR_GREATER
                    using var reg = token.Register(() => { try { _listener?.Stop(); } catch { } });
                    var client = await _listener.AcceptTcpClientAsync(token);
#else
                    var tcs = new TaskCompletionSource<TcpClient>();
                    var ar = _listener.BeginAcceptTcpClient(_ => {
                        try { tcs.TrySetResult(_listener.EndAcceptTcpClient(ar)); } catch (Exception ex) { tcs.TrySetException(ex); }
                    }, null);
                    using (token.Register(() => tcs.TrySetCanceled()))
                        var client = await tcs.Task;
#endif
                    ConfigureClient(client);

                    lock (_lockObj)
                    {
                        // Đóng client cũ nếu có
                        if (_tcpClient != null)
                        {
                            Log("Closing previous client");
                            SafeCloseClient(_tcpClient);
                        }

                        _tcpClient = client;
                        _stream = _tcpClient.GetStream();
                        _stream.ReadTimeout = _recvTimeoutMs > 0 ? _recvTimeoutMs : Timeout.Infinite;
                        _stream.WriteTimeout = _sendTimeoutMs > 0 ? _sendTimeoutMs : Timeout.Infinite;

                        _receiveBufferOffset = 0;
                        _receiveBufferCount = 0;

                        Log($"Accepted client {_tcpClient.Client?.RemoteEndPoint}");
                    }
                }
                catch (ObjectDisposedException)
                {
                    // listener.Stop() khi cancel → thoát vòng lặp
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested) Log($"Accept error: {ex.Message}");
                }
            }
        }

        private void OpenAsClient()
        {
            if (string.IsNullOrWhiteSpace(_host))
                throw new InvalidOperationException("Host cannot be empty in client mode.");

            Exception? last = null;
            const int MaxRetries = 3;
            const int RetryDelayMs = 1000;

            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    if (i > 0) { Log($"Retry {i + 1}/{MaxRetries} after {RetryDelayMs} ms"); Thread.Sleep(RetryDelayMs); }

                    var client = new TcpClient();
                    client.NoDelay = true;
                    client.ReceiveTimeout = _recvTimeoutMs;
                    client.SendTimeout = _sendTimeoutMs;

                    IPAddress ip;
                    if (!IPAddress.TryParse(_host, out ip))
                    {
                        if (_host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) ip = IPAddress.Loopback;
                        else
                        {
                            var he = Dns.GetHostEntry(_host);
                            ip = he.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                                 ?? he.AddressList.First();
                        }
                    }

                    var ep = new IPEndPoint(ip, _port);
                    Log($"Connecting to {ep} ...");

                    // Connect with timeout
                    var connectTask = client.ConnectAsync(ep.Address, ep.Port);
                    if (!connectTask.Wait(_connectTimeoutMs))
                    {
                        client.Close();
                        throw new TimeoutException($"Connect timeout after {_connectTimeoutMs} ms");
                    }

                    ConfigureKeepAlive(client.Client);

                    lock (_lockObj)
                    {
                        _tcpClient = client;
                        _stream = client.GetStream();
                        _stream.ReadTimeout = _recvTimeoutMs > 0 ? _recvTimeoutMs : Timeout.Infinite;
                        _stream.WriteTimeout = _sendTimeoutMs > 0 ? _sendTimeoutMs : Timeout.Infinite;

                        _receiveBufferOffset = 0;
                        _receiveBufferCount = 0;
                    }

                    Log($"Connected to {ep}");
                    return;
                }
                catch (SocketException ex)
                {
                    last = ex;
                    Log($"Connect failed: {ex.SocketErrorCode} - {ex.Message}");
                    if (ex.SocketErrorCode == SocketError.HostNotFound) break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log($"Connect failed: {ex.Message}");
                }
            }

            throw new InvalidOperationException($"Failed to connect to {_host}:{_port}. Last error: {last?.Message}", last);
        }

        public void Close()
        {
            lock (_lockObj)
            {
                CloseInternal();
            }
        }

        private void CloseInternal()
        {
            try
            {
                _ctsAccept?.Cancel();
            }
            catch { }

            try
            {
                if (_acceptTask != null)
                {
                    try { _acceptTask.Wait(500); } catch { }
                    _acceptTask = null;
                }
            }
            catch { }

            try
            {
                if (_listener != null)
                {
                    try { _listener.Stop(); } catch { }
                    _listener = null;
                }
            }
            catch { }

            if (_stream != null)
            {
                try { _stream.Close(); } catch { }
                _stream = null;
            }

            if (_tcpClient != null)
            {
                SafeCloseClient(_tcpClient);
                _tcpClient = null;
            }

            try
            {
                _ctsAccept?.Dispose();
                _ctsAccept = null;
            }
            catch { }

            _receiveBufferOffset = 0;
            _receiveBufferCount = 0;

            Log("Closed");
        }

        public void DiscardInBuffer()
        {
            lock (_lockObj)
            {
                _receiveBufferOffset = 0;
                _receiveBufferCount = 0;

                if (!IsOpen) return;

                try
                {
                    // Đọc sạch dữ liệu đang có
                    var sock = _tcpClient!.Client;
                    var trash = new byte[1024];
                    while (sock.Available > 0)
                    {
                        int toRead = Math.Min(trash.Length, sock.Available);
                        _stream!.ReadTimeout = 10;
                        _stream.Read(trash, 0, toRead);
                    }
                    // reset timeout
                    _stream!.ReadTimeout = _recvTimeoutMs > 0 ? _recvTimeoutMs : Timeout.Infinite;
                }
                catch { }
            }
        }

        public void DiscardOutBuffer()
        {
            // TCP không có flush discard out buffer
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset >= buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return 0;

            lock (_lockObj)
            {
                if (!IsOpen || _stream == null) return 0;

                try
                {
                    // Ưu tiên dữ liệu đã đệm
                    if (_receiveBufferCount > 0)
                    {
                        int bytes = Math.Min(count, _receiveBufferCount);
                        Array.Copy(_receiveBuffer, _receiveBufferOffset, buffer, offset, bytes);
                        _receiveBufferOffset += bytes;
                        _receiveBufferCount -= bytes;
                        return bytes;
                    }

                    // Nếu không có sẵn và không muốn block, trả 0 khi không DataAvailable
                    if (_recvTimeoutMs == 0 && _tcpClient!.Available == 0) return 0;

                    // Đọc từ stream (block theo ReadTimeout)
                    int read = _stream.Read(buffer, offset, count);
                    return read;
                }
                catch (IOException ioe) // timeout cũng ném IOException
                {
                    if (ioe.InnerException is SocketException se &&
                        (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock))
                        return 0;

                    Log($"Read error: {ioe.Message}");
                    return 0;
                }
                catch (Exception ex)
                {
                    Log($"Read error: {ex.Message}");
                    return 0;
                }
            }
        }

        public int ReadByte()
        {
            var one = new byte[1];
            var n = Read(one, 0, 1);
            return n == 1 ? one[0] : -1;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset >= buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return;

            lock (_lockObj)
            {
                if (!IsOpen || _stream == null) throw new InvalidOperationException("Socket is not connected.");

                try
                {
                    _stream.Write(buffer, offset, count);
                    _stream.Flush();
                }
                catch (Exception ex)
                {
                    Log($"Write error: {ex.Message}");
                    CloseInternal();
                    throw;
                }
            }
        }

        private static void ConfigureClient(TcpClient client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = client.ReceiveTimeout == 0 ? Timeout.Infinite : client.ReceiveTimeout;
            client.SendTimeout = client.SendTimeout == 0 ? Timeout.Infinite : client.SendTimeout;

            ConfigureKeepAlive(client.Client);
        }

        private static bool IsSocketConnected(Socket socket)
        {
            try
            {
                return socket.Connected && !(socket.Poll(1, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch { return false; }
        }

        private static void ConfigureKeepAlive(Socket socket)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                // SIO_KEEPALIVE_VALS: on=1, time(ms)=5000, interval(ms)=2000
                byte[] keepAlive = new byte[12];
                BitConverter.GetBytes((uint)1).CopyTo(keepAlive, 0);
                BitConverter.GetBytes((uint)5000).CopyTo(keepAlive, 4);
                BitConverter.GetBytes((uint)2000).CopyTo(keepAlive, 8);
                socket.IOControl(IOControlCode.KeepAliveValues, keepAlive, null);
            }
            catch { }
        }

        private static void SafeCloseClient(TcpClient client)
        {
            try
            {
                try { client.Client?.Shutdown(SocketShutdown.Both); } catch { }
                client.Close();
                client.Dispose();
            }
            catch { }
        }

        private static void Log(string msg)
        {
            Debug.WriteLine($"[TcpEmcTransport] {msg}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TcpEmcTransport] {msg}");
        }

        public void Dispose() => Close();

        public override string ToString() =>
            _isServerMode ? $"TCPServer(:{_port})" : $"TCPClient({_host}:{_port})";
    }
}
