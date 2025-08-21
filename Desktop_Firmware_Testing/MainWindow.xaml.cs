using Microsoft.Win32;
using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
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

        private void ConnectTcp(byte cabinetAddr)
        {
            var host = TxtIp.Text?.Trim();
            if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                MessageBox.Show("Host/IP không hợp lệ");
                return;
            }
            if (!int.TryParse(TxtTcpPort.Text, out int tcpPort) || tcpPort < 1 || tcpPort > 65535)
            {
                MessageBox.Show("TCP port không hợp lệ");
                return;
            }

            try
            {
                // tăng recvTimeout để tránh false timeout trên TCP
                var tcpTx = new TcpEmcTransport(host, tcpPort, recvTimeoutMs: 500, sendTimeoutMs: 2000);
                tcpTx.Open();
                _transport = tcpTx;

                _sender = new FirmwareSender(_transport, DefaultBlockSize, DefaultRetries, DefaultAckTimeoutMs, cabinetAddr, 0);
                _sender.LogEmitted += (_, msg) => Dispatcher.Invoke(() => Log(msg));

                TryFlushIo();
                bool pingOk = _sender.Ping(0x55, DefaultPingTimeoutMs, CancellationToken.None);
                Log($"PING {(pingOk ? "OK" : "FAIL")}");

                TxtConnState.Text = $"TCP: {host}:{tcpPort}";
                BtnConnect.Content = "Ngắt";
                BtnSend.IsEnabled = pingOk && !string.IsNullOrWhiteSpace(TxtFile.Text);

                if (!pingOk) Log("Thiết bị không phản hồi ping");
                else Log($"Kết nối TCP {host}:{tcpPort} thành công");
            }
            catch (Exception ex)
            {
                _transport = null;
                TryDisconnect();
                MessageBox.Show("Không kết nối TCP được: " + ex.Message);
            }
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
