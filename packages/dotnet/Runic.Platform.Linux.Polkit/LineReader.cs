using System.Text;

namespace Runic.Platform.Linux.Polkit;

/// <summary>Splits a pipe into UTF-8 lines with bounded memory.</summary>
internal static class LineReader
{
    /// <summary>The longest line delivered in one piece, in bytes; longer lines arrive in several parts.</summary>
    internal const int MaximumLineBytes = 16 * 1024;

    public static Task PumpAsync(Stream stream, Action<string> line) => Task.Run(() => PumpCoreAsync(stream, line));

    private static async Task PumpCoreAsync(Stream stream, Action<string> deliver)
    {
        var buffer = new byte[MaximumLineBytes];
        var length = 0;
        while (true)
        {
            int read;
            try { read = await stream.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { read = 0; }
            if (read == 0) break;
            length += read;
            var start = 0;
            for (var i = length - read; i < length; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                deliver(Decode(buffer.AsSpan(start, i - start)));
                start = i + 1;
            }
            if (start > 0)
            {
                buffer.AsSpan(start, length - start).CopyTo(buffer);
                length -= start;
            }
            if (length == buffer.Length)
            {
                // Split before an incomplete UTF-8 sequence at the end, so a character is not cut in two.
                var cut = CompleteUtf8Prefix(buffer.AsSpan(0, length));
                deliver(Decode(buffer.AsSpan(0, cut)));
                buffer.AsSpan(cut, length - cut).CopyTo(buffer);
                length -= cut;
            }
        }
        if (length > 0) deliver(Decode(buffer.AsSpan(0, length)));
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
