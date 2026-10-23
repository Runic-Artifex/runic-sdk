using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using Runic.Application.Views;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>Adapts a Desktop surface's removable capabilities to the Views transport.</summary>
/// <remarks>
/// Disposing the transport removes every capability it registered and stops state
/// delivery, while the surface itself stays open. A window uses this to close its
/// browser routes before accepted operations have drained.
/// <para>
/// Published state reaches the page as data that the served bridge script parses with <c>JSON.parse</c> before it
/// calls <c>__{name}Changed</c>, so a Content-Security-Policy without <c>'unsafe-eval'</c> receives it.
/// </para>
/// </remarks>
public sealed class DesktopBridgeTransport : IAsyncBridgeTransport, IBridgeUtf8Transport, IDisposable
{
    private readonly object _gate = new();
    private readonly DesktopSurface _surface;
    private readonly Func<string, string, Task> _push;
    private readonly Func<string, ReadOnlyMemory<byte>, Task> _pushUtf8;
    private readonly ILogger? _logger;
    private readonly HashSet<Registration> _registrations = [];
    private readonly Dictionary<string, Delivery> _deliveries = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a transport for <paramref name="surface"/>.</summary>
    public DesktopBridgeTransport(DesktopSurface surface)
        : this(surface ?? throw new ArgumentNullException(nameof(surface)), (route, state) => surface.PushStateAsync(route, state),
            (route, state) => surface.PushStateUtf8Async(route, state))
    {
    }

    // The log category of Desktop Bridge delivery failures.
    internal const string LogCategory = "Runic.Application.Views.Desktop";

    // Without a logger, delivery failures are written to System.Diagnostics.Trace.
    internal DesktopBridgeTransport(DesktopSurface surface, ILogger? logger)
        : this(surface ?? throw new ArgumentNullException(nameof(surface)), (route, state) => surface.PushStateAsync(route, state),
            (route, state) => surface.PushStateUtf8Async(route, state), logger)
    {
    }

    // The state senders, which take the route and the state's JSON, are replaceable for headless tests of delivery
    // order. Without a UTF-8 sender, UTF-8 states are decoded for the string sender.
    internal DesktopBridgeTransport(DesktopSurface surface, Func<string, string, Task> push, ILogger? logger = null)
        : this(surface, push, (route, state) => push(route, Encoding.UTF8.GetString(state.Span)), logger)
    {
    }

    internal DesktopBridgeTransport(DesktopSurface surface, Func<string, string, Task> push,
        Func<string, ReadOnlyMemory<byte>, Task> pushUtf8, ILogger? logger = null)
    {
        _surface = surface;
        _push = push;
        _pushUtf8 = pushUtf8;
        _logger = logger;
    }

    internal int RegisteredRouteCount
    {
        get { lock (_gate) return _registrations.Count; }
    }

    /// <inheritdoc />
    public IDisposable Bind(string name, Func<IBridgeArguments, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(name, (invocation, _) =>
            ValueTask.FromResult(PresentationResult.FromString(handler(new DesktopBridgeArguments(invocation)))));
    }

    /// <inheritdoc />
    public IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(name, async (invocation, token) =>
            PresentationResult.FromString(await handler(new DesktopBridgeArguments(invocation), token)
                .ConfigureAwait(false)));
    }

    /// <summary>
    /// Queues a state publication without blocking the caller. A route has at most one
    /// state in flight; a newer state replaces one that has not been sent yet.
    /// </summary>
    public void Publish(string name, string stateJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stateJson);
        lock (_gate)
        {
            if (_disposed) return;
            if (_deliveries.TryGetValue(name, out var running))
            {
                running.Pending = stateJson;
                return;
            }
            _deliveries.Add(name, new Delivery());
        }
        _ = DeliverAsync(name, stateJson);
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(string name, string stateJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stateJson);
        lock (_gate) if (_disposed) return;
        await _push(name, stateJson).ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers one frame without coalescing it with other frames, as <see cref="PublishAsync"/> does, from its UTF-8
    /// encoding. The browser receives the same bytes as from <see cref="PublishAsync"/>.
    /// </summary>
    public async ValueTask PublishUtf8Async(string name, ReadOnlyMemory<byte> stateUtf8, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate) if (_disposed) return;
        cancellationToken.ThrowIfCancellationRequested();
        await _pushUtf8(name, stateUtf8).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes every registered capability and stops state delivery; the surface stays open.</summary>
    public void Dispose()
    {
        Registration[] registrations;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            registrations = [.. _registrations];
            _registrations.Clear();
            _deliveries.Clear();
        }
        foreach (var registration in registrations) registration.Release();
    }

    private Registration Register(string name, PresentationCapabilityHandler handler)
    {
        lock (_gate)
        {
            // A closing window can still attach content while accepted work
            // drains. Its routes are inert once the transport was disposed.
            if (_disposed) return Registration.Inert;
            var registration = new Registration(this, _surface.RegisterCapability(name, handler));
            _registrations.Add(registration);
            return registration;
        }
    }

    private async Task DeliverAsync(string name, string stateJson)
    {
        while (true)
        {
            try { await _push(name, stateJson).ConfigureAwait(false); }
            catch (Exception error)
            {
                if (_logger is null) Trace.TraceError($"Bridge snapshot delivery for {name} failed: {error}");
                else DesktopLog.SnapshotDeliveryFailed(_logger, error, name,
                    error.GetType().FullName ?? error.GetType().Name);
            }

            lock (_gate)
            {
                if (_disposed || !_deliveries.TryGetValue(name, out var delivery) || delivery.Pending is null)
                {
                    _deliveries.Remove(name);
                    return;
                }
                stateJson = delivery.Pending;
                delivery.Pending = null;
            }
        }
    }

    private void Remove(Registration registration)
    {
        lock (_gate) _registrations.Remove(registration);
    }

    private sealed class Delivery
    {
        public string? Pending { get; set; }
    }

    private sealed class Registration(DesktopBridgeTransport? owner, IDisposable? capability) : IDisposable
    {
        public static Registration Inert { get; } = new(null, null);
        private IDisposable? _capability = capability;

        public void Dispose()
        {
            owner?.Remove(this);
            Release();
        }

        public void Release() => Interlocked.Exchange(ref _capability, null)?.Dispose();
    }

    private sealed class DesktopBridgeArguments(PresentationInvocation invocation) : IBridgeArguments
    {
        // PresentationSession.Id is WebUiEvent.ConnectionId, allocated for an
        // authenticated WebSocket connection. It is deliberately not the
        // engine's private session GUID. Desktop has one browser client for a
        // connection, so the trusted client and connection identities are the
        // same value; reconnecting receives a different key.
        private readonly string _connectionKey = invocation.Session.Id.ToString(CultureInfo.InvariantCulture);
        private int _nextArgument;

        public string ClientKey => _connectionKey;
        public string ConnectionKey => _connectionKey;
        public long GetInt64() => invocation.GetInt64(_nextArgument++);
        public bool GetBoolean() => invocation.GetBoolean(_nextArgument++);
        public string GetString() => invocation.GetString(_nextArgument++);
    }
}
