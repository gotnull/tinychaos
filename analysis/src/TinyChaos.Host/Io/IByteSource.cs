namespace TinyChaos.Host.Io;

/// <summary>
/// A byte-chunk source. Either a live serial port (<see cref="SerialSource"/>)
/// or a captured binary file (<see cref="FileSource"/>).
/// </summary>
public interface IByteSource : IDisposable
{
    /// <summary>
    /// Read up to <paramref name="buffer"/>.Length bytes. Returns the number
    /// of bytes read. Returns 0 only at end of stream; serial sources block
    /// (with a timeout) until at least one byte is available or the stream
    /// is closed.
    /// </summary>
    int Read(Span<byte> buffer);
}
