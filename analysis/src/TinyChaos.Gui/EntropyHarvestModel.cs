using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TinyChaos.Gui;

/// <summary>
/// Detects avalanche spikes above/below a rolling noise baseline and packs
/// each crossing into a bit: above = 1, below = 0. Every 8 bits is committed
/// as a byte written as two uppercase hex characters to an output file.
///
/// Baseline estimation: circular buffer of the most recent <c>window</c>
/// samples provides O(1) rolling mean and standard deviation. A sample is
/// classified as a spike when its Z-score (|sample - mean| / sigma) exceeds
/// <c>threshold</c>. Samples inside the band are skipped silently.
///
/// Output format: uppercase hex pairs separated by spaces, 16 bytes per line.
/// Incomplete trailing bits are discarded on close.
/// </summary>
public sealed class EntropyHarvestModel : IDisposable
{
    private readonly int _window;
    private readonly double _threshold;

    // Circular ring buffer for rolling statistics.
    private readonly double[] _ring;
    private int _ringPos;
    private int _ringCount;
    private double _runSum;
    private double _runSumSq;

    // Bit accumulator: bits packed MSB-first.
    private int _accum;
    private int _bitCount;
    private long _bytesWritten;
    private long _bytesOnCurrentLine;

    private StreamWriter? _writer;

    public bool IsOpen => _writer is not null;

    /// <summary>Bytes committed to the output file so far. Safe to read from any thread.</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public EntropyHarvestModel(int window = 512, double threshold = 2.5)
    {
        if (window < 4) throw new ArgumentOutOfRangeException(nameof(window));
        if (threshold <= 0.0) throw new ArgumentOutOfRangeException(nameof(threshold));
        _window = window;
        _threshold = threshold;
        _ring = new double[window];
    }

    /// <summary>Open <paramref name="path"/> for writing and reset all state.</summary>
    public void Open(string path)
    {
        Close();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(path, append: false, Encoding.ASCII) { AutoFlush = false };

        Array.Clear(_ring, 0, _ring.Length);
        _ringPos = 0;
        _ringCount = 0;
        _runSum = 0.0;
        _runSumSq = 0.0;
        _accum = 0;
        _bitCount = 0;
        Interlocked.Exchange(ref _bytesWritten, 0);
        _bytesOnCurrentLine = 0;
    }

    /// <summary>
    /// Flush and close the output file. Any incomplete partial byte is
    /// discarded — only full bytes are meaningful entropy.
    /// </summary>
    public void Close()
    {
        if (_writer is null) return;
        try
        {
            if (Interlocked.Read(ref _bytesWritten) > 0) _writer.WriteLine();
            _writer.Flush();
        }
        catch { /* swallow */ }
        try { _writer.Dispose(); } catch { /* swallow */ }
        _writer = null;
    }

    /// <summary>
    /// Feed one channel-0 (zener) ADC sample. Must be called from a single
    /// thread (the capture read thread). <see cref="BytesWritten"/> is safe
    /// to read concurrently from the UI thread.
    /// </summary>
    public void Feed(ushort sample)
    {
        double x = sample;

        // Update the circular rolling-stats buffer.
        if (_ringCount < _window)
        {
            _ring[_ringPos] = x;
            _runSum += x;
            _runSumSq += x * x;
            _ringCount++;
        }
        else
        {
            double evicted = _ring[_ringPos];
            _runSum += x - evicted;
            _runSumSq += x * x - evicted * evicted;
            _ring[_ringPos] = x;
        }
        _ringPos = (_ringPos + 1) % _window;

        // Wait for at least half the window before trusting the baseline.
        if (_ringCount < _window / 2) return;

        double mean = _runSum / _ringCount;
        double variance = Math.Max(0.0, _runSumSq / _ringCount - mean * mean);
        double sigma = Math.Sqrt(variance);

        // Degenerate (flat or near-flat signal): no meaningful spike to classify.
        if (sigma < 1.0) return;

        double z = (x - mean) / sigma;
        if (Math.Abs(z) < _threshold) return;

        // Spike: above baseline = 1, below = 0, packed MSB-first.
        _accum = (_accum << 1) | (z > 0.0 ? 1 : 0);
        _bitCount++;
        if (_bitCount == 8) CommitByte();
    }

    private void CommitByte()
    {
        if (_writer is null) { _accum = 0; _bitCount = 0; return; }

        if (_bytesOnCurrentLine > 0 && _bytesOnCurrentLine % 16 == 0)
        {
            _writer.WriteLine();
            _bytesOnCurrentLine = 0;
        }
        else if (_bytesOnCurrentLine > 0)
        {
            _writer.Write(' ');
        }

        _writer.Write(_accum.ToString("X2"));
        _accum = 0;
        _bitCount = 0;
        _bytesOnCurrentLine++;
        long written = Interlocked.Increment(ref _bytesWritten);
        if (written % 64 == 0) _writer.Flush();
    }

    public void Dispose() => Close();
}
