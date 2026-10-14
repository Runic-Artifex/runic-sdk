using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Runic.Application.Views.Desktop;
using TUnit.Core;

// RunicDesktopHost.Run lends the application's ILoggerFactory to the Desktop host for the run. A message after Run
// returned, when the caller disposes its services, or from a factory that was disposed early goes to Trace (#250).
// The case adds a Trace listener; the cases do not run in parallel (AssemblyInfo.cs).
internal sealed class LentLoggerFactoryChecks
{
    [Test, NotInParallel(Order = 0)]
    public void LentFactoryLogsUntilReturnedThenTraces()
    {
        var factory = new RecordingLoggerFactory();
        var lent = new LentLoggerFactory(factory);
        var logger = lent.CreateLogger("Runic.Desktop");
        using var trace = new TraceCapture();
        Trace.Listeners.Add(trace);
        try
        {
            Log(logger, LogLevel.Warning, "while lent");
            Check(factory.Messages.SequenceEqual(["while lent"]) && trace.Text.Length == 0,
                $"A lent logger did not log through the factory: {string.Join(", ", factory.Messages)} / {trace.Text}");

            factory.Disposed = true;
            Log(logger, LogLevel.Error, "after the factory was disposed");
            Check(trace.Text.Contains("after the factory was disposed", StringComparison.Ordinal),
                $"A disposed factory's message did not fall back to Trace: {trace.Text}");

            factory.Disposed = false;
            lent.Return();
            Log(logger, LogLevel.Information, "after Run returned");
            Log(lent.CreateLogger("Runic.Desktop"), LogLevel.Warning, "created after Run returned");
            Check(factory.Messages.Count == 1 && trace.Text.Contains("after Run returned", StringComparison.Ordinal)
                && trace.Text.Contains("created after Run returned", StringComparison.Ordinal),
                $"A returned factory was still used: {string.Join(", ", factory.Messages)} / {trace.Text}");
        }
        finally { Trace.Listeners.Remove(trace); }
    }

    private static void Log(ILogger logger, LogLevel level, string message) =>
        logger.Log(level, new EventId(3008, "BrowserProfileNotDeleted"), message, null, static (text, _) => text);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        internal bool Disposed { get; set; }
        internal List<string> Messages { get; } = [];
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() => Disposed = true;

        private sealed class Logger(RecordingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ObjectDisposedException.ThrowIf(owner.Disposed, owner);
                owner.Messages.Add(formatter(state, exception));
            }
        }
    }

    private sealed class TraceCapture : TraceListener
    {
        private readonly StringBuilder _text = new();
        internal string Text { get { lock (_text) return _text.ToString(); } }
        public override void Write(string? message) { lock (_text) _text.Append(message); }
        public override void WriteLine(string? message) { lock (_text) _text.AppendLine(message); }
    }
}
