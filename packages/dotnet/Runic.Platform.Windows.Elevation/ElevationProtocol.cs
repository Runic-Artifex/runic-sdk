using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Runic.Platform.Windows.Elevation;

/// <summary>What the helper reports when it connects, before the action runs.</summary>
internal enum HelperStatus : byte
{
    /// <summary>The helper runs elevated and accepts the action; it waits for <see cref="ElevationProtocol.Go"/>.</summary>
    Accepted = 0,
    /// <summary>The helper does not run elevated: <c>runas</c> started it without UAC, which is off.</summary>
    NotElevated = 1,
    /// <summary>The helper's declarations do not accept the verb and arguments.</summary>
    Rejected = 2,
    /// <summary>The helper runs on a JIT runtime, which reads startup hooks and profilers from the environment.</summary>
    NotNativeAot = 3,
}

/// <summary>
/// The helper's command line and the frames on the pipe between the application (the pipe's server) and the elevated
/// helper (its client).
/// </summary>
/// <remarks>
/// Every frame is a type byte, a little-endian 32-bit payload length and the payload. The helper sends
/// <see cref="Hello"/> (protocol version and <see cref="HelperStatus"/>), then output frames; the application sends
/// <see cref="Go"/> to let an accepted action start, and <see cref="Cancel"/> to ask it to stop. Closing the pipe
/// means the same as <see cref="Cancel"/>, and before <see cref="Go"/> it means the action must not start.
/// </remarks>
internal static class ElevationProtocol
{
    /// <summary>The helper's first argument; the second is the pipe name, then the action's argument vector.</summary>
    public const string Marker = "--runic-elevation";
    public const byte Version = 1;

    public const byte Hello = 0x01, StandardOutput = 0x02, StandardError = 0x03;
    public const byte Go = 0x10, Cancel = 0x11;

    /// <summary>The largest payload either side accepts; output is sent in parts of at most this size.</summary>
    public const int MaximumPayload = 16 * 1024;
    public const int HeaderSize = 5;

    private const string PipePrefix = "runic-elevation-";

    /// <summary>A pipe name with 128 random bits, so no other process can predict and create it first.</summary>
    public static string NewPipeName() => PipePrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Whether <paramref name="name"/> is a name <see cref="NewPipeName"/> makes; the helper opens nothing else.</summary>
    public static bool IsPipeName(string name)
    {
        if (name.Length != PipePrefix.Length + 32 || !name.StartsWith(PipePrefix, StringComparison.Ordinal)) return false;
        foreach (var c in name.AsSpan(PipePrefix.Length))
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>The full path of a pipe name on the local machine.</summary>
    public static string PipePath(string name) => @"\\.\pipe\" + name;

    /// <summary>The helper's arguments: the marker, the pipe name and the action's argument vector.</summary>
    public static IEnumerable<string> HelperArguments(string pipeName, IEnumerable<string> argumentVector) =>
        [Marker, pipeName, .. argumentVector];

    /// <summary>Splits the helper's arguments into the pipe name and the action's argument vector.</summary>
    public static bool TryParseHelperArguments(IReadOnlyList<string> arguments, out string pipeName, out IReadOnlyList<string> argumentVector)
    {
        pipeName = "";
        argumentVector = [];
        if (arguments.Count < 2 || arguments[0] != Marker || !IsPipeName(arguments[1])) return false;
        pipeName = arguments[1];
        argumentVector = arguments.Skip(2).ToArray();
        return true;
    }

    public static void WriteFrame(Stream stream, byte type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPayload) throw new ArgumentOutOfRangeException(nameof(payload));
        // One write per frame, so frames from different threads never interleave below the caller's lock.
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        stream.Write(frame);
        stream.Flush();
    }

    public static void WriteHello(Stream stream, HelperStatus status) => WriteFrame(stream, Hello, [Version, (byte)status]);

