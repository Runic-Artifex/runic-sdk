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

    public static async Task<int> RunAsync(RunicDesktopHost host, RunicWindowOptions options, ProbeLog failures, bool elevated)
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
            if (error.InnerException is { } inner) Console.Error.WriteLine(inner);
            return 1;
        }
        catch (Exception error)
        {
            // Any other failure is the probe's too, and says what happened.
            Console.Error.WriteLine($"HOSTED_COMPONENTS_FAILED: {error}");
            return 1;
        }
    }

    private static async Task<string> RunCoreAsync(RunicDesktopHost host, RunicWindowOptions options, ProbeLog failures)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var step = "";
        void At(string name)
        {
            step = name;
            ProbeLog.Write($"step: {name}");
        }
        At("open the dashboard's window");
        try
        {
            await using var window = await host.OpenWindowAsync<DashboardWindow>(options, timeout.Token);
            var dashboard = window.ViewModel;

            At("start the widgets");
            await dashboard.StartWidgetsAsync(timeout.Token);
            Check(dashboard.Widgets.Count == Guests.Widgets.Count, $"A widget did not start: {dashboard.Indexer}; {dashboard.Sync}.");

            // Attached once the page imported the widget's module, created its element and the element connected.
            foreach (var (name, _) in Guests.Widgets)
            {
                At($"show the {name} widget as a component");
                var region = await UntilAsync(() => dashboard.Region(name) is { State: RunicHostedRegionState.Attached } attached ? attached : null,
                    failures, timeout.Token);
                Check(region.IsComponent, $"The {name} widget shows in a frame, not as a component.");
            }

            At("read the page's engine finding");
            var finding = await UntilAsync(() => dashboard.ScopedRegistries is { Length: > 0 } value ? value : null, failures, timeout.Token);

            At("stop the widgets");
            foreach (var widget in dashboard.Widgets) Check(await widget.StopAsync(timeout.Token) == 0, "A widget did not exit cleanly.");
            foreach (var (name, _) in Guests.Widgets)
            {
                At($"end the {name} widget's region");
                await UntilAsync(() => dashboard.Region(name) is { State: RunicHostedRegionState.Ended } ended ? ended : null, failures, timeout.Token);
            }
            return finding;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new ProbeException($"Could not {step} within {Wait.TotalSeconds:0} s.");
        }
    }

    private static async Task<T> UntilAsync<T>(Func<T?> read, ProbeLog failures, CancellationToken cancellationToken) where T : class
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

// The probe's log: every message of the host, and the guests' output lines that the host passes on (events 2021 and
// 2022), written at once to standard error, so a probe that ends abruptly still shows how far it got. It also records
// the failures that the host reports only in its log: a region that did not attach (event 2026,
// DesktopHostedRegionAttachFailed) and a region or window that failed (2028, 2019), so the probe fails with the reason.
internal sealed class ProbeLog : ILoggerProvider
{
    private static readonly HashSet<string> Failures = ["DesktopHostedRegionAttachFailed", "DesktopHostedRegionFailed", "DesktopHostedWindowFailed"];
    private static readonly Lock Gate = new();
    private readonly TaskCompletionSource<(string Message, Exception? Exception)> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<(string Message, Exception? Exception)> First => _first.Task;

    public static void Write(string line)
    {
        lock (Gate)
        {
            Console.Error.WriteLine($"HOSTED_COMPONENTS_PROBE: {line}");
            Console.Error.Flush();
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(ProbeLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Debug for the hosting category, whose debug events are the guests' output lines.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (category.StartsWith("Runic.Application.Views.Desktop", StringComparison.Ordinal) ? LogLevel.Debug : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            Write($"{logLevel} {category}[{eventId.Id}]: {message}{(exception is null ? "" : Environment.NewLine + exception)}");
            if (eventId.Name is { } name && Failures.Contains(name)) owner._first.TrySetResult((message, exception));
        }
    }
}
