using System.IO.Ports;

namespace TinyChaos.Host.Io;

/// <summary>
/// Live byte source backed by a System.IO.Ports.SerialPort.
///
/// The <paramref name="baudRate"/> argument is forwarded to the underlying
/// driver but is ignored by the transport when the device is a USB CDC
/// virtual serial port. It matters for the UART fallback.
/// </summary>
public sealed class SerialSource : IByteSource
{
    private readonly SerialPort _port;

    public SerialSource(string portName, int baudRate = 921600, int readTimeoutMs = 500)
    {
        _port = new SerialPort(portName, baudRate)
        {
            ReadTimeout = readTimeoutMs,
            DtrEnable = true,
            RtsEnable = true,
        };
        _port.Open();
    }

    public int Read(Span<byte> buffer)
    {
        try
        {
            // SerialPort.Read on a Span: as of .NET 8 there is an overload
            // accepting byte[] but not Span. Allocate a temporary backing
            // array; the cost is dominated by the actual I/O.
            var tmp = new byte[buffer.Length];
            int n = _port.Read(tmp, 0, tmp.Length);
            tmp.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        catch (TimeoutException)
        {
            return 0; // signal "no data this tick"; caller can loop
        }
    }

    public void Dispose()
    {
        try { _port.Close(); } catch { /* swallow */ }
        _port.Dispose();
    }
}
