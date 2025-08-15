using Microsoft.Win32;
using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Desktop_Firmware_Testing
{
    public partial class MainWindow : Window
    {
        private SerialPort? _port;
        private FirmwareSender? _sender;
        private CancellationTokenSource? _cts;

        public MainWindow()
        {
            InitializeComponent();
            RefreshPorts();
            TxtLog.Text = "";
            TxtConnState.Text = "Chưa kết nối";
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
                BtnSend.IsEnabled = _port?.IsOpen == true;
                Log($"Chọn file: {Path.GetFileName(dlg.FileName)}");
            }
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_port?.IsOpen == true)
            {
                try { _port.Close(); } catch { }
                _port = null;
                TxtConnState.Text = "Chưa kết nối";
                BtnConnect.Content = "Kết nối";
                BtnSend.IsEnabled = false;
                Log("Đã ngắt kết nối");
                return;
            }

            if (CmbPorts.SelectedItem is not string portName)
            {
                MessageBox.Show("Chọn cổng COM");
                return;
            }
            int baud = int.Parse(((ComboBoxItem)CmbBaud.SelectedItem!).Content!.ToString()!);

            _port = new SerialPort(portName)
            {
                BaudRate = baud,
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                ReadTimeout = 2000,
                WriteTimeout = 2000,
                DtrEnable = true,
                RtsEnable = false,
                ReceivedBytesThreshold = 1
            };

            try
            {
                _port.Open();
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                TxtConnState.Text = $"Kết nối: {portName} @ {baud}";
                BtnConnect.Content = "Ngắt";
                BtnSend.IsEnabled = !string.IsNullOrWhiteSpace(TxtFile.Text);
                Log($"Kết nối {portName} thành công");
            }
            catch (Exception ex)
            {
                _port = null;
                MessageBox.Show("Không mở được cổng: " + ex.Message);
            }
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            if (_port == null || !_port.IsOpen)
            {
                MessageBox.Show("Chưa kết nối COM");
                return;
            }

            var filePath = TxtFile.Text; // chụp trên UI thread
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show("File không hợp lệ");
                return;
            }

            // cấu hình gửi
            ushort chunk = 1024;       // đổi nếu cần
            byte cabinetAddr = 1;      // 1..4. thêm ô nhập nếu muốn

            BtnSend.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            Prog.Value = 0;
            TxtProgress.Text = "0%";
            _cts = new CancellationTokenSource();

            _sender = new FirmwareSender(
                _port,
                blockSize: chunk,
                maxRetries: 5,
                ackTimeoutMs: 2000,
                cabinetAddr: cabinetAddr,
                loadAddress: 0 // không dùng với CMD 0xFC
            );

            _sender.LogEmitted += (_, msg) => Dispatcher.Invoke(() => Log(msg));
            _sender.ProgressChanged += (_, p) => Dispatcher.Invoke(() =>
            {
                Prog.Value = p.Percent;
                TxtProgress.Text = $"{p.Percent:0}% ({p.SentBytes}/{p.TotalBytes} bytes)";
            });

            try
            {
                var ok = await Task.Run(() => _sender.SendAsync(filePath, _cts.Token));
                Log(ok ? "Hoàn tất" : "Thất bại");
            }
            catch (OperationCanceledException)
            {
                Log("Đã huỷ theo yêu cầu");
            }
            catch (Exception ex)
            {
                Log("Lỗi: " + ex.Message);
            }
            finally
            {
                BtnCancel.IsEnabled = false;
                BtnSend.IsEnabled = true;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void Log(string msg)
        {
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            TxtLog.ScrollToEnd();
        }
    }
}
