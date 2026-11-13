using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;
using static DesktopChecks;

// sdk#900 W4b: RunicCloseDrain on RunicDesktopHost. A decided close keeps the embedded window open and the page attached
// until the Window's accepted work has drained (here a scope it drains, DrainOnClose), a second user close is ignored,
// RequestCloseAsync returns at the decision, and a shutdown drains before ShutdownRequested (D064.15, D064.16).
internal sealed class CloseDrainChecks
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

    [Test, NotInParallel(Order = 82)]
    public async Task ADecidedUserCloseKeepsTheWindowUntilItsWorkHasDrained()
    {
        await using var fixture = await GuardFixture.OpenAsync<GuardedWindow>(closeDrain: RunicCloseDrain.UntilDrained);
        var work = new AcceptedWorkScope();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = fixture.ViewModel.Context.DrainOnClose(work);
        var running = work.RunAsync(() => release.Task);
        var closed = fixture.Window.WaitForCloseAsync().AsTask();

        fixture.Native.UserClose();
        await WaitUntilAsync(() => fixture.Opened.IsClosing, "The decided close did not mark the Window closing.");
        await Task.Delay(Settle);
        Require(fixture.Native.IsOpen && fixture.Presentation.IsOpen && !closed.IsCompleted,
            "The window closed although the work it drains was still running.");
        Require(((DesktopBridgeWindow<GuardedShellModel>)fixture.Window).IsDraining, "The Window is not draining.");

        // D064.16: the user's second close and a request are consumed while the window drains.
        fixture.Native.UserClose();
        Require(await fixture.Opened.RequestCloseAsync().AsTask().WaitScaledAsync(Step),
            "RequestCloseAsync during the drain did not return true.");
        await Task.Delay(Settle);
        Require(fixture.Script.AskCount == 1, $"The guard was asked {fixture.Script.AskCount} times.");
        Require(fixture.Native.IsOpen, "A second close ended the drain.");

        release.TrySetResult();
        await running;
        await closed.WaitScaledAsync(Step);
        Require(!fixture.Native.IsOpen, "The native window stayed open after the work had drained.");
    }

    [Test, NotInParallel(Order = 83)]
    public async Task RequestCloseReturnsOnceTheCloseIsDecided()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>(closeDrain: RunicCloseDrain.UntilDrained);
        var work = new AcceptedWorkScope();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = fixture.ViewModel.Context.DrainOnClose(work);
        var running = work.RunAsync(() => release.Task);
        Require(fixture.Native.ConfirmsClose, "A Window with a close drain opened without a native close veto.");

        // "Save and close" from accepted work: the request returns at the decision, not when the window has gone.
        Require(await fixture.ViewModel.QuitAsync().WaitScaledAsync(Step), "The request did not return true at the decision.");
        Require(fixture.Native.IsOpen, "The request closed the window before its work had drained.");

        release.TrySetResult();
        await running;
        await fixture.Window.WaitForCloseAsync().AsTask().WaitScaledAsync(Step);
    }

    [Test, NotInParallel(Order = 84)]
    public async Task AForTimeoutClosesTheVisibleWindowAndKeepsDurableWork()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>(closeDrain: RunicCloseDrain.For(TimeSpan.FromMilliseconds(300)));
        var work = new AcceptedWorkScope();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = fixture.ViewModel.Context.DrainOnClose(work);
        var running = work.RunAsync(() => release.Task);
        try
        {
            fixture.Native.UserClose();
            await fixture.Window.WaitForCloseAsync().AsTask().WaitScaledAsync(Step);
            Require(!fixture.Native.IsOpen, "The window stayed open after its drain's timeout.");
            Require(!running.IsCompleted, "The drain's timeout ended the durable work.");
            var result = await fixture.Window.CloseAsync(TimeSpan.Zero).AsTask().WaitScaledAsync(Step);
            Require(!result.Drained && result.RemainingDurableWork == 1, $"The close reported {result}.");
            release.TrySetResult();
            await running;
            await result.Completion.WaitScaledAsync(Step);
        }
        finally { release.TrySetResult(); }
    }

    [Test, NotInParallel(Order = 85)]
    public async Task AShutdownDrainsTheWindowBeforeShutdownRequested()
    {
        await using var fixture = await GuardFixture.OpenAsync<GuardedWindow>(closeDrain: RunicCloseDrain.UntilDrained);
        var work = new AcceptedWorkScope();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = fixture.ViewModel.Context.DrainOnClose(work);
        var running = work.RunAsync(() => release.Task);

        Require(fixture.Desktop.RequestShutdown("SIGTERM"), "The shutdown did not start.");
        await WaitUntilAsync(() => fixture.Desktop.ShutdownStarting.IsCancellationRequested, "ShutdownStarting was not cancelled.");
        await WaitUntilAsync(() => fixture.Opened.IsClosing, "The shutdown did not start the Window's drain.");
        await Task.Delay(Settle);
        Require(fixture.Native.IsOpen && !fixture.Desktop.ShutdownRequested.IsCancellationRequested,
            "The shutdown closed the window or requested shutdown before the work had drained.");
        // A shutdown does not ask the guard (unchanged).
        Require(fixture.Script.AskCount == 0, "The shutdown asked the Window's guard.");

        release.TrySetResult();
        await running;
        await fixture.Window.WaitForCloseAsync().AsTask().WaitScaledAsync(Step);
        await WaitUntilAsync(() => fixture.Desktop.ShutdownRequested.IsCancellationRequested, "ShutdownRequested was not cancelled after the drain.");
    }

    [Test, NotInParallel(Order = 86)]
    public async Task ShutdownDrainTimeoutEndsTheVisibleDrainWithoutCancellingDurableWork()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>(closeDrain: RunicCloseDrain.UntilDrained,
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(300));
        var work = new AcceptedWorkScope();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = fixture.ViewModel.Context.DrainOnClose(work);
        var running = work.RunAsync(() => release.Task);

        Require(fixture.Desktop.RequestShutdown("SIGINT"), "The shutdown did not start.");
        await fixture.Window.WaitForCloseAsync().AsTask().WaitScaledAsync(Step);
        await WaitUntilAsync(() => fixture.Desktop.ShutdownRequested.IsCancellationRequested, "ShutdownRequested was not cancelled at the limit.");
        Require(!fixture.Native.IsOpen && !running.IsCompleted, "The limit did not close the window, or it ended the durable work.");

        release.TrySetResult();
        await running;
    }

    [Test, NotInParallel(Order = 87)]
    public async Task AHostThatCannotKeepItsWindowOpenLogsWindowCloseDrainUnavailable()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>(confirmsClose: false, closeDrain: RunicCloseDrain.UntilDrained);
        Require(fixture.Logs.Has(1055, LogLevel.Warning), "WindowCloseDrainUnavailable (1055) was not logged.");
        var entry = fixture.Logs.Entries.First(static entry => entry.EventId == 1055);
        Require(entry.Message.Contains("configured embedded-window host", StringComparison.Ordinal), $"Event 1055 read {entry.Message}");
        Require(fixture.Diagnostics.Any(static diagnostic => diagnostic.Code == "close-drain-unavailable"),
            "Runic Desktop did not report close-drain-unavailable.");
    }

    [Test, NotInParallel(Order = 88)]
    public async Task WithoutADrainTheWindowClosesAtOnce()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>();
        var work = new AcceptedWorkScope();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = fixture.ViewModel.Context.DrainOnClose(work);
        var running = work.RunAsync(() => release.Task);
        Require(!fixture.Native.ConfirmsClose, "A Window without a drain or guard configured a native close veto.");

        fixture.Native.UserClose();
        await fixture.Window.WaitForCloseAsync().AsTask().WaitScaledAsync(Step);
        Require(!fixture.Native.IsOpen, "The window did not close at once.");
        release.TrySetResult();
        await running;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + Step * 4;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(failure);
            await Task.Delay(20);
        }
    }
}
