using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Runic.Application.Tool;

/// <summary>
/// Collapses identical output lines of a child process (devtools-library-dx §3.5): a line that was printed less than
/// <see cref="Window"/> ago is held back and counted, and <see cref="Flush"/> prints one summary per held line, with its
/// count. Vite logged one unresolved import 1,107 times in 30 s (F6). Lines compare without ANSI colours and without a
/// leading clock time, such as Vite's <c>10:42:07 AM</c>. Thread-safe: a process's standard output and error call it from
/// two threads.
/// </summary>
internal sealed partial class RepeatedLineFilter : IDisposable
{
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    /// <summary>The summaries that one flush prints at most; the rest are counted in one line.</summary>
    internal const int SummaryLines = 3;

    private readonly object _gate = new();
    private readonly string _label;
    private readonly Action<string> _write;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Entry> _lines = new(StringComparer.Ordinal);
    private readonly ITimer? _timer;

    internal RepeatedLineFilter(string label, Action<string> write, TimeProvider? time = null, bool flushPeriodically = true)
    {
        _label = label;
        _write = write;
        _time = time ?? TimeProvider.System;
        _timer = flushPeriodically ? _time.CreateTimer(_ => Flush(), null, FlushInterval, FlushInterval) : null;
    }

    /// <summary>Whether to hold <paramref name="line"/> back: an identical line was printed within <see cref="Window"/>.</summary>
    internal bool Suppress(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string key = Key(line);
        if (key.Length == 0) return false;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_lines.TryGetValue(key, out Entry? entry) && now - entry.Printed < Window)
            {
                entry.Held++;
                return true;
            }
            if (entry is null)
            {
                // Forget lines that have not been printed for a while, so a long session keeps a small table.
                if (_lines.Count >= 512)
                {
                    foreach (string old in _lines.Where(pair => pair.Value.Held == 0 && now - pair.Value.Printed >= Window).Select(static pair => pair.Key).ToList())
                        _lines.Remove(old);
                }
                _lines[key] = new Entry(line, now);
            }
            else
            {
                entry.Printed = now;
            }
            return false;
        }
    }

    /// <summary>Prints a summary of the held lines and starts counting again.</summary>
    internal void Flush()
    {
        List<(string Line, int Count)> held;
        lock (_gate)
        {
            held = [.. _lines.Values.Where(static entry => entry.Held > 0).OrderByDescending(static entry => entry.Held)
                .Select(static entry => (entry.Line, entry.Held))];
            foreach (Entry entry in _lines.Values) entry.Held = 0;
        }
        foreach ((string line, int count) in held.Take(SummaryLines))
        {
            _write($"[{_label}] (repeated {Times(count)}) {Key(line)}");
        }
        if (held.Count > SummaryLines)
        {
            int rest = held.Skip(SummaryLines).Sum(static pair => pair.Count);
            int kinds = held.Count - SummaryLines;
            _write($"[{_label}] (and {rest.ToString("N0", CultureInfo.InvariantCulture)} repeats of {kinds.ToString(CultureInfo.InvariantCulture)} other line{(kinds == 1 ? "" : "s")})");
        }
    }

    private static string Times(int count) =>
        count == 1 ? "once more" : count.ToString("N0", CultureInfo.InvariantCulture) + " more times";

    /// <summary>The line without ANSI escapes, a leading clock time and surrounding space.</summary>
    internal static string Key(string line) => LeadingTime().Replace(Strip(line), string.Empty).Trim();

    private static string Strip(string line) => AnsiEscape().Replace(line, string.Empty);

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"^\s*\d{1,2}:\d{2}:\d{2}(?:\.\d+)?(?:\s*[AP]M)?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingTime();

    public void Dispose()
    {
        _timer?.Dispose();
        Flush();
    }

    private sealed class Entry(string line, DateTimeOffset printed)
    {
        internal string Line { get; } = line;
        internal DateTimeOffset Printed { get; set; } = printed;
        internal int Held { get; set; }
    }
}

/// <summary>
/// The first line of a process's output that reports an error (it says "error", without ANSI colours and clock time),
/// for a failure message that names the cause instead of only an exit code or a timeout.
/// </summary>
internal sealed class FirstErrorLine
{
    private string? _line;

    internal string? Line => Volatile.Read(ref _line);

    internal void Observe(string line)
    {
        if (Volatile.Read(ref _line) is not null || line is null) return;
        string key = RepeatedLineFilter.Key(line);
        if (key.Length == 0 || key.Length > 2_000 || !key.Contains("error", StringComparison.OrdinalIgnoreCase)) return;
        Interlocked.CompareExchange(ref _line, key, null);
    }
}
