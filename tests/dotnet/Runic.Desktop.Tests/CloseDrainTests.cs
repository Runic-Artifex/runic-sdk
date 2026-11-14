using System.Collections.Concurrent;
using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// sdk#900 W4b: DesktopWindowOptions.CloseDrainAsync and the shutdown's drain (D064.15, D064.16), with a window host that
// sends its native close through CloseRequested as the built-in hosts do.
public sealed class CloseDrainTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

    [Test]
    public async Task AnApprovedUserCloseKeepsTheWindowOpenUntilTheDrainReturns()
    {
        var factory = new DrainWindowHostFactory();
        await using var host = await StartAsync(factory);
        await using var surface = await CreateSurfaceAsync(host);
        var drain = new Drain();
        var asked = 0;
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            ConfirmCloseAsync = _ => { Interlocked.Increment(ref asked); return ValueTask.FromResult(true); },
            CloseDrainAsync = drain.RunAsync,
        });
        Assert.True(window.DrainsVisibly);

        factory.Host!.UserClose();
        Assert.Equal(DesktopCloseReason.User, await drain.Started.Task.WaitScaledAsync(StepTimeout));
        Assert.True(window.IsOpen);

        // D064.16: a second close by the user, and a request, are consumed while the window drains.
        factory.Host.UserClose();
        Assert.True(await window.RequestCloseAsync().AsTask().WaitScaledAsync(StepTimeout));
        await Task.Delay(Settle);
        Assert.True(window.IsOpen);
        Assert.Equal(1, Volatile.Read(ref asked));
        Assert.Equal(1, drain.Runs);

        drain.Release();
        await window.WaitForCloseAsync().AsTask().WaitScaledAsync(StepTimeout);
        Assert.False(window.IsOpen);
        // The drain returned on its own, before anything ended it.
        Assert.True(drain.Ended);
    }

    [Test]
    public async Task ARefusedCloseDoesNotDrain()
    {
        var factory = new DrainWindowHostFactory();
        await using var host = await StartAsync(factory);
        await using var surface = await CreateSurfaceAsync(host);
        var drain = new Drain();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            ConfirmCloseAsync = static _ => ValueTask.FromResult(false),
            CloseDrainAsync = drain.RunAsync,
        });

        Assert.False(await window.RequestCloseAsync().AsTask().WaitScaledAsync(StepTimeout));
        factory.Host!.UserClose();
        await Task.Delay(Settle);
        Assert.Equal(0, drain.Runs);
        Assert.True(window.IsOpen);
    }

    [Test]
    public async Task RequestCloseReturnsAtApprovalAndADrainWithoutAGuardStillVetoesTheNativeClose()
    {
        var factory = new DrainWindowHostFactory();
        await using var host = await StartAsync(factory);
        await using var surface = await CreateSurfaceAsync(host);
        var drain = new Drain();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            CloseDrainAsync = drain.RunAsync,
        });

        // The work being drained may ask for the close itself ("Save and close") without waiting for itself.
        Assert.True(await window.RequestCloseAsync().AsTask().WaitScaledAsync(StepTimeout));
        Assert.Equal(DesktopCloseReason.Request, await drain.Started.Task.WaitScaledAsync(StepTimeout));
        Assert.True(window.IsOpen);

        drain.Release();
        await window.WaitForCloseAsync().AsTask().WaitScaledAsync(StepTimeout);
    }

    [Test]
    public async Task AForcedCloseEndsTheDrain()
    {
        var factory = new DrainWindowHostFactory();
        await using var host = await StartAsync(factory);
        await using var surface = await CreateSurfaceAsync(host);
        var drain = new Drain();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            CloseDrainAsync = drain.RunAsync,
        });

        factory.Host!.UserClose();
        await drain.Started.Task.WaitScaledAsync(StepTimeout);
        await window.CloseAsync().AsTask().WaitScaledAsync(StepTimeout);

        Assert.True(drain.Token.IsCancellationRequested);
        Assert.False(window.IsOpen);
    }

    [Test]
    public async Task AFailingDrainClosesTheWindow()
    {
        var factory = new DrainWindowHostFactory();
        await using var host = await StartAsync(factory);
        await using var surface = await CreateSurfaceAsync(host);
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            CloseDrainAsync = static (_, _) => throw new InvalidOperationException("drain failed"),
        });

        factory.Host!.UserClose();
        await window.WaitForCloseAsync().AsTask().WaitScaledAsync(StepTimeout);
    }

    [Test]
    public async Task AShutdownDrainsVisibleWindowsBeforeShutdownRequested()
    {
        var factory = new DrainWindowHostFactory();
        await using var host = await StartAsync(factory);
        await using var drainingSurface = await CreateSurfaceAsync(host);
        var drain = new Drain();
        await using var draining = await drainingSurface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            CloseDrainAsync = drain.RunAsync,
        });
        await using var plainSurface = await CreateSurfaceAsync(host);
        await using var plain = await plainSurface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });

        Assert.True(host.RequestShutdown("SIGTERM"));
        await CancelledAsync(host.ShutdownStarting);
        Assert.Equal(DesktopCloseReason.Shutdown, await drain.Started.Task.WaitScaledAsync(StepTimeout));
        // A window without a drain closes at once; the draining one stays, and ShutdownRequested waits for it.
        await plain.WaitForCloseAsync().AsTask().WaitScaledAsync(StepTimeout);
        await Task.Delay(Settle);
        Assert.True(draining.IsOpen);
        Assert.False(host.ShutdownRequested.IsCancellationRequested);

        drain.Release();
        await draining.WaitForCloseAsync().AsTask().WaitScaledAsync(StepTimeout);
        await CancelledAsync(host.ShutdownRequested);
        // The drain returned on its own, before anything ended it.
        Assert.True(drain.Ended);
    }

    [Test]
    public async Task ShutdownDrainTimeoutClosesTheWindowsAndCancelsNoWork()
    {
        var factory = new DrainWindowHostFactory();
        await using var host = await StartAsync(factory, shutdownDrainTimeout: TimeSpan.FromMilliseconds(300));
        await using var surface = await CreateSurfaceAsync(host);
        var drain = new Drain(ignoresToken: true);
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            CloseDrainAsync = drain.RunAsync,
        });

        Assert.True(host.RequestShutdown("SIGINT"));
        await drain.Started.Task.WaitScaledAsync(StepTimeout);
        // The drain ignores its token: the limit still closes the window, and only the visible phase ends.
        await CancelledAsync(host.ShutdownRequested);
        await window.WaitForCloseAsync().AsTask().WaitScaledAsync(StepTimeout);
        Assert.True(drain.Token.IsCancellationRequested);
        Assert.False(drain.Ended);
        drain.Release();
    }

    [Test]
    public async Task AShutdownJoinsTheDrainOfAUserClose()
    {
        var factory = new DrainWindowHostFactory();
        await using var host = await StartAsync(factory);
        await using var surface = await CreateSurfaceAsync(host);
        var drain = new Drain();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            CloseDrainAsync = drain.RunAsync,
        });

        factory.Host!.UserClose();
        Assert.Equal(DesktopCloseReason.User, await drain.Started.Task.WaitScaledAsync(StepTimeout));
        Assert.True(host.RequestShutdown("SIGTERM"));
        await CancelledAsync(host.ShutdownStarting);
        await Task.Delay(Settle);
        Assert.False(host.ShutdownRequested.IsCancellationRequested);
        Assert.Equal(1, drain.Runs);

        drain.Release();
        await CancelledAsync(host.ShutdownRequested);
        await window.WaitForCloseAsync().AsTask().WaitScaledAsync(StepTimeout);
    }

    [Test]
    public async Task WithoutADrainBothShutdownTokensAreCancelledTogether()
    {
        var factory = new DrainWindowHostFactory();
        var order = new ConcurrentQueue<string>();
        await using var host = await StartAsync(factory);
        host.ShutdownStarting.Register(() => order.Enqueue("starting"));
        host.ShutdownRequested.Register(() => order.Enqueue("requested"));
        await using var surface = await CreateSurfaceAsync(host);
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });

        Assert.True(host.RequestShutdown("SIGTERM"));
        await window.WaitForCloseAsync().AsTask().WaitScaledAsync(StepTimeout);
        await CancelledAsync(host.ShutdownRequested);
        Assert.Equal(["starting", "requested"], order.ToArray());
    }

    [Test]
    public async Task AQuitIsAcceptedOnceTheGuardsAgreedAndTheWindowsDrainBeforeShutdownRequested()
    {
        var factory = new DrainWindowHostFactory();
        await using var host = await StartAsync(factory, confirmQuit: static (_, _) => ValueTask.FromResult(true));
        await using var surface = await CreateSurfaceAsync(host);
        var drain = new Drain();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            ConfirmCloseAsync = static _ => ValueTask.FromResult(true),
            CloseDrainAsync = drain.RunAsync,
        });

        Assert.Equal(DesktopQuitOutcome.Accepted,
            await DesktopQuitCoordinator.QuitAsync(DesktopQuitSource.Menu, [host]).WaitScaledAsync(StepTimeout));
        Assert.Equal(DesktopCloseReason.Quit, await drain.Started.Task.WaitScaledAsync(StepTimeout));
        await CancelledAsync(host.ShutdownStarting);
        Assert.True(window.IsOpen);
        Assert.False(host.ShutdownRequested.IsCancellationRequested);

        drain.Release();
        await CancelledAsync(host.ShutdownRequested);
        await window.WaitForCloseAsync().AsTask().WaitScaledAsync(StepTimeout);
    }

    [Test]
    public async Task AWindowThatCannotRefuseACloseDrainsAfterItIsGone()
    {
        var factory = new DrainWindowHostFactory(confirms: false);
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        await using var host = await StartAsync(factory, diagnostics: diagnostics);
        await using var surface = await CreateSurfaceAsync(host);
        var drain = new Drain();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            CloseDrainAsync = drain.RunAsync,
        });
        Assert.False(window.DrainsVisibly);
        var warning = Assert.Single(diagnostics, static item => item.Code == "close-drain-unavailable");
        Assert.Equal(DesktopDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("DesktopWindowOptions.CloseDrainAsync", warning.Option);

        factory.Host!.UserClose();
        var waited = window.WaitForCloseAsync().AsTask();
        Assert.Equal(DesktopCloseReason.User, await drain.Started.Task.WaitScaledAsync(StepTimeout));
        Assert.False(window.IsOpen);
        await Task.Delay(Settle);
        Assert.False(waited.IsCompleted);

        drain.Release();
        await waited.WaitScaledAsync(StepTimeout);
    }

    [Test]
    public async Task ShutdownDrainTimeoutIsValidated()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await DesktopHost.StartAsync(new DesktopHostOptions { ShutdownDrainTimeout = TimeSpan.FromSeconds(-2) }));
        await using var infinite = await DesktopHost.StartAsync(new DesktopHostOptions { ShutdownDrainTimeout = Timeout.InfiniteTimeSpan });
        await using var zero = await DesktopHost.StartAsync(new DesktopHostOptions { ShutdownDrainTimeout = TimeSpan.Zero });
        Assert.Equal(TimeSpan.FromSeconds(30), new DesktopHostOptions().ShutdownDrainTimeout);
    }

    private static Task CancelledAsync(CancellationToken token) =>
        Task.Delay(Timeout.Infinite, token).ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
            .WaitScaledAsync(StepTimeout, CancellationToken.None);

    private static ValueTask<DesktopSurface> CreateSurfaceAsync(DesktopHost host) =>
        host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("x") });

    private static ValueTask<DesktopHost> StartAsync(DrainWindowHostFactory factory, TimeSpan? shutdownDrainTimeout = null,
        Func<DesktopQuitRequest, CancellationToken, ValueTask<bool>>? confirmQuit = null, ConcurrentQueue<DesktopDiagnostic>? diagnostics = null) =>
        DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
            ConfirmQuitAsync = confirmQuit,
            ShutdownDrainTimeout = shutdownDrainTimeout ?? TimeSpan.FromSeconds(30),
            DiagnosticSink = diagnostics is null ? null : diagnostics.Enqueue,
        });

    // A CloseDrainAsync the test releases.
    private sealed class Drain(bool ignoresToken = false)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;
        private int _ended;

        internal TaskCompletionSource<DesktopCloseReason> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        internal int Runs => Volatile.Read(ref _runs);
        internal bool Ended => Volatile.Read(ref _ended) != 0;

        internal void Release() => _release.TrySetResult();

        internal async ValueTask RunAsync(DesktopCloseDrainContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            Token = cancellationToken;
            Started.TrySetResult(context.Reason);
            await (ignoresToken ? _release.Task : _release.Task.WaitAsync(cancellationToken));
            Volatile.Write(ref _ended, 1);
        }
    }
}

