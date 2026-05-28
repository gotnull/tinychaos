using System.Buffers.Binary;

namespace TinyChaos.Protocol;

/// <summary>
/// Streaming packet framer.
///
/// Turns a byte chunk feed into a sequence of <see cref="IFrameEvent"/>
/// items. Tolerates arbitrary corruption: leading garbage, mid-packet bit
/// flips, magic bytes flipped, packet truncation, doubled magics.
///
/// Correctness properties (mirrored from the Python reference test suite):
///
/// <list type="bullet">
///   <item>Feeding the same byte stream in one-byte chunks, fixed chunks,
///   or random chunks yields the same event sequence.</item>
///   <item>The framer never emits a <see cref="PacketReceivedEvent"/> for a
///   packet whose CRC is invalid. If a bit flips, the framer either emits
///   <see cref="BadCrcEvent"/>, <see cref="BadVersionEvent"/>, or treats
///   the bytes as resync skip.</item>
///   <item>After any number of bad bytes, the framer eventually resyncs
///   onto the next valid packet, provided one appears.</item>
/// </list>
/// </summary>
public sealed class Framer
{
    private enum State
    {
        SearchMagic0,
        SearchMagic1,
        ReadHeader,
        ReadBody,
        ReadCrc,
    }

    private State _state = State.SearchMagic0;
    private int _skippedBytes;
    private readonly List<byte> _headerBuf = new(ProtocolConstants.HeaderSize - 2);
    private readonly List<byte> _bodyBuf = new();
    private readonly List<byte> _crcBuf = new(2);
    private PacketHeader _header;
    private int _bodyTarget;
    private readonly Queue<byte> _pending = new();
    private IFrameEvent? _deferredEvent;

