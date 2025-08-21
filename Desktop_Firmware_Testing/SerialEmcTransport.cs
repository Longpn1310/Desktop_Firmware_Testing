using System;
using System.IO.Ports;

namespace Desktop_Firmware_Testing
{
    public sealed class SerialEmcTransport : IEmcTransport
    {
        private readonly SerialPort _port;

        public SerialEmcTransport(SerialPort port)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
        }

        public bool IsOpen => _port.IsOpen;
        public int BytesToRead => _port.BytesToRead;

        public void Open()
        {
            if (!_port.IsOpen) _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
        }

        public void Close()
        {
            try { if (_port.IsOpen) _port.Close(); } catch { }
        }

        public void DiscardInBuffer() { try { _port.DiscardInBuffer(); } catch { } }
        public void DiscardOutBuffer() { try { _port.DiscardOutBuffer(); } catch { } }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_port.BytesToRead <= 0) return 0;
            try { return _port.Read(buffer, offset, count); }
            catch (TimeoutException) { return 0; }
        }

        public int ReadByte()
        {
            if (_port.BytesToRead <= 0) return -1;
            try { return _port.ReadByte(); }
            catch (TimeoutException) { return -1; }
        }

        public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);

        public void Dispose() => Close();
        public override string ToString() => $"Serial({_port.PortName})";
    }
}
