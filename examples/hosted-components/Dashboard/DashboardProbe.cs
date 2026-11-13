using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;

namespace HostedDashboard;

// `HostedDashboard --probe [--elevated]`: CI's check on a real desktop. The dashboard starts two widget processes; each
// window shows as a component in its region of the dashboard's page, under the page's Content-Security-Policy: the page
// imports the widget's module from the widget's origin and connects it with its component token. The page reports
// whether its engine implements scoped custom element registries (a REGION FINDING line). With --elevated, dashboard
// and widgets run as the elevated pair (CI's Windows runner runs elevated).
internal static class DashboardProbe
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(RunicDesktopHost host, RunicWindowOptions options, RegionFailures failures, bool elevated)
    {
        try
        {
            var finding = await RunCoreAsync(host, options, failures);
            Console.WriteLine($"REGION FINDING: scoped custom element registries {finding}");
            Console.WriteLine($"HOSTED_COMPONENTS_OK|component|component|stop{(elevated ? "|elevated" : "")}");
            return 0;
        }
        catch (ProbeException error)
        {
            Console.Error.WriteLine($"HOSTED_COMPONENTS_FAILED: {error.Message}");
            return 1;
        }
    }

    private static async Task<string> RunCoreAsync(RunicDesktopHost host, RunicWindowOptions options, RegionFailures failures)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var step = "open the dashboard's window";
        try
        {
            await using var window = await host.OpenWindowAsync<DashboardWindow>(options, timeout.Token);
            var dashboard = window.ViewModel;

            step = "start the widgets";
            await dashboard.StartWidgetsAsync(timeout.Token);
            Check(dashboard.Widgets.Count == Guests.Widgets.Count, $"A widget did not start: {dashboard.Indexer}; {dashboard.Sync}.");

            // Attached once the page imported the widget's module, created its element and the element connected.
            foreach (var (name, _) in Guests.Widgets)
            {
                step = $"show the {name} widget as a component";
                var region = await UntilAsync(() => dashboard.Region(name) is { State: RunicHostedRegionState.Attached } attached ? attached : null,
                    failures, timeout.Token);
                Check(region.IsComponent, $"The {name} widget shows in a frame, not as a component.");
            }

            step = "read the page's engine finding";
            var finding = await UntilAsync(() => dashboard.ScopedRegistries is { Length: > 0 } value ? value : null, failures, timeout.Token);

            step = "stop the widgets";
            foreach (var widget in dashboard.Widgets) Check(await widget.StopAsync(timeout.Token) == 0, "A widget did not exit cleanly.");
            foreach (var (name, _) in Guests.Widgets)
            {
                step = $"end the {name} widget's region";
                await UntilAsync(() => dashboard.Region(name) is { State: RunicHostedRegionState.Ended } ended ? ended : null, failures, timeout.Token);
            }
            return finding;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new ProbeException($"Could not {step} within {Wait.TotalSeconds:0} s.");
        }
    }

    private static async Task<T> UntilAsync<T>(Func<T?> read, RegionFailures failures, CancellationToken cancellationToken) where T : class
    {
        while (true)
        {
            if (read() is { } value) return value;
            if (failures.First.IsCompleted)
            {
                var (message, exception) = await failures.First;
                throw new ProbeException($"The dashboard reported: {exception?.Message ?? message}", exception);
            }
            await Task.Delay(50, cancellationToken);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new ProbeException(message);
    }

    private sealed class ProbeException(string message, Exception? inner = null) : Exception(message, inner);
}

// The host reports a region that did not attach (event 2026, DesktopHostedRegionAttachFailed), a region or window that
// failed (2028, 2019) only in its log. The probe reads the log so it can fail with the reason.
internal sealed class RegionFailures : ILoggerProvider
{
    private static readonly HashSet<string> Names = ["DesktopHostedRegionAttachFailed", "DesktopHostedRegionFailed", "DesktopHostedWindowFailed"];
    private readonly TaskCompletionSource<(string Message, Exception? Exception)> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<(string Message, Exception? Exception)> First => _first.Task;

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(RegionFailures owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name is { } name && Names.Contains(name)) owner._first.TrySetResult((formatter(state, exception), exception));
        }
    }
}