    /// <summary>Reads one frame into <paramref name="buffer"/>; null at the end of the stream, also in the middle of a frame.</summary>
    /// <exception cref="InvalidDataException">The frame is longer than <see cref="MaximumPayload"/>.</exception>
    public static async ValueTask<(byte Type, int Length)?> ReadFrameAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        if (await stream.ReadAtLeastAsync(buffer.AsMemory(0, HeaderSize), HeaderSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < HeaderSize)
            return null;
        var type = buffer[0];
        var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(1));
        if (length is < 0 or > MaximumPayload) throw new InvalidDataException($"A frame of {length} bytes is longer than the protocol allows.");
        if (length > 0 && await stream.ReadAtLeastAsync(buffer.AsMemory(0, length), length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < length)
            return null;
        return (type, length);
    }

    /// <summary>The status of a <see cref="Hello"/> payload, or null for another version or a malformed payload.</summary>
    public static HelperStatus? ParseHello(ReadOnlySpan<byte> payload) =>
        payload.Length == 2 && payload[0] == Version && Enum.IsDefined((HelperStatus)payload[1]) ? (HelperStatus)payload[1] : null;
}

/// <summary>Builds a Windows command line that the C runtime and <c>CommandLineToArgvW</c> split back into the same arguments.</summary>
internal static class WindowsCommandLine
{
    /// <summary>The longest command line Windows accepts, in UTF-16 code units, without the terminating NUL.</summary>
    public const int MaximumLength = 32766;

    public static string Build(IEnumerable<string> arguments)
    {
        var line = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (line.Length > 0) line.Append(' ');
            Append(line, argument);
        }
        return line.ToString();
    }

    private static void Append(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            line.Append(argument);
            return;
        }
        line.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == argument.Length)
            {
                // Before the closing quote every backslash is doubled, so none escapes it.
                line.Append('\\', backslashes * 2);
                break;
            }
            if (argument[i] == '"') line.Append('\\', backslashes * 2 + 1).Append('"');
            else line.Append('\\', backslashes).Append(argument[i]);
        }
        line.Append('"');
    }
}

/// <summary>Splits the bytes of one output channel into UTF-8 lines with bounded memory.</summary>
internal sealed class LineSplitter(Action<string> deliver)
{
    /// <summary>The longest line delivered in one piece, in bytes; longer lines arrive in several parts.</summary>
    internal const int MaximumLineBytes = 16 * 1024;

    private readonly byte[] _buffer = new byte[MaximumLineBytes];
    private int _length;

    public void Push(ReadOnlySpan<byte> bytes)
    {
        while (bytes.Length > 0)
        {
            var take = Math.Min(bytes.Length, _buffer.Length - _length);
            bytes[..take].CopyTo(_buffer.AsSpan(_length));
            var start = 0;
            for (var i = _length; i < _length + take; i++)
            {
                if (_buffer[i] != (byte)'\n') continue;
                deliver(Decode(_buffer.AsSpan(start, i - start)));
                start = i + 1;
            }
            _length += take;
            bytes = bytes[take..];
            if (start > 0)
            {
                _buffer.AsSpan(start, _length - start).CopyTo(_buffer);
                _length -= start;
            }
            if (_length == _buffer.Length)
            {
                // Split before an incomplete UTF-8 sequence at the end, so a character is not cut in two.
                var cut = CompleteUtf8Prefix(_buffer.AsSpan(0, _length));
                deliver(Decode(_buffer.AsSpan(0, cut)));
                _buffer.AsSpan(cut, _length - cut).CopyTo(_buffer);
                _length -= cut;
            }
        }
    }

    public void Flush()
    {
        if (_length > 0) deliver(Decode(_buffer.AsSpan(0, _length)));
        _length = 0;
    }

    private static string Decode(ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        return Encoding.UTF8.GetString(line);
    }

    internal static int CompleteUtf8Prefix(ReadOnlySpan<byte> bytes)
    {
        // Walk back over at most three continuation bytes to the lead byte of the last sequence.
        var i = bytes.Length - 1;
        var continuation = 0;
        while (i >= 0 && continuation < 3 && (bytes[i] & 0xC0) == 0x80)
        {
            i--;
            continuation++;
        }
        if (i < 0) return bytes.Length;
        var lead = bytes[i];
        var needed = lead >= 0xF0 ? 3 : lead >= 0xE0 ? 2 : lead >= 0xC0 ? 1 : 0;
        return needed > continuation ? i : bytes.Length;
    }
}
