using System.Collections.Concurrent;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// The one quit path (D045.2) without AppKit: ConfirmQuitAsync, then each window's close guard, then the shutdown that
// cancels ShutdownRequested. The macOS native smoke (--lifecycle) drives it through the quit Apple event.
public sealed class QuitTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task RefusedQuitKeepsWindowsAndShutdownRequested()
    {
        var sources = new ConcurrentQueue<DesktopQuitSource>();
        var guardAsked = false;
        await using var host = await StartAsync((request, _) => { sources.Enqueue(request.Source); return ValueTask.FromResult(false); });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("x") });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            ConfirmCloseAsync = _ => { guardAsked = true; return ValueTask.FromResult(true); },
        });

        Assert.Equal(DesktopQuitOutcome.Refused, await DesktopQuitCoordinator.QuitAsync(DesktopQuitSource.Dock, [host]));

        Assert.Equal([DesktopQuitSource.Dock], sources.ToArray());
        Assert.False(guardAsked);
        Assert.True(window.IsOpen);
        Assert.False(host.ShutdownRequested.IsCancellationRequested);
    }

    [Test]
    public async Task CloseGuardCanRefuseAQuitTheApplicationAccepted()
    {
        var order = new ConcurrentQueue<string>();
        var allow = false;
        await using var host = await StartAsync((_, _) => { order.Enqueue("quit"); return ValueTask.FromResult(true); });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("x") });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            ConfirmCloseAsync = _ => { order.Enqueue("guard"); return ValueTask.FromResult(Volatile.Read(ref allow)); },
        });

        Assert.Equal(DesktopQuitOutcome.Refused, await DesktopQuitCoordinator.QuitAsync(DesktopQuitSource.Menu, [host]));
        Assert.Equal(["quit", "guard"], order.ToArray());
        Assert.True(window.IsOpen);
        Assert.False(host.ShutdownRequested.IsCancellationRequested);

        Volatile.Write(ref allow, true);
        Assert.Equal(DesktopQuitOutcome.Accepted, await DesktopQuitCoordinator.QuitAsync(DesktopQuitSource.System, [host]));
        await window.WaitForCloseAsync().AsTask().WaitAsync(StepTimeout);
        await CancelledAsync(host.ShutdownRequested);
        Assert.False(window.IsOpen);
    }

    [Test]
    public async Task QuitClosesUnguardedWindowsThroughTheShutdown()
    {
        await using var host = await StartAsync(confirm: null);
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("x") });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });

        Assert.Equal(DesktopQuitOutcome.Accepted, await DesktopQuitCoordinator.QuitAsync(DesktopQuitSource.Dock, [host]));

        await window.WaitForCloseAsync().AsTask().WaitAsync(StepTimeout);
        await CancelledAsync(host.ShutdownRequested);
    }

    [Test]
    public async Task QuitWithoutWindowRaisesShutdownRequested()
    {
        // An application behind a status icon, or one waiting for a Dock click, ends on ShutdownRequested.
        await using var windowless = await StartAsync(confirm: null, handlesShutdownWithoutWindow: true);
        Assert.Equal(DesktopQuitOutcome.Accepted, await DesktopQuitCoordinator.QuitAsync(DesktopQuitSource.Dock, [windowless]));
        await CancelledAsync(windowless.ShutdownRequested);

        // Without that declaration nothing would end the application: the platform ends the process, after
        // ShutdownRequested, as for a signal.
        await using var undeclared = await StartAsync(confirm: null);
        Assert.Equal(DesktopQuitOutcome.AcceptedWithoutOrder, await DesktopQuitCoordinator.QuitAsync(DesktopQuitSource.Menu, [undeclared]));
        await CancelledAsync(undeclared.ShutdownRequested);
    }

    [Test]
    public async Task FailedConfirmationKeepsTheApplicationRunning()
    {
        await using var host = await StartAsync((_, _) => throw new InvalidOperationException("prompt failed"), handlesShutdownWithoutWindow: true);

        Assert.Equal(DesktopQuitOutcome.Refused, await DesktopQuitCoordinator.QuitAsync(DesktopQuitSource.Menu, [host]));
        Assert.False(host.ShutdownRequested.IsCancellationRequested);
    }

    [Test]
    public async Task ShutdownWhileDecidingCancelsTheConfirmationAndGoesAhead()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await StartAsync(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return false;
        }, handlesShutdownWithoutWindow: true);

        var quit = DesktopQuitCoordinator.QuitAsync(DesktopQuitSource.Dock, [host]);
        await entered.Task.WaitAsync(StepTimeout);
        Assert.True(host.RequestShutdown("SIGTERM"));

        Assert.Equal(DesktopQuitOutcome.Accepted, await quit.WaitAsync(StepTimeout));
    }

    [Test]
    public async Task SignalAsksConfirmQuitAsyncAndARefusalLetsTheNextSignalAskAgain()
    {
        var answers = new Queue<bool>([false, true]);
        var sources = new ConcurrentQueue<DesktopQuitSource>();
        var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await StartAsync((request, _) =>
        {
            sources.Enqueue(request.Source);
            lock (answers) return ValueTask.FromResult(answers.Dequeue());
        }, handlesShutdownWithoutWindow: true);

        Assert.True(host.RequestShutdownForSignal("SIGINT", () => refused.TrySetResult()));
        await refused.Task.WaitAsync(StepTimeout);
        Assert.False(host.ShutdownRequested.IsCancellationRequested);

        Assert.True(host.RequestShutdownForSignal("SIGINT", static () => { }));
        await CancelledAsync(host.ShutdownRequested);
        Assert.Equal([DesktopQuitSource.Signal, DesktopQuitSource.Signal], sources.ToArray());
        Assert.False(host.RequestShutdownForSignal("SIGINT", static () => { }));
    }

    [Test]
    public async Task SignalWithoutConfirmQuitAsyncShutsDownAtOnce()
    {
        await using var host = await StartAsync(confirm: null, handlesShutdownWithoutWindow: true);

        Assert.True(host.RequestShutdownForSignal("SIGTERM", static () => throw new InvalidOperationException("not refused")));
        await CancelledAsync(host.ShutdownRequested);
        Assert.False(host.RequestShutdownForSignal("SIGTERM", static () => { }));
    }

    [Test]
    public async Task ReopenRequestedReachesEveryHandlerAfterOneThrows()
    {
        await using var host = await StartAsync(confirm: null);
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ReopenRequested += static (_, _) => throw new InvalidOperationException("handler failed");
        host.ReopenRequested += (sender, e) => { if (ReferenceEquals(sender, host)) seen.TrySetResult(e.HasVisibleWindows); };

        host.RaiseReopenRequested(hasVisibleWindows: false);

        Assert.False(await seen.Task.WaitAsync(StepTimeout));
    }

    [Test]
    public async Task LiveHostsListsStartedHostsUntilDisposed()
    {
        var host = await StartAsync(confirm: null);
        Assert.Contains(host, DesktopHost.LiveHosts());
        await host.DisposeAsync();
        Assert.DoesNotContain(host, DesktopHost.LiveHosts());
    }

    [Test]
    public void EachOutcomeMapsToTheReplyOfTheQuitEvent()
    {
        // A refusal answers userCanceledErr at once, which cancels a logout; an accepted quit answers when the application
        // returned; a quit that nothing would end lets the platform terminate after answering.
        Assert.Equal(DesktopQuitReply.Refuse, DesktopQuitCoordinator.ReplyFor(DesktopQuitOutcome.Refused));
        Assert.Equal(DesktopQuitReply.AfterApplication, DesktopQuitCoordinator.ReplyFor(DesktopQuitOutcome.Accepted));
        Assert.Equal(DesktopQuitReply.Terminate, DesktopQuitCoordinator.ReplyFor(DesktopQuitOutcome.AcceptedWithoutOrder));
        Assert.Equal(DesktopQuitReply.Terminate, DesktopQuitCoordinator.ReplyFor(DesktopQuitOutcome.NoHost));
        Assert.Equal(-128, DesktopQuitCoordinator.UserCanceled);
    }

    [Test]
    public async Task MacOSOptionsAreValidated()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await DesktopHost.StartAsync(new DesktopHostOptions { MacOS = null! }));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await DesktopHost.StartAsync(new DesktopHostOptions { MacOS = new MacOSDesktopOptions { DockMenu = null! } }));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await DesktopHost.StartAsync(new DesktopHostOptions { MacOS = new MacOSDesktopOptions { DockMenu = [new DesktopMenu("No items", default)] } }));
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            MacOS = new MacOSDesktopOptions
            {
                DockMenu = [new DesktopMenuItem("New Window", "window.new"), new DesktopMenuSeparator(), new DesktopMenu("More", [new DesktopMenuItem("Help", "help")])],
            },
        });
        Assert.Equal(3, host.Options.MacOS.DockMenu.Count);
        Assert.Equal(0, new MacOSDesktopOptions().ForwardingApplicationDelegate);
        Assert.Empty(new MacOSDesktopOptions().DockMenu);
    }

    // RequestShutdown cancels ShutdownRequested on the thread pool.
    private static Task CancelledAsync(CancellationToken token) =>
        Task.Delay(Timeout.Infinite, token).ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
            .WaitAsync(StepTimeout, CancellationToken.None);

    private static ValueTask<DesktopHost> StartAsync(
        Func<DesktopQuitRequest, CancellationToken, ValueTask<bool>>? confirm, bool handlesShutdownWithoutWindow = false) =>
        DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new ClosableWindowHostFactory(),
            WaitForConnection = false,
            ConfirmQuitAsync = confirm,
            HandlesShutdownWithoutWindow = handlesShutdownWithoutWindow,
        });
}
