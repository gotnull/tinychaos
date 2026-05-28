namespace TinyChaos.Host.Export;

/// <summary>
/// Append the bytes of accepted packets to a file. The file format is the
/// on-wire format, suitable for replay through <see cref="Io.FileSource"/>.
/// </summary>
public sealed class RawBinaryExporter : IDisposable
{
    private readonly FileStream _stream;

    public RawBinaryExporter(string path)
    {
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public void WritePacketBytes(ReadOnlySpan<byte> payload) => _stream.Write(payload);

    public void Dispose()
    {
        _stream.Flush();
        _stream.Dispose();
    }
}
