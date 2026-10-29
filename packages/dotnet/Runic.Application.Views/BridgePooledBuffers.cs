using System.Buffers;
using System.Text.Json;

namespace Runic.Application.Views;

// The buffers of the Bridge publication path: state and frame pushes, replies and the member encodings they compare.
//
// - BridgePooledWriter: a UTF-8 buffer of segments rented from ArrayPool<byte>.Shared. Segments double from 4 KiB up
//   to 1 MiB, so growing never copies what was written, and a large state does not keep a per-thread buffer alive.
// - BridgeMessage: one push the delivery owns until the transport took it. Its memory is pooled and returned once the
//   broadcast completed; IBridgeTransport.BroadcastAsync promises the memory only until then.
// - BridgeComparingWriter: compares a member's encoding with the bytes last published while it is written, without
//   keeping a copy, and keeps the new bytes only when they differ.
// - BridgePublicationWriter: a pooled writer with its Utf8JsonWriter, cached per thread (nested use rents another).
internal sealed class BridgePooledWriter : IBufferWriter<byte>, IDisposable
{
    private const int FirstSegment = 4 * 1024;
    private const int MaximumSegment = 1024 * 1024;
    private byte[] _current = [];
    private int _used;
    // The full segments before _current, with their lengths.
    private List<(byte[] Array, int Length)>? _full;
    private long _fullLength;

    public long Length => _fullLength + _used;

    public void Advance(int count)
    {
        if ((uint)count > (uint)(_current.Length - _used)) throw new ArgumentOutOfRangeException(nameof(count));
        _used += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _current.AsMemory(_used);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _current.AsSpan(_used);
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;
        if (_current.Length - _used >= sizeHint) return;
        var next = Math.Max(sizeHint, _current.Length == 0 ? FirstSegment : Math.Min(MaximumSegment, _current.Length * 2));
        if (_used == 0)
        {
            if (_current.Length != 0) ArrayPool<byte>.Shared.Return(_current);
        }
        else
        {
            (_full ??= []).Add((_current, _used));
            _fullLength += _used;
        }
        _current = ArrayPool<byte>.Shared.Rent(next);
        _used = 0;
    }

    // Writes bytes as they are.
    public void Write(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var span = GetSpan(Math.Min(bytes.Length, MaximumSegment));
            var count = Math.Min(span.Length, bytes.Length);
            bytes[..count].CopyTo(span);
            Advance(count);
            bytes = bytes[count..];
        }
    }

    public byte[] ToArray() => ToArray(0, Length);

    // A copy of length bytes from start.
    public byte[] ToArray(long start, long length)
    {
        if (length == 0) return [];
        var copy = GC.AllocateUninitializedArray<byte>(checked((int)length));
        CopyTo(start, copy);
        return copy;
    }

    // Whether the bytes from start equal other.
    public bool SequenceEqual(long start, ReadOnlySpan<byte> other)
    {
        var compared = 0;
        foreach (var segment in Segments(start, other.Length))
        {
            if (!segment.SequenceEqual(other.Slice(compared, segment.Length))) return false;
            compared += segment.Length;
        }
        return compared == other.Length;
    }

    public void CopyTo(long start, Span<byte> destination)
    {
        var copied = 0;
        foreach (var segment in Segments(start, destination.Length))
        {
            segment.CopyTo(destination[copied..]);
            copied += segment.Length;
        }
    }

    // The written bytes as one message the caller owns; this writer is empty afterwards. A message in one segment
    // keeps that segment; a longer one is copied into one rented array.
    public BridgeMessage Detach()
    {
        BridgeMessage message;
        if (_full is null)
        {
            message = BridgeMessage.Pooled(_current, _used);
            _current = [];
            _used = 0;
            return message;
        }
        var length = checked((int)Length);
        var array = ArrayPool<byte>.Shared.Rent(length);
        CopyTo(0, array.AsSpan(0, length));
        message = BridgeMessage.Pooled(array, length);
        Reset();
        return message;
    }

    // Empties the writer and returns every segment but the last.
    public void Reset()
    {
        if (_full is not null)
        {
            foreach (var (array, _) in _full) ArrayPool<byte>.Shared.Return(array);
            _full = null;
            _fullLength = 0;
        }
        _used = 0;
    }

    public void Dispose()
    {
        Reset();
        if (_current.Length != 0) ArrayPool<byte>.Shared.Return(_current);
        _current = [];
    }

    // Empties the writer and keeps at most a small segment for the next use.
    public void Trim()
    {
        Reset();
        if (_current.Length > 64 * 1024) Dispose();
    }

    private SegmentEnumerable Segments(long start, int length) => new(this, start, length);

    private readonly ref struct SegmentEnumerable(BridgePooledWriter owner, long start, int length)
    {
        public SegmentEnumerator GetEnumerator() => new(owner, start, length);
    }

    // The spans that cover length bytes from start, segment by segment.
    private ref struct SegmentEnumerator(BridgePooledWriter owner, long start, int length)
    {
        private int _segment = -1;
        private long _offset;
        private long _start = start;
        private int _remaining = length;

        public ReadOnlySpan<byte> Current { get; private set; }

        public bool MoveNext()
        {
            var count = (owner._full?.Count ?? 0) + 1;
            while (_remaining > 0 && ++_segment < count)
            {
                var (array, used) = _segment < count - 1 ? owner._full![_segment] : (owner._current, owner._used);
                var end = _offset + used;
                if (_start < end)
                {
                    var from = (int)(_start - _offset);
                    var take = Math.Min(used - from, _remaining);
                    Current = array.AsSpan(from, take);
                    _start += take;
                    _remaining -= take;
                    _offset = end;
                    return true;
                }
                _offset = end;
            }
            return false;
        }
    }
}

