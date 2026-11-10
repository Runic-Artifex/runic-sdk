using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Runic.Application.Views.Desktop.Hosting;

// IRunicHostedApplications of one RunicDesktopHost: the guests it started, which it stops when the Desktop host shuts
// down and when RunicDesktopHost.Run returns.
internal sealed class RunicHostedApplications : IRunicHostedApplications, IAsyncDisposable
{
    internal const string LogCategory = "Runic.Application.Views.Desktop.Hosting";

    private readonly RunicDesktopHost _host;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly List<RunicHostedApplication> _running = [];
    // The starts still in their handshake, which the host's shutdown or disposal cancels and waits for.
    private readonly HashSet<Task> _starting = [];
    // Cancelled when the Desktop host's shutdown was requested or this is disposed: no guest starts or opens a window.
    private readonly CancellationTokenSource _closing = new();
    private readonly CancellationTokenRegistration _shutdown;
    private readonly object _eventGate = new();
    private Task _events = Task.CompletedTask;
    private int _disposed;

    internal RunicHostedApplications(RunicDesktopHost host)
    {
        _host = host;
        _logger = host.Services.GetService<ILoggerFactory>()?.CreateLogger(LogCategory) ?? NullLogger.Instance;
        // The host's shutdown, such as Quit or a signal, stops the guests as the host's own windows close.
        _shutdown = host.Desktop.ShutdownRequested.Register(static state => _ = ((RunicHostedApplications)state!).StopAllAsync("host-exit"), this);
    }

    public IReadOnlyList<RunicHostedApplication> Applications
    {
        get { lock (_gate) return [.. _running]; }
    }

    // Whether the host is shutting down: a guest's window-open is refused.
    internal bool IsClosing => _closing.IsCancellationRequested || _host.Desktop.ShutdownRequested.IsCancellationRequested;

    public async ValueTask<RunicHostedApplication> StartAsync(RunicGuestStartInfo startInfo, RunicHostedApplicationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (_host.IsHosted || _host.ServeOnly)
            throw new InvalidOperationException("A hosted or serve-only application shows no windows of its own, so it cannot host another application.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || IsClosing) throw ShuttingDown();
            _starting.Add(completion.Task);
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
            RunicHostedApplication application;
            try
            {
                application = await RunicHostedApplication.StartAsync(this, _host, startInfo, options ?? new RunicHostedApplicationOptions(),
                    _logger, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _closing.IsCancellationRequested)
            {
                // The start ended the guest's process tree.
                throw ShuttingDown();
            }
            var stop = false;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || IsClosing) stop = true;
                else if (!application.Exited.IsCompleted) _running.Add(application);
            }
            if (stop)
            {
                await application.StopCoreAsync("host-exit", CancellationToken.None).ConfigureAwait(false);
                throw ShuttingDown();
            }
            return application;
        }
        finally
        {
            lock (_gate) _starting.Remove(completion.Task);
            completion.TrySetResult();
        }
    }

    private static InvalidOperationException ShuttingDown() => new("The Desktop host is shutting down.");

    // ----- window-services/1: activations that are a guest's, not the user's (host-protocol.md, Window services).

    // The last request of any guest that can activate one of its windows, or another window as one of its windows closes,
    // hides or minimizes (a Stopwatch timestamp; 0 for none). Host-wide, across guests: an activation of any guest's
    // window within RunicHostedWindow.GuestActivationWindow after it is a guest's, so another guest, such as a second
    // instance of the same one, cannot hand a window over by minimizing, hiding or closing the active one.
    private long _guestActivation;

    // The guests' dialogs and context menus shown now, and the policy decisions still pending, such as a host's prompt:
    // while one is, and within GuestActivationWindow after it ended, an activation is a guest's, since the platform
    // activates the owner as the prompt closes, before the host learns why.
    private int _guestPrompts;

    internal void NoteGuestActivation() => Volatile.Write(ref _guestActivation, Stopwatch.GetTimestamp());

    internal void BeginGuestPrompt() => Interlocked.Increment(ref _guestPrompts);

    internal void EndGuestPrompt()
    {
        NoteGuestActivation();
        Interlocked.Decrement(ref _guestPrompts);
    }

    internal bool IsGuestActivationRecent => Volatile.Read(ref _guestPrompts) > 0 ||
        (Volatile.Read(ref _guestActivation) is var noted and not 0 && Stopwatch.GetElapsedTime(noted) <= RunicHostedWindow.GuestActivationWindow);

    public event EventHandler<RunicHostedWindowEventArgs>? WindowOpened;

    public event EventHandler<RunicHostedWindowEventArgs>? WindowClosed;

    internal void RaiseWindowOpened(RunicHostedWindow window) => Raise(WindowOpened, window);

    internal void RaiseWindowClosed(RunicHostedWindow window) => Raise(WindowClosed, window);

    // On a thread-pool thread, so a handler never runs in the channel's receive loop or a native callback, and one event
    // after the other, so a window's WindowClosed follows its WindowOpened.
    private void Raise(EventHandler<RunicHostedWindowEventArgs>? handler, RunicHostedWindow window)
    {
        if (handler is null) return;
        lock (_eventGate)
        {
            _events = _events.ContinueWith(_ =>
            {
                try { handler(this, new RunicHostedWindowEventArgs(window)); }
                catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedWindowPolicyFailed(_logger, error, error.GetType().Name); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    internal void Remove(RunicHostedApplication application)
    {
        lock (_gate) _running.Remove(application);
    }

    // The host shuts down: pending starts are cancelled, which ends their guests, and the running guests stop.
    private async Task StopAllAsync(string reason)
    {
        try { await _closing.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        Task[] starting;
        lock (_gate) starting = [.. _starting];
        await Task.WhenAll(starting).ConfigureAwait(false);
        RunicHostedApplication[] running;
        lock (_gate) running = [.. _running];
        await Task.WhenAll(running.Select(async application =>
        {
            try { _ = await application.StopCoreAsync(reason, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedApplicationChannelFailed(_logger, error, error.GetType().Name); }
        })).ConfigureAwait(false);
    }

    // RunicDesktopHost.Run returned, or the services are disposed: pending starts fail and every guest still running
    // stops.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Dispose();
        await StopAllAsync("host-exit").ConfigureAwait(false);
    }
}
