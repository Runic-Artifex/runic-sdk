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

    public static async Task<int> RunAsync(RunicDesktopHost host, RunicWindowOptions options, ProbeLog failures, bool elevated)
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
            if (error.InnerException is { } inner) Console.Error.WriteLine(inner);
            return 1;
        }
        catch (Exception error)
        {
            // Any other failure is the probe's too, and says what happened.
            Console.Error.WriteLine($"HOSTED_REGION_FAILED: {error}");
            return 1;
        }
    }

    private static async Task RunCoreAsync(RunicDesktopHost host, RunicWindowOptions options, ProbeLog failures)
    {
        using var timeout = new CancellationTokenSource(Wait);
        var step = "";
        void At(string name)
        {
            step = name;
            ProbeLog.Write($"step: {name}");
        }
        At("open the installer's window");
        try
        {
            await using var window = await host.OpenWindowAsync<InstallerWindow>(options, timeout.Token);
            var installer = window.ViewModel;

            At("go to the check step");
            Check(await installer.NextAsync(timeout.Token), "The wizard did not go to the check step.");

            // The check starts, its Window opens in the region, and its page loads in the frame and connects.
            At("attach the check's window to the region");
            var region = await UntilAsync(() => installer.CheckRegion, failures, installer, timeout.Token);
            await UntilAsync(() => region.State == RunicHostedRegionState.Attached ? region : null, failures, installer, timeout.Token);
            Check(region.Name == CheckPolicy.Region && !region.IsComponent && region.Size is { Width: > 0, Height: > 0 },
                $"The region is {region.Name}, {region.Size}.");

            // The title comes from the check's page in the frame, once its checks ran.
            At("receive the check page's title");
            await UntilAsync(() => region.Title is "System check: passed" or "System check: problems found" ? region : null, failures, installer, timeout.Token);

            At("detach the check");
            var detached = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            region.Detached += (_, e) => detached.TrySetResult(e.Reason);
            await region.DetachAsync(timeout.Token);
            Check(await detached.Task.WaitAsync(timeout.Token) == "host", "The detach did not report the host as its reason.");
            Check(region.State == RunicHostedRegionState.Detached && !region.Window.Closed.IsCompleted, "The detached window is not open and detached.");

            At("attach the check again");
            await region.MoveToAsync(window, CheckPolicy.Region, timeout.Token);
            await UntilAsync(() => region.State == RunicHostedRegionState.Attached ? region : null, failures, installer, timeout.Token);

            // Next asks the check's close guard (CheckStep); the checks have finished, so the check allows it and closes.
            At("leave the check step");
            Check(await installer.NextAsync(timeout.Token), "The wizard did not leave the check step.");
            Check(installer.Step == "finish", $"The wizard shows {installer.Step}.");
            Check(region.Window.Closed.IsCompleted, "The departure went ahead before the check's Window closed.");
            At("end the check's region");
            await UntilAsync(() => region.State == RunicHostedRegionState.Ended ? region : null, failures, installer, timeout.Token);
            At("end the check");
            Check(await installer.CheckApplication!.Exited.WaitAsync(timeout.Token) == 0, "The check did not exit cleanly.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new ProbeException($"Could not {step} within {Wait.TotalSeconds:0} s.");
        }
    }

    // Waits for a value, failing early with the reason the host logged or the check's start failure.
    private static async Task<T> UntilAsync<T>(Func<T?> read, ProbeLog failures, InstallerViewModel installer, CancellationToken cancellationToken)
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
            Console.Error.WriteLine($"HOSTED_REGION_PROBE: {line}");
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
