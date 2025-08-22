using Microsoft.Win32;
using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Desktop_Firmware_Testing
{
    public partial class MainWindow : Window
    {
        private SerialPort? _serial;
        private IEmcTransport? _transport;
        private FirmwareSender? _sender;
        private CancellationTokenSource? _cts;

        // cấu hình mặc định
        private const int DefaultBlockSize = 512;
        private const int DefaultRetries = 5;
        private const int DefaultAckTimeoutMs = 2000;
        private const int DefaultPingTimeoutMs = 1000;

        public MainWindow()
        {
            InitializeComponent();
            RefreshPorts();
            TxtLog.Text = "";
            TxtConnState.Text = "Chưa kết nối";
        }

        // Gắn trong XAML: Loaded="Window_Loaded"
        private void Window_Loaded(object sender, RoutedEventArgs e) => UpdateModeUI();

        protected override void OnClosed(EventArgs e)
        {
            TryDisconnect();
            base.OnClosed(e);
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateModeUI();
        }

        private void UpdateModeUI()
        {
            bool serial = RdoSerial.IsChecked == true;
            PnlSerial.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
            PnlTcp.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;

            if (_transport?.IsOpen == true) TryDisconnect();

            BtnSend.IsEnabled = false;
            TxtConnState.Text = "Chưa kết nối";
            BtnConnect.Content = "Kết nối";
        }

        private void RefreshPorts()
        {
            var ports = SerialPort.GetPortNames().OrderBy(x => x).ToArray();
            CmbPorts.ItemsSource = ports;
            if (ports.Length > 0) CmbPorts.SelectedIndex = 0;
        }

        private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

        private void PickFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Binary firmware (*.bin)|*.bin",
                DefaultExt = ".bin",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dlg.ShowDialog() == true)
            {
                TxtFile.Text = dlg.FileName;
                BtnSend.IsEnabled = _transport?.IsOpen == true;
                Log($"Chọn file: {Path.GetFileName(dlg.FileName)}");
            }
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_transport?.IsOpen == true)
            {
                TryDisconnect();
                TxtConnState.Text = "Chưa kết nối";
                BtnConnect.Content = "Kết nối";
                BtnSend.IsEnabled = false;
                Log("Đã ngắt kết nối");
                return;
            }

            // địa chỉ tủ 1..6
            byte cabinetAddr = 1;
            if (CmbAddr.SelectedItem is ComboBoxItem addrItem &&
                byte.TryParse(addrItem.Content?.ToString(), out var parsed) &&
                parsed >= 1 && parsed <= 6)
            {
                cabinetAddr = parsed;
            }

            if (RdoSerial.IsChecked == true)
            {
                ConnectSerial(cabinetAddr);
            }
            else
            {
                ConnectTcp(cabinetAddr);
            }
        }

        private void ConnectSerial(byte cabinetAddr)
        {
            if (CmbPorts.SelectedItem is not string portName)
            {
                MessageBox.Show("Chọn cổng COM");
                return;
            }
            if (CmbBaud.SelectedItem is not ComboBoxItem baudItem ||
                !int.TryParse(baudItem.Content?.ToString(), out int baud))
            {
                MessageBox.Show("Baud rate không hợp lệ");
                return;
            }

            _serial = new SerialPort(portName)
            {
                BaudRate = baud,
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                ReadTimeout = 200,   // chỉ để tránh block dài; không dùng làm timeout tổng
                WriteTimeout = 2000,
                DtrEnable = true,
                RtsEnable = false,
                ReceivedBytesThreshold = 1
            };

            try
            {
                var serialTx = new SerialEmcTransport(_serial);
                serialTx.Open();
                _transport = serialTx;

                _sender = new FirmwareSender(_transport, DefaultBlockSize, DefaultRetries, DefaultAckTimeoutMs, cabinetAddr, 0);
                _sender.LogEmitted += (_, msg) => Dispatcher.Invoke(() => Log(msg));

                // ping không dùng token timeout
                TryFlushIo();
                bool pingOk = _sender.Ping(0x55, DefaultPingTimeoutMs, CancellationToken.None);
                Log($"PING {(pingOk ? "OK" : "FAIL")}");

                TxtConnState.Text = $"Serial: {_serial.PortName} @ {baud}";
                BtnConnect.Content = "Ngắt";
                BtnSend.IsEnabled = pingOk && !string.IsNullOrWhiteSpace(TxtFile.Text);

                if (!pingOk) Log("Thiết bị không phản hồi ping");
                else Log($"Kết nối Serial {_serial.PortName} thành công");
            }
            catch (Exception ex)
            {
                _transport = null;
                TryDisconnect();
                MessageBox.Show("Không mở được cổng: " + ex.Message);
            }
        }

        // Thay thế method ConnectTcp trong MainWindow.cs với code sau:

        private void ConnectTcp(byte cabinetAddr)
        {
            var host = TxtIp.Text?.Trim();

            // Kiểm tra port
            if (!int.TryParse(TxtTcpPort.Text, out int tcpPort) || tcpPort < 1 || tcpPort > 65535)
            {
                MessageBox.Show("TCP port không hợp lệ");
                return;
            }

            try
            {
                // Xác định mode dựa vào input
                bool isServerMode = false;
                string displayMode = "Client";

                // Nếu host trống hoặc là các giá trị đặc biệt -> Server mode
                if (string.IsNullOrWhiteSpace(host) ||
                    host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
                    host.Equals("*", StringComparison.OrdinalIgnoreCase) ||
                    host.Equals("Any", StringComparison.OrdinalIgnoreCase) ||
                    host.Equals("Server", StringComparison.OrdinalIgnoreCase))
                {
                    host = "0.0.0.0";  // Force server mode
                    isServerMode = true;
                    displayMode = "Server";
                    Log($"Starting TCP Server on port {tcpPort}, waiting for client connection...");
                }
                else if (host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                         host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    // Hỏi người dùng muốn mode nào
                    var result = MessageBox.Show(
                        "Bạn muốn chạy ở chế độ nào?\n\n" +
                        "YES = TCP Server (thiết bị kết nối vào)\n" +
                        "NO = TCP Client (kết nối đến localhost)\n\n" +
                        "Nếu bạn đang test với thiết bị firmware, chọn YES.",
                        "Chọn chế độ TCP",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel)
                    {
                        return;
                    }
                    else if (result == MessageBoxResult.Yes)
                    {
                        host = "0.0.0.0";  // Server mode
                        isServerMode = true;
                        displayMode = "Server";
                        Log($"Starting TCP Server on port {tcpPort}...");
                    }
                    else
                    {
                        // Keep as localhost for client mode
                        Log($"Connecting to TCP Server at {host}:{tcpPort}...");
                    }
                }
                else
                {
                    // Client mode với IP cụ thể
                    Log($"Connecting to TCP Server at {host}:{tcpPort}...");
                }

                // Tạo transport với timeout phù hợp
                var tcpTx = new TcpEmcTransport(
                    host: host,
                    port: tcpPort,
                    recvTimeoutMs: 2000,
                    sendTimeoutMs: 2000,
                    connectTimeoutMs: 10000 // Server chờ lâu hơn
                );

                // Thông báo cho user biết đang làm gì
                if (isServerMode)
                {
                    Log("TCP Server đang khởi động...");
                    Log($"Đang lắng nghe trên port {tcpPort}");
                    Log("Chờ thiết bị kết nối (timeout 30 giây)...");

                    // Có thể hiển thị IP máy để user biết
                    string localIP = GetLocalIPAddress();
                    if (!string.IsNullOrEmpty(localIP))
                    {
                        Log($"Thiết bị có thể kết nối đến: {localIP}:{tcpPort}");
                    }
                }
                try
                {
                    tcpTx.Open();
                }
                catch (Exception ex)
                {

                    throw;
                }
                // Mở kết nối
              
                _transport = tcpTx;

                // Khởi tạo sender
                _sender = new FirmwareSender(_transport, DefaultBlockSize, DefaultRetries, DefaultAckTimeoutMs, cabinetAddr, 0);
                _sender.LogEmitted += (_, msg) => Dispatcher.Invoke(() => Log(msg));

                TryFlushIo();
                Thread.Sleep(50);  // Cho thiết bị thở

                // Test ping
                bool pingOk = false;
                try
                {
                    Log("Testing connection with PING...");
                    pingOk = _sender.Ping(0x55, DefaultPingTimeoutMs, CancellationToken.None);
                    Log($"PING {(pingOk ? "OK" : "FAIL")}");
                }
                catch (Exception pingEx)
                {
                    Log($"PING error: {pingEx.Message}");
                }

                // Cập nhật UI
                if (isServerMode)
                {
                    TxtConnState.Text = $"TCP Server: Port {tcpPort} (Connected)";
                    Log($"TCP Server ready, client connected from remote");
                }
                else
                {
                    TxtConnState.Text = $"TCP Client: {host}:{tcpPort}";
                    Log($"Connected to TCP server at {host}:{tcpPort}");
                }

                BtnConnect.Content = "Ngắt";
                BtnSend.IsEnabled = pingOk && !string.IsNullOrWhiteSpace(TxtFile.Text);

                if (!pingOk)
                {
                    Log("⚠️ Thiết bị không phản hồi PING");
                    Log("Kiểm tra:");
                    Log("  1. Thiết bị đã kết nối chưa?");
                    Log("  2. Firmware thiết bị có hỗ trợ lệnh PING không?");
                    Log("  3. Baudrate/cấu hình có đúng không?");
                }
                else
                {
                    Log("✓ Kết nối thành công và thiết bị phản hồi PING");
                }
            }
            catch (TimeoutException tex)
            {
                _transport = null;
                TryDisconnect();

                string message = "Timeout kết nối: " + tex.Message;
                if (host == "0.0.0.0")
                {
                    message += "\n\nKhông có thiết bị nào kết nối trong 30 giây.\n" +
                              "Kiểm tra:\n" +
                              "1. Thiết bị đã được cấu hình đúng IP và port chưa?\n" +
                              "2. Firewall có chặn port " + tcpPort + " không?\n" +
                              "3. Thiết bị và máy tính có cùng mạng không?";
                }
                MessageBox.Show(message, "Timeout", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (SocketException sex)
            {
                _transport = null;
                TryDisconnect();

                string message = $"Lỗi socket ({sex.SocketErrorCode}): {sex.Message}";

                if (sex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    message += "\n\nKết nối bị từ chối. ";
                    if (host != "0.0.0.0")
                    {
                        message += "Kiểm tra:\n" +
                                  "1. Server có đang chạy không?\n" +
                                  "2. IP và port có đúng không?\n" +
                                  "3. Firewall có chặn không?\n\n" +
                                  "Mẹo: Nếu muốn app này làm Server, dùng IP: 0.0.0.0";
                    }
                }
                else if (sex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    message += $"\n\nPort {tcpPort} đã được sử dụng bởi chương trình khác.\n" +
                              "Hãy chọn port khác hoặc tắt chương trình đang dùng port này.";
                }

                MessageBox.Show(message, "Lỗi Socket", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                _transport = null;
                TryDisconnect();
                MessageBox.Show($"Lỗi không xác định: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                Log($"Exception: {ex}");
            }
        }

        // Helper method để lấy IP máy local
        private string GetLocalIPAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }
            return "";
        }


        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            if (_transport == null || !_transport.IsOpen)
            {
                MessageBox.Show("Chưa kết nối");
                return;
            }

            var filePath = TxtFile.Text;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show("File không hợp lệ");
                return;
            }

            if (CmbAddr.SelectedItem is not ComboBoxItem addrItem ||
                !byte.TryParse(addrItem.Content?.ToString(), out var cabinetAddr) ||
                cabinetAddr < 1 || cabinetAddr > 6)
            {
                MessageBox.Show("Địa chỉ tủ không hợp lệ (1..6)");
                return;
            }

            BtnSend.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            Prog.Value = 0;
            TxtProgress.Text = "0%";
            _cts = new CancellationTokenSource();

            _sender = new FirmwareSender(
                transport: _transport,
                blockSize: DefaultBlockSize,
                maxRetries: DefaultRetries,
                ackTimeoutMs: DefaultAckTimeoutMs,
                cabinetAddr: cabinetAddr,
                loadAddress: 0
            );

            _sender.LogEmitted += (_, m) => Dispatcher.Invoke(() => Log(m));
            _sender.ProgressChanged += (_, p) => Dispatcher.Invoke(() =>
            {
                Prog.Value = p.Percent;
                TxtProgress.Text = $"{p.Percent:0}% ({p.SentBytes}/{p.TotalBytes} bytes)";
            });

            // pre-ping không dùng token timeout
            if (!_sender.Ping(0x55, DefaultPingTimeoutMs, CancellationToken.None))
            {
                Log("Thiết bị không phản hồi PING. Huỷ gửi.");
                BtnCancel.IsEnabled = false;
                BtnSend.IsEnabled = _transport.IsOpen && !string.IsNullOrWhiteSpace(TxtFile.Text);
                return;
            }
            Log("PING OK. Bắt đầu gửi firmware.");

            try
            {
                var ok = await Task.Run(() => _sender.SendAsyncMce(filePath, _cts.Token));
                Log(ok ? "Hoàn tất gửi firmware." : "Gửi firmware thất bại.");
            }
            catch (OperationCanceledException)
            {
                Log("Đã huỷ theo yêu cầu.");
            }
            catch (Exception ex)
            {
                Log("Lỗi: " + ex.Message);
            }
            finally
            {
                BtnCancel.IsEnabled = false;
                BtnSend.IsEnabled = _transport.IsOpen;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
        }

        private void Log(string msg)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => Log(msg));
                return;
            }

            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            TxtLog.CaretIndex = TxtLog.Text.Length;
            TxtLog.ScrollToEnd();
        }

        private void TryFlushIo()
        {
            try { _transport?.DiscardInBuffer(); } catch { }
            try { _transport?.DiscardOutBuffer(); } catch { }
        }

        private void TryDisconnect()
        {
            try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
            _cts = null;
            try { _transport?.Close(); } catch { }
            _transport = null;
            try { _serial?.Close(); _serial?.Dispose(); } catch { }
            _serial = null;
        }
    }
}
