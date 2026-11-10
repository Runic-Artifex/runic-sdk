using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Platform;

namespace HostedShell;

// `HostedShell --probe`: the shell starts the counter without its own window, and checks on a real desktop that the
// counter's window opens as a native window of the shell with the counter's menu bar, that the bar follows the shell's
// language, and that the counter exits when the shell stops it. The counter's own behaviour has unit tests
// (../Counter/Tests).
internal static class ShellProbe
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(RunicDesktopHost host, HostedWindowFailures failures)
    {
        try
        {
            await RunCoreAsync(host, failures);
            Console.WriteLine("HOSTED_WINDOWS_OK|window|menu-bar|language|stop");
            return 0;
        }
        catch (ProbeException error)
        {
            Console.Error.WriteLine($"HOSTED_WINDOWS_FAILED: {error.Message}");
            return 1;
        }
    }

    private static async Task RunCoreAsync(RunicDesktopHost host, HostedWindowFailures failures)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var step = "set the shell's language";
        try
        {
            var locale = host.Services.GetRequiredService<IRunicApplicationLocale>();
            await locale.SetLocaleAsync("en", timeout.Token);
            var applications = host.HostedApplications;
            var opened = new TaskCompletionSource<RunicHostedWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            applications.WindowOpened += (_, e) => opened.TrySetResult(e.Window);

            step = "start the counter";
            await using var counter = await applications.StartAsync(Guests.Counter, new RunicHostedApplicationOptions { Policy = new CounterPolicy() },
                timeout.Token);

            // The window opens, the shell fails to open it (event 2019, with the reason), or the counter exits first.
            step = "open the counter's window";
            var first = await Task.WhenAny(opened.Task, failures.First, counter.Exited).WaitAsync(timeout.Token);
            if (first == failures.First)
            {
                var failure = await failures.First;
                throw new ProbeException($"The shell could not open the counter's window: {failure.Exception?.Message ?? failure.Message}", failure.Exception);
            }
            if (first == counter.Exited) throw new ProbeException($"The counter exited with {await counter.Exited} before its window opened.");
            var window = await opened.Task;
            Check(window.Type == "CounterTool.CounterWindow" && window.Presentation.IsOpen, $"The counter's {window.Type} is not open in a window of the shell.");

            step = "show the counter's menu bar";
            await WaitForMenuAsync(window, "Counter", timeout.Token);
            step = "switch the counter's menu bar to German";
            await locale.SetLocaleAsync("de", timeout.Token);
            await WaitForMenuAsync(window, "Zähler", timeout.Token);
            step = "switch to the dark color scheme";
            await host.Desktop.SetColorSchemeAsync(DesktopColorScheme.Dark, timeout.Token);

            step = "stop the counter";
            Check(await counter.StopAsync(timeout.Token) == 0, "The counter did not exit cleanly.");
            Check(window.Closed.IsCompleted && !window.Presentation.IsOpen, "The counter's window is open after it stopped.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new ProbeException($"Could not {step} within {Wait.TotalSeconds:0} s.");
        }
    }

    private static async Task WaitForMenuAsync(RunicHostedWindow window, string title, CancellationToken cancellationToken)
    {
        while (window.MenuBar is not [{ Title: var first }, ..] || first != title) await Task.Delay(50, cancellationToken);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new ProbeException(message);
    }

    private sealed class ProbeException(string message, Exception? inner = null) : Exception(message, inner);
}

// The host reports a guest window it could not open only in its log (event 2019, DesktopHostedWindowFailed, with the
// exception) and in its reply to the guest, which keeps running. The probe reads the log so it can fail with the reason.
internal sealed class HostedWindowFailures : ILoggerProvider
{
    private readonly TaskCompletionSource<(string Message, Exception? Exception)> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<(string Message, Exception? Exception)> First => _first.Task;

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(HostedWindowFailures owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "DesktopHostedWindowFailed") owner._first.TrySetResult((formatter(state, exception), exception));
        }
    }
}
