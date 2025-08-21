using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Desktop_Firmware_Testing
{
    public interface IEmcTransport : IDisposable
    {
        bool IsOpen { get; }
        int BytesToRead { get; }
        void Open();
        void Close();
        void DiscardInBuffer();
        void DiscardOutBuffer();
        int Read(byte[] buffer, int offset, int count);   
        int ReadByte();
        void Write(byte[] buffer, int offset, int count);
    }
}
