using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// The CDP adapter's traffic, written to the file that <c>RUNIC_DEV_CDP_TRACE</c> names, for reporting a debugger that
/// does not work with it. Off unless set; the file holds the page's data, so it is for local diagnosis only, and on Linux
/// and macOS dev creates it readable and writable by its user only (0600). Script sources and inline source maps made it
/// grow by hundreds of megabytes in minutes on a large page (#1065), so a JSON string longer than
/// <see cref="LongStringLimit"/> keeps only its start, unless <c>RUNIC_DEV_CDP_TRACE_SOURCES=1</c>.
/// </summary>
internal static class CdpTrace
{
    internal const string Variable = "RUNIC_DEV_CDP_TRACE";
    internal const string SourcesVariable = "RUNIC_DEV_CDP_TRACE_SOURCES";
    internal const int LongStringLimit = 4096;
    private const int KeptStart = 200;
    private static readonly object Gate = new();
    private static readonly string? Path = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path ? path : null;
    private static readonly bool Sources = Environment.GetEnvironmentVariable(SourcesVariable) == "1";

    internal static void Write(string channel, string text)
    {
        if (Path is null) return;
        if (!Sources) text = ShortenLongStrings(text);
        string line = $"{DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {channel} {text}\n";
        lock (Gate)
        {
            try
            {
                Append(Path, line);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// <paramref name="text"/> with each JSON string longer than <see cref="LongStringLimit"/> characters (as written, with
    /// its escapes) cut to its first characters and a note of how many were left out. A string inside a string (a nested,
    /// escaped message) counts as part of the outer one. Text that is not JSON passes through unless it has a quote.
    /// </summary>
    internal static string ShortenLongStrings(string text)
    {
        if (text.Length <= LongStringLimit) return text;
        StringBuilder? shortened = null;
        int copied = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '"') continue;
            int start = index + 1;
            int end = start;
            while (end < text.Length && text[end] != '"') end += text[end] == '\\' ? 2 : 1;
            if (end >= text.Length) break;
            int length = end - start;
            if (length > LongStringLimit)
            {
                // Whole escape sequences only, such as \" or \u00e9.
                int kept = start;
                while (kept < start + KeptStart) kept += text[kept] != '\\' ? 1 : text[kept + 1] == 'u' ? 6 : 2;
                int keep = kept - start;
                shortened ??= new StringBuilder(text.Length / 8);
                shortened.Append(text, copied, start + keep - copied);
                shortened.Append(CultureInfo.InvariantCulture, $"…[{length - keep} characters left out; {SourcesVariable}=1 keeps them]");
                copied = end;
            }
            index = end;
        }
        if (shortened is null) return text;
        shortened.Append(text, copied, text.Length - copied);
        return shortened.ToString();
    }

    internal static void Append(string path, string text)
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(Encoding.UTF8.GetBytes(text));
    }
}
