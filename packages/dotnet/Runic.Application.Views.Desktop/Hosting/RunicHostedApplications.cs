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
    private readonly CancellationTokenRegistration _shutdown;
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

    public async ValueTask<RunicHostedApplication> StartAsync(RunicGuestStartInfo startInfo, RunicHostedApplicationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (_host.IsHosted || _host.ServeOnly)
            throw new InvalidOperationException("A hosted or serve-only application shows no windows of its own, so it cannot host another application.");
        if (Volatile.Read(ref _disposed) != 0 || _host.Desktop.ShutdownRequested.IsCancellationRequested)
            throw new InvalidOperationException("The Desktop host is shutting down.");
        var application = await RunicHostedApplication.StartAsync(this, _host, startInfo, options ?? new RunicHostedApplicationOptions(),
            _logger, cancellationToken).ConfigureAwait(false);
        var stop = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) stop = true;
            else if (!application.Exited.IsCompleted) _running.Add(application);
        }
        if (stop)
        {
            await application.StopCoreAsync("host-exit", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("The Desktop host is shutting down.");
        }
        return application;
    }

    public event EventHandler<RunicHostedWindowEventArgs>? WindowOpened;

    public event EventHandler<RunicHostedWindowEventArgs>? WindowClosed;

    internal void RaiseWindowOpened(RunicHostedWindow window) => Raise(WindowOpened, window);

    internal void RaiseWindowClosed(RunicHostedWindow window) => Raise(WindowClosed, window);

    // On a thread-pool thread, so a handler never runs in the channel's receive loop or a native callback.
    private void Raise(EventHandler<RunicHostedWindowEventArgs>? handler, RunicHostedWindow window)
    {
        if (handler is null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { handler(this, new RunicHostedWindowEventArgs(window)); }
            catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedWindowPolicyFailed(_logger, error, error.GetType().Name); }
        });
    }

    internal void Remove(RunicHostedApplication application)
    {
        lock (_gate) _running.Remove(application);
    }

    private async Task StopAllAsync(string reason)
    {
        RunicHostedApplication[] running;
        lock (_gate) running = [.. _running];
        await Task.WhenAll(running.Select(async application =>
        {
            try { _ = await application.StopCoreAsync(reason, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedApplicationChannelFailed(_logger, error, error.GetType().Name); }
        })).ConfigureAwait(false);
    }

    // RunicDesktopHost.Run returned, or the services are disposed: every guest still running stops.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Dispose();
        await StopAllAsync("host-exit").ConfigureAwait(false);
    }
}
