using System.Linq;

namespace Runic.Application.Bridge;

/// <summary>Internal direct-route transport seam for the experimental window Bridge.</summary>
internal interface IWindowBridgeTransport
{
    WindowBridgeEndpointLease Bind(string route, Func<WindowBridgeArguments, string> handler);
    WindowBridgeEndpointLease BindAsync(string route, Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> handler);
    void Publish(string route, string payload);
}

/// <summary>
/// Optional internal capability of a transport that publishes through a stable
/// native dispatcher rather than a direct native route.
/// </summary>
internal interface IWindowBridgeEndpointTransport : IWindowBridgeTransport
{
    WindowBridgeEndpointLease BindEndpoint(string route, Func<WindowBridgeArguments, string> handler);
    WindowBridgeEndpointLease BindEndpointAsync(string route,
        Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> handler);
    void Publish(WindowBridgeEndpoint endpoint, string payload);
}

/// <summary>
/// Optional internal capability for transports whose browser route descriptors
/// are published as one revisioned manifest. A scope makes an attachment's
/// endpoint additions or removals visible to the browser in one snapshot.
/// </summary>
internal interface IWindowBridgeEndpointManifestBatcher
{
    IDisposable BeginEndpointManifestUpdate();
}

/// <summary>Opaque identity of one live adapter endpoint.</summary>
internal sealed record WindowBridgeEndpoint(string Id, long Generation);

/// <summary>
/// Retires one endpoint idempotently. <see cref="Drain"/> completes after
/// callbacks which entered before retirement have returned.
/// </summary>
internal abstract class WindowBridgeEndpointLease : IDisposable
{
    public abstract WindowBridgeEndpoint Endpoint { get; }
    public abstract Task Drain { get; }
    public abstract void Dispose();

    internal static WindowBridgeEndpointLease Direct(string route, IDisposable resource) => new DirectLease(route, resource);

    private sealed class DirectLease(string route, IDisposable resource) : WindowBridgeEndpointLease
    {
        private IDisposable? _resource = resource;
        public override WindowBridgeEndpoint Endpoint { get; } = new(route, 0);
        public override Task Drain => Task.CompletedTask;
        public override void Dispose() => Interlocked.Exchange(ref _resource, null)?.Dispose();
    }
}

/// <summary>
/// Owns the endpoint leases and View resources created for one internal Window
/// Bridge attachment. Retiring an attachment immediately rejects new endpoint
/// ingress; its completion releases the View resource only after callbacks
/// already admitted by the transport have returned.
/// </summary>
internal sealed class WindowBridgeAttachment : IDisposable
{
    private readonly IDisposable _resource;
    private readonly WindowBridgeEndpointLease[] _endpoints;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _retired;

    private WindowBridgeAttachment(IDisposable resource, WindowBridgeEndpointLease[] endpoints)
    {
        _resource = resource;
        _endpoints = endpoints;
    }

    internal static WindowBridgeAttachment Create(IDisposable resource, params WindowBridgeEndpointLease[] endpoints)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Any(endpoint => endpoint is null)) throw new ArgumentException("An attachment endpoint cannot be null.", nameof(endpoints));
        return new(resource, endpoints);
    }

    internal static WindowBridgeAttachment Create(params WindowBridgeEndpointLease[] endpoints) =>
        Create(EmptyResource.Instance, endpoints);

    public static implicit operator WindowBridgeAttachment(WindowBridgeEndpointLease endpoint) =>
        Create(EmptyResource.Instance, endpoint);

    internal Task Completion => _completion.Task;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0) return;
        List<Exception>? failures = null;
        foreach (WindowBridgeEndpointLease endpoint in _endpoints)
        {
            try { endpoint.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        _ = CompleteAsync(failures);
    }

    private async Task CompleteAsync(List<Exception>? failures)
    {
        try
        {
            try { await Task.WhenAll(_endpoints.Select(endpoint => endpoint.Drain)).ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            try { _resource.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            if (failures is null) _completion.TrySetResult();
            else _completion.TrySetException(new AggregateException(failures));
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    private sealed class EmptyResource : IDisposable
    {
        internal static EmptyResource Instance { get; } = new();
        public void Dispose() { }
    }
}

/// <summary>Arguments supplied by a host-owned direct route.</summary>
internal interface WindowBridgeArguments
{
    WindowBridgeConnection Connection { get; }
    string GetString();
    long GetInt64();
    bool GetBoolean();
}

/// <summary>Identifies one browser client connection for presentation ownership.</summary>
internal sealed record WindowBridgeConnection(string ClientId, string ConnectionId)
{
    internal static WindowBridgeConnection Create(string clientId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return new(clientId, connectionId);
    }
}

/// <summary>
/// Opaque identity created once by a loaded browser document. It is distinct
/// from the physical native callback connection, which can survive reload.
/// </summary>
internal sealed record WindowBridgeDocumentEpoch
{
    private WindowBridgeDocumentEpoch(string value)
    {
        Value = value;
        Ordinal = ulong.Parse(value.AsSpan(0, 16), System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    internal string Value { get; }
    internal ulong Ordinal { get; }

    internal static WindowBridgeDocumentEpoch Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 32 || !value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F'))
            throw new ArgumentException("A document epoch must be a 32-character uppercase hexadecimal value.", nameof(value));
        return new(value);
    }

    public override string ToString() => Value;
}

/// <summary>Result of beginning a document on an authenticated native connection.</summary>
internal sealed record WindowBridgeDocumentAdmission(bool Accepted, WindowBridgeDocumentEpoch Epoch, string? Error)
{
    internal static WindowBridgeDocumentAdmission AcceptedEpoch(WindowBridgeDocumentEpoch epoch) => new(true, epoch, null);
    internal static WindowBridgeDocumentAdmission Rejected(WindowBridgeDocumentEpoch epoch, string error) => new(false, epoch, error);
}
