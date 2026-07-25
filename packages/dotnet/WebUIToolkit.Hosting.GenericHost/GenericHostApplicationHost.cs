using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace WebUIToolkit.Hosting;

/// <summary>Adapts one Generic Host instance to the dependency-neutral lifecycle kernel.</summary>
public sealed class GenericHostApplicationHost : IApplicationHost
{
    private readonly IHost _host;
    private readonly IHostApplicationLifetime _lifetime;
    private CancellationTokenRegistration _stoppingRegistration;
    private int _bound;
    private int _disposed;

    /// <summary>Initializes the bridge without starting the Generic Host.</summary>
    public GenericHostApplicationHost(IHost host, IHostApplicationLifetime lifetime)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    /// <summary>
    /// Forwards Generic Host stopping to the kernel controller. The controller remains the
    /// sole owner of first-signal selection.
    /// </summary>
    public void BindStopController(IApplicationStopController stopController)
    {
        ArgumentNullException.ThrowIfNull(stopController);
        if (Interlocked.Exchange(ref _bound, 1) != 0)
        {
            throw new InvalidOperationException("The Generic Host bridge is already bound.");
        }

        _stoppingRegistration = _lifetime.ApplicationStopping.UnsafeRegister(
            static state => ((IApplicationStopController)state!).RequestStop(StopReason.HostStopping),
            stopController);
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _host.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            await _host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stoppingRegistration.Dispose();
        if (_host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _host.Dispose();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
