using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsWebUi;
using WebUiNative = CsWebUi.Native.WebUiNative;
using Runic.Application.Bridge;

namespace Runic.Application.CsWebUi;

/// <summary>
/// One fixed native CS-WebUI dispatcher with a managed table of logical Window
/// Bridge endpoints. Native binding registrations are bounded for the lifetime
/// of the window; endpoint leases remain managed and generation-scoped.
/// </summary>
internal sealed class CsWebUiWindowBridgeTransport : IWindowBridgeTransport, IWindowBridgeAttachmentBatcher, IDisposable
{
    internal const string DispatchBinding = "__runicBridgeDispatch";
    internal const string EndpointHandoffFunction = "__runicBridgeEndpointHandoff";
    private const string DisconnectedReply = "{\"ok\":false,\"state\":null,\"error\":{\"kind\":\"disconnected\",\"message\":\"This view is no longer connected.\"}}";
    private readonly object _gate = new();
    private readonly IWindowBridgeNative _native;
    private readonly byte[] _credential;
    private readonly BridgeLimits _limits;
    private readonly Dictionary<string, EndpointEntry> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EndpointEntry> _routes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _close = new();
    private readonly IDisposable _dispatchBinding;
    private readonly int _nativeRegistrationCalls = 1;
    private long _nextGeneration;
    private long _endpointManifestRevision;
    private int _endpointManifestUpdateDepth;
    private bool _endpointManifestDirty;
    // Bootstrap readers must never observe a partly-mutated batch paired with
    // the preceding revision. This is replaced only with the complete map as
    // an outermost manifest update commits.
    private string _endpointManifestBootstrapJson = "{\"revision\":0,\"endpoints\":{}}";
    private int _peakEndpoints;
    private bool _disposed;

    internal CsWebUiWindowBridgeTransport(WebUiWindow window, string credential, BridgeLimits limits)
        : this(new WebUiNativeAdapter(window), credential, limits)
    {
    }

