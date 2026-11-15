using System;
using System.Globalization;
using System.IO;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// The CDP adapter's traffic, written to the file that <c>RUNIC_DEV_CDP_TRACE</c> names, for reporting a debugger that
/// does not work with it. Off unless set; the file holds the page's data, so it is for local diagnosis only.
/// </summary>
internal static class CdpTrace
{
    internal const string Variable = "RUNIC_DEV_CDP_TRACE";
    private static readonly object Gate = new();
    private static readonly string? Path = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path ? path : null;

    internal static void Write(string channel, string text)
    {
        if (Path is null) return;
        string line = $"{DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {channel} {text}\n";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path, line);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
