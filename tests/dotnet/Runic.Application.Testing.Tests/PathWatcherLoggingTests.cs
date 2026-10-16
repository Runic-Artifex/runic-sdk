using System.Collections.Concurrent;
using System.Diagnostics;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class PathWatcherLoggingTests
{
    // A directory an application creates on its first start is not a problem to warn about (#312): without a
    // failed callback the watcher polls for it and logs at Debug level, which the Trace fallback leaves out.
    [Test]
    public async Task AMissingDirectoryIsNotAWarning()
    {
        var root = Directory.CreateTempSubdirectory("runic-watch-log-").FullName;
        var trace = new TraceCapture();
        Trace.Listeners.Add(trace);
        try
        {
            var state = Path.Combine(root, "state");
            await using var context = new RunicModelContext();
            var tester = new RunicPathWatchTester();
            var target = RunicWatchTarget.File(Path.Combine(state, "draft.json"));
            using (RunicPathWatcher.Start(context, [target], _ => { }, options: tester.Options))
                await tester.DrainAsync();
            Require(!trace.Entries.Any(entry => entry.Contains(state, StringComparison.Ordinal)),
                $"A missing directory was written to Trace: {string.Join(" | ", trace.Entries)}");
        }
        finally
        {
            Trace.Listeners.Remove(trace);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TraceCapture : TraceListener
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
            Entries.Enqueue(message ?? string.Empty);
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id,
            string? format, params object?[]? args) =>
            Entries.Enqueue(args is null ? format ?? string.Empty : string.Format(System.Globalization.CultureInfo.InvariantCulture, format ?? string.Empty, args));
        public override void Write(string? message) => Entries.Enqueue(message ?? string.Empty);
        public override void WriteLine(string? message) => Entries.Enqueue(message ?? string.Empty);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