    // This constructor is intentionally internal for deterministic adapter
    // tests. It represents the small native seam, not an author-facing host.
    internal CsWebUiWindowBridgeTransport(IWindowBridgeNative native, string credential, BridgeLimits limits)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        _credential = Encoding.ASCII.GetBytes(credential);
        if (_credential.Length != 64) throw new ArgumentException("A 64-byte hexadecimal credential is required.", nameof(credential));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _dispatchBinding = _native.BindAsync(DispatchBinding, DispatchAsync);
    }

    // Compatibility counter used by the first host fixture: logical endpoints,
    // rather than native registrations, are visible through this existing name.
    internal int BoundRouteCount { get { lock (_gate) return _endpoints.Count; } }
    internal int NativeRegistrationCalls => _nativeRegistrationCalls;
    internal int PeakLogicalEndpointCount { get { lock (_gate) return _peakEndpoints; } }

    // Host bootstrap uses this only for the internal manual fixture until the
    // generated presentation protocol carries endpoint descriptors itself.
    internal string EndpointManifestJson
    {
        get
        {
            lock (_gate) return EndpointManifestJsonUnsafe();
        }
    }

    /// <summary>
    /// Monotonic snapshot revision for the bootstrap endpoint map. A late native
    /// JavaScript handoff with an older revision cannot revive a retired endpoint
    /// in a newly loaded document.
    /// </summary>
    internal long EndpointManifestRevision { get { lock (_gate) return _endpointManifestRevision; } }

    /// <summary>
    /// Defers browser handoff while one attachment creates or retires several
    /// routes. The managed table remains authoritative throughout; committing
    /// the outermost scope emits its one newest full snapshot.
    /// </summary>
    public IDisposable BeginAttachmentUpdate()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            checked { _endpointManifestUpdateDepth++; }
        }
        return new AttachmentUpdate(this);
    }

    /// <summary>Atomic bootstrap snapshot for a freshly loaded browser document.</summary>
    internal string EndpointManifestBootstrapJson
    {
        get
        {
            lock (_gate) return _endpointManifestBootstrapJson;
        }
    }

    public WindowBridgeEndpointLease Bind(string route, Func<WindowBridgeArguments, string> handler) => Add(route, handler, null);

    public WindowBridgeEndpointLease BindAsync(string route, Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> handler) =>
        Add(route, null, handler);

    internal WindowBridgeEndpointLease BindEndpoint(string route, Func<WindowBridgeArguments, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(route, handler, null);
    }

    internal WindowBridgeEndpointLease BindEndpointAsync(string route,
        Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(route, null, handler);
    }

    internal CsWebUiWindowBridgeEndpoint DescriptorFor(WindowBridgeEndpointLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease is not Lease local || !ReferenceEquals(local.Owner, this))
            throw new ArgumentException("The endpoint lease was not created by this CS-WebUI transport.", nameof(lease));
        return local.Entry.Endpoint;
    }

    private Lease Add(string route,
        Func<WindowBridgeArguments, string>? sync,
        Func<WindowBridgeArguments, CancellationToken, ValueTask<string>>? async)
    {
        ValidateRoute(route);
        string? handoff;
        EndpointEntry entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_routes.ContainsKey(route)) throw new InvalidOperationException("A logical Window Bridge route is already active.");
            var endpoint = new CsWebUiWindowBridgeEndpoint(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), ++_nextGeneration);
            entry = new EndpointEntry(route, endpoint, sync, async);
            _endpoints.Add(endpoint.Id, entry);
            _routes.Add(route, entry);
            _peakEndpoints = Math.Max(_peakEndpoints, _endpoints.Count);
            handoff = AdvanceEndpointManifestUnsafe();
        }
        PublishEndpointManifest(handoff);
        return new Lease(this, entry);
    }

    internal bool CredentialMatches(string supplied) =>
        Encoding.ASCII.GetByteCount(supplied) == 64
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), _credential);

    private async ValueTask<string> DispatchAsync(IWindowBridgeNativeCallback callback, CancellationToken nativeCancellation)
    {
        EndpointEntry? entry = null;
        var admitted = false;
        try
        {
            if (!TryAuthenticate(callback) || !TryReadEnvelope(callback.GetBytes(1), out DispatchEnvelope envelope))
                throw new InvalidOperationException("Invalid Window Bridge callback.");

            lock (_gate)
            {
                if (_disposed || !_endpoints.TryGetValue(envelope.Endpoint, out entry)
                    || entry.Retired || entry.Endpoint.Generation != envelope.Generation)
                    return DisconnectedReply;
                entry.InFlight++;
                admitted = true;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(nativeCancellation, _close.Token);
            var arguments = new Arguments(callback, envelope.Payload);
            string reply = entry.Async is { } async
                ? await async(arguments, linked.Token).ConfigureAwait(false)
                : entry.Sync!(arguments);
            return ValidateReply(reply);
        }
        catch (OperationCanceledException) when (nativeCancellation.IsCancellationRequested || _close.IsCancellationRequested)
        {
            return DisconnectedReply;
        }
        catch
        {
            callback.CloseClient();
            return DisconnectedReply;
        }
        finally
        {
            if (admitted) ReleaseInvocation(entry!);
        }
    }

    private bool TryAuthenticate(IWindowBridgeNativeCallback callback) =>
        callback.IsCallback && callback.ArgumentCount == 2
        && callback.ArgumentLength(0) == 64
        && callback.ArgumentLength(1) <= _limits.MaxFrameBytes
        && CryptographicOperations.FixedTimeEquals(callback.GetBytes(0), _credential);

    private bool TryReadEnvelope(ReadOnlySpan<byte> bytes, out DispatchEnvelope envelope)
    {
        envelope = default!;
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = _limits.MaxDepth });
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object) return false;
            int seen = 0;
            int version = 0;
            string? endpoint = null;
            long generation = 0;
            JsonElement payload = default;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "v" when (seen & 1) == 0 && property.Value.TryGetInt32(out version): seen |= 1; break;
                    case "endpoint" when (seen & 2) == 0 && property.Value.ValueKind is JsonValueKind.String:
                        endpoint = property.Value.GetString(); seen |= 2; break;
                    case "generation" when (seen & 4) == 0 && property.Value.TryGetInt64(out generation): seen |= 4; break;
                    case "payload" when (seen & 8) == 0: payload = property.Value.Clone(); seen |= 8; break;
                    default: return false;
                }
            }
            if (seen != 15 || version != 1 || generation < 1 || !IsEndpointId(endpoint)
                || !IsWithinPayloadLimits(payload)) return false;
            envelope = new(endpoint!, generation, payload);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private bool IsWithinPayloadLimits(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return Encoding.UTF8.GetByteCount(value.GetString()!) <= _limits.MaxStringBytes;
            case JsonValueKind.Array:
                if (value.GetArrayLength() > _limits.MaxCollectionItems) return false;
                foreach (JsonElement item in value.EnumerateArray())
                    if (!IsWithinPayloadLimits(item)) return false;
                return true;
            case JsonValueKind.Object:
            {
                var count = 0;
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (++count > _limits.MaxCollectionItems
                        || Encoding.UTF8.GetByteCount(property.Name) > _limits.MaxStringBytes
                        || !IsWithinPayloadLimits(property.Value)) return false;
                }
                return true;
            }
            default:
                return true;
        }
    }

    private static bool IsEndpointId(string? value) => value is { Length: 32 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private void Release(EndpointEntry entry)
    {
        string? handoff = null;
        lock (_gate)
        {
            if (entry.Retired) return;
            entry.Retired = true;
            _endpoints.Remove(entry.Endpoint.Id);
            _routes.Remove(entry.Route);
            if (entry.InFlight == 0) entry.Drained.TrySetResult();
            handoff = AdvanceEndpointManifestUnsafe();
        }
        PublishEndpointManifest(handoff);
    }

    private string? AdvanceEndpointManifestUnsafe()
    {
        if (_endpointManifestUpdateDepth != 0)
        {
            _endpointManifestDirty = true;
            return null;
        }
        return CreateEndpointManifestHandoffUnsafe();
    }

    private string CreateEndpointManifestHandoffUnsafe()
    {
        long revision = checked(++_endpointManifestRevision);
        string endpoints = EndpointManifestJsonUnsafe();
        _endpointManifestBootstrapJson = WindowBridgeJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WritePropertyName("endpoints");
            writer.WriteRawValue(endpoints, skipInputValidation: false);
            writer.WriteEndObject();
        });
        return WindowBridgeJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteNumber("revision", revision);
            writer.WritePropertyName("endpoints");
            writer.WriteRawValue(endpoints, skipInputValidation: false);
            writer.WriteEndObject();
        });
    }

    private string EndpointManifestJsonUnsafe() => WindowBridgeJson.Write(writer =>
    {
        writer.WriteStartObject();
        foreach ((string route, EndpointEntry entry) in _routes)
        {
            writer.WritePropertyName(route);
            writer.WriteStartObject();
            writer.WriteString("endpoint", entry.Endpoint.Id);
            writer.WriteNumber("generation", entry.Endpoint.Generation);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    });

    private void PublishEndpointManifest(string? handoff)
    {
        if (handoff is null) return;
        try
        {
            // A send is not a browser execution acknowledgment, and one bad
            // peer must not roll back a route used by another peer. The next
            // document-begin reply reconciles the authoritative full map.
            _native.RunJavaScript($"globalThis.{EndpointHandoffFunction}?.(JSON.parse({WindowBridgeJson.StringLiteral(handoff)}));");
        }
        catch (Exception)
        {
            // A disconnected browser cannot receive the handoff. The fixed
            // dispatcher still rejects retired or unknown descriptors.
        }
    }

    private void EndEndpointManifestUpdate()
    {
        string? handoff = null;
        lock (_gate)
        {
            if (_endpointManifestUpdateDepth == 0)
                throw new InvalidOperationException("The Window Bridge endpoint manifest update was already completed.");
            _endpointManifestUpdateDepth--;
            if (_endpointManifestUpdateDepth == 0 && _endpointManifestDirty)
            {
                _endpointManifestDirty = false;
                if (!_disposed) handoff = CreateEndpointManifestHandoffUnsafe();
            }
        }
        PublishEndpointManifest(handoff);
    }

    private void ReleaseInvocation(EndpointEntry entry)
    {
        lock (_gate)
        {
            entry.InFlight--;
            if (entry.Retired && entry.InFlight == 0) entry.Drained.TrySetResult();
        }
    }

    public void Dispose()
    {
        EndpointEntry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _endpoints.Values.ToArray();
            _endpoints.Clear();
            _routes.Clear();
            foreach (EndpointEntry entry in entries)
            {
                entry.Retired = true;
                if (entry.InFlight == 0) entry.Drained.TrySetResult();
            }
        }
        try { ObserveCancellationAndDispose(_close.CancelAsync()); }
        catch (Exception) { _close.Dispose(); }
        _dispatchBinding.Dispose();
    }

    private void ObserveCancellationAndDispose(Task cancellation) =>
        _ = cancellation.ContinueWith(static (finished, owner) =>
        {
            _ = finished.Exception;
            ((CancellationTokenSource)owner!).Dispose();
        }, _close, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ValidateRoute(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        if (Encoding.UTF8.GetByteCount(route) > _limits.MaxStringBytes)
            throw new ArgumentException("The Window Bridge route exceeds the configured limit.", nameof(route));
    }

    private string ValidateReply(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (Encoding.UTF8.GetByteCount(reply) > _limits.MaxFrameBytes)
            throw new InvalidOperationException("The Window Bridge callback reply exceeds the configured limit.");
        return reply;
    }

    internal sealed class EndpointEntry
    {
        internal EndpointEntry(string route, CsWebUiWindowBridgeEndpoint endpoint,
            Func<WindowBridgeArguments, string>? sync,
            Func<WindowBridgeArguments, CancellationToken, ValueTask<string>>? async)
        {
            Route = route;
            Endpoint = endpoint;
            Sync = sync;
            Async = async;
        }

        internal string Route { get; }
        internal CsWebUiWindowBridgeEndpoint Endpoint { get; }
        internal Func<WindowBridgeArguments, string>? Sync { get; }
        internal Func<WindowBridgeArguments, CancellationToken, ValueTask<string>>? Async { get; }
        internal bool Retired;
        internal int InFlight;
        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class Lease : WindowBridgeEndpointLease
    {
        private readonly CsWebUiWindowBridgeTransport _owner;
        private CsWebUiWindowBridgeTransport? _releaseOwner;
        internal Lease(CsWebUiWindowBridgeTransport owner, EndpointEntry entry)
        {
            _owner = owner;
            _releaseOwner = owner;
            Entry = entry;
        }

        internal EndpointEntry Entry { get; }
        internal CsWebUiWindowBridgeTransport Owner => _owner;
        public override Task Drain => Entry.Drained.Task;
        public override void Dispose() => Interlocked.Exchange(ref _releaseOwner, null)?.Release(Entry);
    }

    private sealed class AttachmentUpdate(CsWebUiWindowBridgeTransport owner) : IDisposable
    {
        private CsWebUiWindowBridgeTransport? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndEndpointManifestUpdate();
    }

    private sealed class Arguments(IWindowBridgeNativeCallback callback, JsonElement payload) : WindowBridgeArguments
    {
        public WindowBridgeConnection Connection { get; } = WindowBridgeConnection.Create(
            callback.ClientId.ToString(CultureInfo.InvariantCulture), callback.ConnectionId.ToString(CultureInfo.InvariantCulture));
        public string GetString() => payload.GetRawText();
        public long GetInt64() => payload.GetInt64();
        public bool GetBoolean() => payload.GetBoolean();
    }

    private sealed record DispatchEnvelope(string Endpoint, long Generation, JsonElement Payload);

    private sealed class WebUiNativeAdapter(WebUiWindow window) : IWindowBridgeNative
    {
        private readonly WebUiWindow _window = window ?? throw new ArgumentNullException(nameof(window));
        public IDisposable BindAsync(string name, Func<IWindowBridgeNativeCallback, CancellationToken, ValueTask<string>> handler) =>
            _window.BindAsync(name, async (eventData, cancellationToken) =>
                WebUiResult.FromString(await handler(new Callback(eventData), cancellationToken).ConfigureAwait(false)));
        public void RunJavaScript(string script) => _window.RunJavaScript(script);

        private sealed class Callback(WebUiEvent eventData) : IWindowBridgeNativeCallback
        {
            public bool IsCallback => eventData.EventType == WebUiEventType.Callback;
            public int ArgumentCount => checked((int)eventData.ArgumentCount);
            public ulong ClientId => checked((ulong)eventData.ClientId);
            public ulong ConnectionId => checked((ulong)eventData.ConnectionId);
            public int ArgumentLength(int index) => checked((int)WebUiNative.InterfaceGetSizeAt(eventData.WindowId, eventData.EventNumber, checked((nuint)index)));
            public byte[] GetBytes(int index) => eventData.GetBytes(checked((nuint)index));
            public void CloseClient() => eventData.CloseClient();
        }
    }
}

/// <summary>Adapter-local descriptor carried only by the fixed CS-WebUI envelope.</summary>
internal sealed record CsWebUiWindowBridgeEndpoint(string Id, long Generation);


/// <summary>Small native seam for deterministic adapter tests.</summary>
internal interface IWindowBridgeNative
{
    IDisposable BindAsync(string name, Func<IWindowBridgeNativeCallback, CancellationToken, ValueTask<string>> handler);
    void RunJavaScript(string script);
}

internal interface IWindowBridgeNativeCallback
{
    bool IsCallback { get; }
    int ArgumentCount { get; }
    ulong ClientId { get; }
    ulong ConnectionId { get; }
    int ArgumentLength(int index);
    byte[] GetBytes(int index);
    void CloseClient();
}
