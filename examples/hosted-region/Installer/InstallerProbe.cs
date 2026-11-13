using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;

namespace HostedInstaller;

// `HostedInstaller --probe [--elevated]`: CI's check on a real desktop. The installer opens its window and goes to the
// check step; the check's Window attaches to the page's region, its page loads in the frame and reports its title; the
// installer detaches it and attaches it again; Next asks the check's close guard, the check closes, and the region ends.
// With --elevated, installer and check run as the elevated pair (CI's Windows runner runs elevated).
internal static class InstallerProbe
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(RunicDesktopHost host, RunicWindowOptions options, RegionFailures failures, bool elevated)
    {
        try
        {
            await RunCoreAsync(host, options, failures);
            Console.WriteLine($"HOSTED_REGION_OK|attach|title|detach|reattach|departure|end{(elevated ? "|elevated" : "")}");
            return 0;
        }
        catch (ProbeException error)
        {
            Console.Error.WriteLine($"HOSTED_REGION_FAILED: {error.Message}");
            return 1;
        }
    }

    private static async Task RunCoreAsync(RunicDesktopHost host, RunicWindowOptions options, RegionFailures failures)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var step = "open the installer's window";
        try
        {
            await using var window = await host.OpenWindowAsync<InstallerWindow>(options, timeout.Token);
            var installer = window.ViewModel;

            step = "go to the check step";
            Check(await installer.NextAsync(timeout.Token), "The wizard did not go to the check step.");

            // The check starts, its Window opens in the region, and its page loads in the frame and connects.
            step = "attach the check's window to the region";
            var region = await UntilAsync(() => installer.CheckRegion, failures, installer, timeout.Token);
            await UntilAsync(() => region.State == RunicHostedRegionState.Attached ? region : null, failures, installer, timeout.Token);
            Check(region.Name == CheckPolicy.Region && !region.IsComponent && region.Size is { Width: > 0, Height: > 0 },
                $"The region is {region.Name}, {region.Size}.");

            // The title comes from the check's page in the frame, once its checks ran.
            step = "receive the check page's title";
            await UntilAsync(() => region.Title is "System check: passed" or "System check: problems found" ? region : null, failures, installer, timeout.Token);

            step = "detach the check";
            var detached = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            region.Detached += (_, e) => detached.TrySetResult(e.Reason);
            await region.DetachAsync(timeout.Token);
            Check(await detached.Task.WaitAsync(timeout.Token) == "host", "The detach did not report the host as its reason.");
            Check(region.State == RunicHostedRegionState.Detached && !region.Window.Closed.IsCompleted, "The detached window is not open and detached.");

            step = "attach the check again";
            await region.MoveToAsync(window, CheckPolicy.Region, timeout.Token);
            await UntilAsync(() => region.State == RunicHostedRegionState.Attached ? region : null, failures, installer, timeout.Token);

            // Next asks the check's close guard (CheckStep); the checks have finished, so the check allows it and closes.
            step = "leave the check step";
            Check(await installer.NextAsync(timeout.Token), "The wizard did not leave the check step.");
            Check(installer.Step == "finish", $"The wizard shows {installer.Step}.");
            Check(region.Window.Closed.IsCompleted, "The departure went ahead before the check's Window closed.");
            step = "end the check's region";
            await UntilAsync(() => region.State == RunicHostedRegionState.Ended ? region : null, failures, installer, timeout.Token);
            step = "end the check";
            Check(await installer.CheckApplication!.Exited.WaitAsync(timeout.Token) == 0, "The check did not exit cleanly.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new ProbeException($"Could not {step} within {Wait.TotalSeconds:0} s.");
        }
    }

    // Waits for a value, failing early with the reason the host logged or the check's start failure.
    private static async Task<T> UntilAsync<T>(Func<T?> read, RegionFailures failures, InstallerViewModel installer, CancellationToken cancellationToken)
        where T : class
    {
        while (true)
        {
            if (read() is { } value) return value;
            if (failures.First.IsCompleted)
            {
                var (message, exception) = await failures.First;
                throw new ProbeException($"The installer reported: {exception?.Message ?? message}", exception);
            }
            if (installer.CheckState.StartsWith("failed", StringComparison.Ordinal)) throw new ProbeException($"The check {installer.CheckState}.");
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