// One push that the delivery owns until the transport took it.
internal sealed class BridgeMessage
{
    private byte[]? _array;
    private readonly bool _pooled;

    private BridgeMessage(byte[] array, int length, bool pooled)
    {
        _array = array;
        Length = length;
        _pooled = pooled;
    }

    public int Length { get; }

    public ReadOnlyMemory<byte> Memory => (_array ?? throw new ObjectDisposedException(nameof(BridgeMessage))).AsMemory(0, Length);

    public static BridgeMessage Pooled(byte[] array, int length) => new(array, length, pooled: true);

    public static BridgeMessage Wrap(byte[] bytes) => new(bytes, bytes.Length, pooled: false);

    // Returns the memory to the pool. The message is not used afterwards.
    public void Release()
    {
        if (Interlocked.Exchange(ref _array, null) is { } array && _pooled) ArrayPool<byte>.Shared.Return(array);
    }
}

// A pooled buffer with its JSON writer. Rent one per message or value; the writer writes into Buffer.
internal sealed class BridgePublicationWriter : IDisposable
{
    [ThreadStatic] private static BridgePublicationWriter? s_cached;

    private BridgePublicationWriter() => Writer = new Utf8JsonWriter(Buffer, BridgeJsonEncoder.WriterOptions);

    public BridgePooledWriter Buffer { get; } = new();

    public Utf8JsonWriter Writer { get; }

    // The number of bytes written so far, including what the writer still holds.
    public long Position
    {
        get
        {
            Writer.Flush();
            return Buffer.Length;
        }
    }

    public static BridgePublicationWriter Rent()
    {
        var cached = s_cached;
        if (cached is null) return new();
        s_cached = null;
        return cached;
    }

    // The written bytes as a push; the writer can be disposed afterwards.
    public BridgeMessage Detach()
    {
        Writer.Flush();
        return Buffer.Detach();
    }

    // The written bytes as an array of their exact length, for a reply the transport may keep.
    public byte[] ToArray()
    {
        Writer.Flush();
        return Buffer.ToArray();
    }

    public void Dispose()
    {
        Writer.Reset(Buffer);
        Buffer.Trim();
        s_cached = this;
    }
}

// Compares the encoding written into it with a baseline as it arrives. While it matches, each chunk is compared and
// reused, so an unchanged member costs no allocation; at the first difference it copies the matched prefix of the
// baseline into a pooled buffer and keeps writing there.
internal sealed class BridgeComparingWriter : IBufferWriter<byte>, IDisposable
{
    private const int ChunkSize = 16 * 1024;
    [ThreadStatic] private static BridgeComparingWriter? s_cached;
    private byte[] _chunk = [];
    private byte[]? _baseline;
    private int _matched;
    private BridgePooledWriter? _diverged;

    private BridgeComparingWriter() => Writer = new Utf8JsonWriter(this, BridgeJsonEncoder.WriterOptions);

    public Utf8JsonWriter Writer { get; }

    public static BridgeComparingWriter Rent(byte[]? baseline)
    {
        var writer = s_cached ?? new BridgeComparingWriter();
        s_cached = null;
        writer._baseline = baseline;
        return writer;
    }

    // Whether what was written equals the baseline. Call after the value was written.
    public bool Matches
    {
        get
        {
            Writer.Flush();
            return _diverged is null && _baseline is not null && _matched == _baseline.Length;
        }
    }

    // The written bytes, when they differ from the baseline.
    public byte[] ToArray()
    {
        Writer.Flush();
        if (_diverged is not null) return _diverged.ToArray();
        // Everything written matched a prefix of the baseline (or there is none): the value is that prefix.
        return _baseline is null || _matched == 0 ? [] : _baseline[.._matched];
    }

    public void Advance(int count)
    {
        if (_diverged is not null)
        {
            _diverged.Advance(count);
            return;
        }
        var written = _chunk.AsSpan(0, count);
        if (_baseline is not null && _baseline.Length - _matched >= count && written.SequenceEqual(_baseline.AsSpan(_matched, count)))
        {
            _matched += count;
            return;
        }
        _diverged = new BridgePooledWriter();
        if (_baseline is not null) _diverged.Write(_baseline.AsSpan(0, _matched));
        _diverged.Write(written);
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (_diverged is not null) return _diverged.GetMemory(sizeHint);
        EnsureChunk(sizeHint);
        return _chunk;
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (_diverged is not null) return _diverged.GetSpan(sizeHint);
        EnsureChunk(sizeHint);
        return _chunk;
    }

    private void EnsureChunk(int sizeHint)
    {
        if (_chunk.Length >= Math.Max(sizeHint, 1)) return;
        if (_chunk.Length != 0) ArrayPool<byte>.Shared.Return(_chunk);
        _chunk = ArrayPool<byte>.Shared.Rent(Math.Max(sizeHint, ChunkSize));
    }

    public void Dispose()
    {
        Writer.Reset(this);
        _diverged?.Dispose();
        _diverged = null;
        _baseline = null;
        _matched = 0;
        if (_chunk.Length > ChunkSize)
        {
            ArrayPool<byte>.Shared.Return(_chunk);
            _chunk = [];
        }
        s_cached = this;
    }
}
