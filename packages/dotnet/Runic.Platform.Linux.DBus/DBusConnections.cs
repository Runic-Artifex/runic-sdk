using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Runic.Platform.Linux.DBus;

/// <summary>The application's shared connections to the session and system buses.</summary>
/// <remarks>
/// <see cref="DBusServiceCollectionExtensions.AddRunicDBus"/> registers one instance for the application. Each bus is
/// connected when first requested and shared by every caller; the service provider closes the connections when it is
/// disposed. A connection that closed, for example because the bus restarted, is replaced on the next request. Do not
/// dispose a shared connection.
/// </remarks>
public interface IDBusConnections
{
    /// <summary>Gets the shared connection to a bus, connecting on first use.</summary>
    /// <returns>The connection, or why there is none, as <see cref="DBusConnection.ConnectAsync(DBusBus, CancellationToken)"/> reports it.</returns>
    ValueTask<PlatformResult<DBusConnection>> GetAsync(DBusBus bus, CancellationToken cancellationToken = default);
}

/// <summary>Registers D-Bus services for dependency injection.</summary>
public static class DBusServiceCollectionExtensions
{
    /// <summary>Registers one <see cref="IDBusConnections"/> for the application. An existing registration is kept.</summary>
    public static IServiceCollection AddRunicDBus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDBusConnections>(static _ => new DBusConnections());
        return services;
    }
}

internal sealed class DBusConnections : IDBusConnections, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DBusConnection?[] _connections = new DBusConnection?[2];
    private bool _disposed;

    public async ValueTask<PlatformResult<DBusConnection>> GetAsync(DBusBus bus, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(bus)) throw new ArgumentOutOfRangeException(nameof(bus));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connections[(int)bus] is { Disconnected.IsCancellationRequested: false } current)
                return new PlatformResult<DBusConnection>.Success(current);
            if (_connections[(int)bus] is { } closed) await closed.DisposeAsync().ConfigureAwait(false);
            _connections[(int)bus] = null;
            var result = await DBusConnection.ConnectAsync(bus, cancellationToken).ConfigureAwait(false);
            if (result is PlatformResult<DBusConnection>.Success(var connection)) _connections[(int)bus] = connection;
            return result;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var connection in _connections)
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