// A window host whose user close goes through DesktopWindowHostOptions.CloseRequested, as the built-in hosts' do.
internal sealed class DrainWindowHostFactory(bool confirms = true) : IDesktopWindowHostFactory
{
    internal DrainWindowHost? Host { get; private set; }
    public bool IsSupported => true;
    public DesktopWindowCapabilities Capabilities =>
        confirms ? DesktopWindowCapabilities.CloseConfirmation : DesktopWindowCapabilities.None;
    public IDesktopWindowHost Create() => Host = new DrainWindowHost(confirms);
}

internal sealed class DrainWindowHost(bool confirms) : IDesktopWindowHost
{
    private int _open;
    private Action? _closeRequested;
    public event EventHandler? Closed;
    public bool IsOpen => Volatile.Read(ref _open) != 0;
    public bool SupportsCloseConfirmation => confirms;
    public nint NativeHandle => IsOpen ? 1 : 0;

    // The title bar's close button: asks the application when it confirms closes, else closes.
    internal void UserClose()
    {
        if (_closeRequested is { } request) request();
        else _ = CloseAsync().AsTask();
    }

    public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _closeRequested = confirms ? options.CloseRequested : null;
        Volatile.Write(ref _open, 1);
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _open, 0) != 0) Closed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _open, 0);
        return ValueTask.CompletedTask;
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
