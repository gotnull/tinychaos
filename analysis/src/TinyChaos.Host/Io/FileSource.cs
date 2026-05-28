namespace TinyChaos.Host.Io;

/// <summary>
/// Replay a captured raw binary file as a stream of chunks. Useful for
/// offline replay and for end-to-end CLI smoke tests without hardware.
/// </summary>
public sealed class FileSource : IByteSource
{
    private readonly FileStream _stream;

    public FileSource(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public int Read(Span<byte> buffer) => _stream.Read(buffer);

    public void Dispose() => _stream.Dispose();
}
