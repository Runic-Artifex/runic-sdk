using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Runic.Application.Logging.Tests;

// A Runic application wired the way a production application is: a ServiceCollection with Serilog behind
// Microsoft.Extensions.Logging, the generated AddRunicViews(), and RunicDesktopHost.Run with default options.
// Nothing passes a logger to Runic. Serilog writes to an in-memory sink and a TraceListener records
// System.Diagnostics.Trace; when the application ends, both are written to standard output for the test:
//   SERILOG <tab> category <tab> level <tab> event ID <tab> event name <tab> Code property <tab> message
//   TRACE <tab> message
internal static class LoggingApplication
{
    internal const string Argument = "--run-logging-application";

    // With RUNIC_TEST_CHILD_PROCESS set, the application runs a child process, this executable with
    // ChildProcessArgument, while its window is open. It ends the child when the window closes, or leaves it running
    // with the value "leak".
    internal const string ChildProcessVariable = "RUNIC_TEST_CHILD_PROCESS";
    internal const string ChildProcessArgument = "--run-child-process";

    // The window options the application checks at startup. X without Y is incomplete, so Runic Desktop
    // reports window-option-incomplete (event 3003) on every platform.
    internal static readonly Runic.Desktop.DesktopWindowOptions CheckedOptions = RunicDesktopHost.DefaultWindowOptions with { X = 100 };

    public static int Run()
    {
        var trace = new RecordingTraceListener();
        Trace.Listeners.Add(trace);
        var sink = new InMemorySink();
        int exitCode;
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddSerilog(
            new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger(), dispose: true));
        services.AddScoped<LoggingViewModel>();
        services.AddRunicViews();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        try
        {
            // Serve-only mode (RUNIC_APPLICATION_SERVE_ONLY=1) serves the window's surface until a line is read
            // from standard input or shutdown is requested, for example with SIGTERM.
            exitCode = RunicDesktopHost.Run(provider, async host =>
            {
                host.Desktop.Validate(CheckedOptions);
                // The recommended startup check, which serve-only mode passes without a display or WebKitGTK (#238).
                host.ValidateWindow<LoggingWindow>().ThrowIfInvalid();
                using var child = StartChildProcess();
                await using (var window = await host.OpenWindowAsync<LoggingWindow>())
                    await window.WaitForCloseAsync();
                if (child is not null && Environment.GetEnvironmentVariable(ChildProcessVariable) != "leak")
                {
                    child.Kill();
                    await child.WaitForExitAsync();
                }
                return 0;
            });
        }
        finally
        {
            // Disposing the provider disposes the Serilog logger, which flushes the sink.
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Trace.Listeners.Remove(trace);
        }
        foreach (var logEvent in sink.Events) Console.Out.WriteLine(Describe(logEvent));
        foreach (var message in trace.Messages) Console.Out.WriteLine($"TRACE\t{OneLine(message)}");
        Console.Out.Flush();
        return exitCode;
    }

    private static Process? StartChildProcess()
    {
        if (Environment.GetEnvironmentVariable(ChildProcessVariable) is not { Length: > 0 }) return null;
        var start = SelfStartInfo(ChildProcessArgument);
        // The child does not hold the application's standard streams.
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.Environment.Remove(ChildProcessVariable);
        return Process.Start(start);
    }

    // Starts this test executable, or dotnet and the test assembly when the suite runs through dotnet exec.
    internal static ProcessStartInfo SelfStartInfo(string argument)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The test process has no path.");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(argument);
        return start;
    }

    private static string Describe(LogEvent logEvent)
    {
        var category = Scalar(logEvent, "SourceContext");
        string? id = null, name = null;
        if (logEvent.Properties.TryGetValue("EventId", out var eventId) && eventId is StructureValue structure)
        {
            id = structure.Properties.FirstOrDefault(property => property.Name == "Id")?.Value is ScalarValue { Value: { } value }
                ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;
            name = structure.Properties.FirstOrDefault(property => property.Name == "Name")?.Value is ScalarValue { Value: string text }
                ? text : null;
        }
        return string.Join('\t', "SERILOG", category, logEvent.Level, id, name, Scalar(logEvent, "Code"),
            OneLine(logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string? Scalar(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) && value is ScalarValue { Value: { } scalar }
            ? scalar.ToString() : null;

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Replace('\t', ' ');

    private sealed class InMemorySink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_events) return [.. _events]; }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    // Records every Trace message, including Trace.TraceInformation, as one line each.
    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];
        private string _pending = "";

        public IReadOnlyList<string> Messages
        {
            get { lock (_gate) return [.. _messages, .. _pending.Length > 0 ? [_pending] : Array.Empty<string>()]; }
        }

        public override void Write(string? message)
        {
            lock (_gate) _pending += message;
        }

        public override void WriteLine(string? message)
        {
            lock (_gate)
            {
                _messages.Add(_pending + message);
                _pending = "";
            }
        }
    }
}