    /// <summary>
    /// Feed a chunk of bytes and yield any events produced.
    /// </summary>
    public IEnumerable<IFrameEvent> Feed(ReadOnlyMemory<byte> chunk)
    {
        // Enqueue without holding a Span in a local. Iterator methods in
        // C# 12 cannot have ref-struct locals (the iterator state machine
        // would need to persist them across yield boundaries). Accessing
        // `chunk.Span[i]` returns a byte for each index without creating a
        // Span local.
        for (int i = 0; i < chunk.Length; i++)
        {
            _pending.Enqueue(chunk.Span[i]);
        }

        while (true)
        {
            if (_deferredEvent is not null)
            {
                var ev = _deferredEvent;
                _deferredEvent = null;
                yield return ev;
                continue;
            }

            if (_pending.Count == 0 && _state == State.SearchMagic0)
            {
                yield break;
            }

            IFrameEvent? produced = Step();
            if (produced is not null)
            {
                yield return produced;
                continue;
            }

            if (_pending.Count == 0)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Force emission of any pending <see cref="ResyncDroppedEvent"/>.
    /// Useful at end-of-stream so the consumer sees trailing skipped bytes.
    /// </summary>
    public ResyncDroppedEvent? FlushResync()
    {
        if (_skippedBytes <= 0) return null;
        var ev = new ResyncDroppedEvent(_skippedBytes);
        _skippedBytes = 0;
        return ev;
    }

    private IFrameEvent? Step() => _state switch
    {
        State.SearchMagic0 => StepSearchMagic0(),
        State.SearchMagic1 => StepSearchMagic1(),
        State.ReadHeader => StepReadHeader(),
        State.ReadBody => StepReadBody(),
        State.ReadCrc => StepReadCrc(),
        _ => throw new InvalidOperationException($"unreachable: {_state}"),
    };

    private IFrameEvent? StepSearchMagic0()
    {
        while (_pending.Count > 0)
        {
            byte b = _pending.Peek();
            if (b == ProtocolConstants.Magic0)
            {
                _pending.Dequeue();
                _state = State.SearchMagic1;
                return null;
            }
            _skippedBytes++;
            _pending.Dequeue();
        }
        return null;
    }

    private IFrameEvent? StepSearchMagic1()
    {
        if (_pending.Count == 0) return null;
        byte b = _pending.Peek();
        if (b == ProtocolConstants.Magic1)
        {
            _pending.Dequeue();
            _state = State.ReadHeader;
            _headerBuf.Clear();
            return null;
        }
        // Magic0 was a false alarm. Do not consume the current byte; it
        // might itself be a Magic0.
        _skippedBytes++;
        _state = State.SearchMagic0;
        return null;
    }

    private IFrameEvent? StepReadHeader()
    {
        int needed = (ProtocolConstants.HeaderSize - 2) - _headerBuf.Count;
        while (needed > 0 && _pending.Count > 0)
        {
            _headerBuf.Add(_pending.Dequeue());
            needed--;
        }
        if (_headerBuf.Count < ProtocolConstants.HeaderSize - 2) return null;

        var span = (ReadOnlySpan<byte>)_headerBuf.ToArray();
        byte version = span[0];
        byte flags = span[1];
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(2, 4));
        uint timeUs = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(6, 4));
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10, 2));

        if (version != ProtocolConstants.ProtocolVersion)
        {
            var ev = new BadVersionEvent(version);
            _skippedBytes += 2 + _headerBuf.Count;
            ResetToSearch();
            return ev;
        }

        if (count > ProtocolConstants.MaxSampleCount)
        {
            _skippedBytes += 2 + _headerBuf.Count;
            ResetToSearch();
            return null;
        }

        _header = new PacketHeader(version, flags, seq, timeUs, count);
        _bodyTarget = 2 * count;
        _bodyBuf.Clear();
        _state = State.ReadBody;
        return null;
    }

    private IFrameEvent? StepReadBody()
    {
        if (_bodyTarget == 0)
        {
            _state = State.ReadCrc;
            _crcBuf.Clear();
            return null;
        }
        while (_bodyBuf.Count < _bodyTarget && _pending.Count > 0)
        {
            _bodyBuf.Add(_pending.Dequeue());
        }
        if (_bodyBuf.Count < _bodyTarget) return null;
        _state = State.ReadCrc;
        _crcBuf.Clear();
        return null;
    }

    private IFrameEvent? StepReadCrc()
    {
        while (_crcBuf.Count < 2 && _pending.Count > 0)
        {
            _crcBuf.Add(_pending.Dequeue());
        }
        if (_crcBuf.Count < 2) return null;

        // CRC over VERSION through last sample.
        var crcInput = new byte[_headerBuf.Count + _bodyBuf.Count];
        Array.Copy(_headerBuf.ToArray(), 0, crcInput, 0, _headerBuf.Count);
        Array.Copy(_bodyBuf.ToArray(), 0, crcInput, _headerBuf.Count, _bodyBuf.Count);
        ushort crcComputed = Crc16.Compute(crcInput);
        ushort crcReceived = BinaryPrimitives.ReadUInt16LittleEndian(_crcBuf.ToArray());

        IFrameEvent? resyncEvent = null;
        if (_skippedBytes > 0)
        {
            resyncEvent = new ResyncDroppedEvent(_skippedBytes);
            _skippedBytes = 0;
        }

        IFrameEvent outcome;
        if (crcReceived != crcComputed)
        {
            outcome = new BadCrcEvent(_header, crcComputed, crcReceived);
        }
        else
        {
            // Build samples array from the body.
            var samples = new ushort[_header.Count];
            var bodyArr = _bodyBuf.ToArray();
            for (int i = 0; i < _header.Count; i++)
            {
                samples[i] = BinaryPrimitives.ReadUInt16LittleEndian(
                    new ReadOnlySpan<byte>(bodyArr, 2 * i, 2));
            }
            outcome = new PacketReceivedEvent(new Packet(_header, samples));
        }

        ResetToSearch();

        if (resyncEvent is not null)
        {
            _deferredEvent = outcome;
            return resyncEvent;
        }
        return outcome;
    }

    private void ResetToSearch()
    {
        _state = State.SearchMagic0;
        _headerBuf.Clear();
        _bodyBuf.Clear();
        _crcBuf.Clear();
        _header = default;
        _bodyTarget = 0;
    }
}
