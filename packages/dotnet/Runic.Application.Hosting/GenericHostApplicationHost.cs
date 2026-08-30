using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Runic.Application.Hosting;

/// <summary>Adapts one explicitly supplied Generic Host to the application host boundary.</summary>
public sealed class GenericHostApplicationHost(IHost host) : IApplicationHost
{
    private readonly IHost _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <inheritdoc />
    public async ValueTask StartAsync(ApplicationCompositionManifest manifest, ReadOnlyMemory<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await _host.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WaitForShutdownAsync(CancellationToken cancellationToken) => await _host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken) => await _host.StopAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }
}
