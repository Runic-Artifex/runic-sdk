using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;

namespace WebUIToolkit.Hosting.WebUi;

/// <summary>Configures the finite resources owned by one browser MVVM transport.</summary>
public sealed record MvvmWebUiTransportOptions
{
    /// <summary>Gets the maximum number of patch and terminal frames waiting for a writer.</summary>
    public int WriterCapacity { get; init; } = 32;

    /// <summary>Gets the maximum number of recently published terminal request identifiers.</summary>
    public int TombstoneCapacity { get; init; } = 64;

    /// <summary>Gets the strict codec limits applied at the raw byte boundary.</summary>
    public MvvmLimits CodecLimits { get; init; } = MvvmLimits.Default;

    /// <summary>Gets an optional sink for closed, low-cardinality diagnostic outcomes.</summary>
    public Action<MvvmTransportDiagnostic>? DiagnosticSink { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WriterCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TombstoneCapacity);
        ArgumentNullException.ThrowIfNull(CodecLimits);
        CodecLimits.Validate();
    }
}

/// <summary>Identifies the authenticated browser view routed to a retained MVVM session.</summary>
public readonly struct MvvmTransportRoute
{
    /// <summary>Creates an authenticated route.</summary>
    public MvvmTransportRoute(MvvmSessionId sessionId, Guid viewId, string capabilityToken)
    {
        if (viewId == Guid.Empty)
        {
            throw new ArgumentException("A view identifier cannot be empty.", nameof(viewId));
        }

        ArgumentNullException.ThrowIfNull(capabilityToken);
        SessionId = sessionId;
        ViewId = viewId;
        CapabilityToken = capabilityToken;
    }

    /// <summary>Gets the retained session identifier.</summary>
    public MvvmSessionId SessionId { get; }

    /// <summary>Gets the browser view identifier.</summary>
    public Guid ViewId { get; }

    /// <summary>Gets the invocation capability presented by the browser.</summary>
    public string CapabilityToken { get; }
}

/// <summary>Closed admission outcomes that never contain attacker-controlled data.</summary>
public enum MvvmTransportRejection
{
    /// <summary>The request was admitted and dispatched.</summary>
    None,

    /// <summary>The handshake, session, view, or capability was not accepted.</summary>
    AuthenticationFailed,

    /// <summary>The bounded writer cannot reserve every possible output frame.</summary>
    OutputLimitExceeded,

    /// <summary>An authoritative replacement snapshot is required before mutations resume.</summary>
    SnapshotRequired,

    /// <summary>The transport has closed and no longer accepts work.</summary>
    SessionClosed,

    /// <summary>A required negotiated transport capability is absent.</summary>
    CapabilityNotNegotiated,
}

/// <summary>A transport dispatch outcome with either one session response or a pre-dispatch rejection.</summary>
public sealed record MvvmTransportDispatchResult
{
    private MvvmTransportDispatchResult(MvvmResponse? response, MvvmTransportRejection rejection)
    {
        Response = response;
        Rejection = rejection;
    }

    /// <summary>Gets whether the request reached the retained session.</summary>
    public bool WasDispatched => Response is not null;

    /// <summary>Gets the authoritative response when the request was dispatched.</summary>
    public MvvmResponse? Response { get; }

    /// <summary>Gets the closed pre-dispatch rejection.</summary>
    public MvvmTransportRejection Rejection { get; }

    internal static MvvmTransportDispatchResult Dispatched(MvvmResponse response) =>
        new(response, MvvmTransportRejection.None);

    internal static MvvmTransportDispatchResult Rejected(MvvmTransportRejection rejection) =>
        new(null, rejection);
}

/// <summary>The two ordered output frame categories owned by the transport writer.</summary>
public enum MvvmTransportFrameKind
{
    /// <summary>A committed atomic patch published before its correlated terminal.</summary>
    Patch,

    /// <summary>The exactly-once terminal outcome for one admitted request.</summary>
    Terminal,
}

/// <summary>One bounded writer entry.</summary>
public sealed record MvvmTransportFrame(
    MvvmTransportFrameKind Kind,
    MvvmRequestId RequestId,
    long FromRevision,
    long Revision,
    MvvmResponse Response);

/// <summary>Closed diagnostic outcomes suitable for bounded-cardinality logs and metrics.</summary>
public enum MvvmTransportDiagnostic
{
    /// <summary>A route or capability failed authentication.</summary>
    AuthenticationFailed,

    /// <summary>A raw frame failed strict protocol decoding.</summary>
    CodecRejected,

    /// <summary>The bounded writer could not reserve a complete response.</summary>
    OutputLimited,

    /// <summary>A mutation was stopped until snapshot replacement completes.</summary>
    SnapshotRequired,

    /// <summary>A requested operation was not negotiated by the client handshake.</summary>
    CapabilityNotNegotiated,
}

/// <summary>
/// Routes one authenticated browser view to one retained MVVM session while owning strict
/// codec admission, capability negotiation, reconnect sequencing, and a bounded output writer.
/// </summary>
public sealed class MvvmWebUiTransport : IAsyncDisposable
{
    private const string PatchesCapability = "patches";
    private const string CancellationCapability = "cancellation";
    private readonly object _gate = new();
    private readonly IMvvmSession _session;
    private readonly Guid _viewId;
    private readonly MvvmWebUiTransportOptions _options;
    private readonly List<MvvmTransportFrame> _output = [];
    private readonly Queue<MvvmRequestId> _tombstones = new();
    private HashSet<string> _negotiatedCapabilities = new(StringComparer.Ordinal);
    private JsonElement? _localSnapshot;
    private int _reservedOutput;
    private bool _handshakeAccepted;
    private bool _snapshotRequired;
    private bool _closed;
    private int _disposed;

    /// <summary>Creates a transport for one already-open retained session and browser view.</summary>
    public MvvmWebUiTransport(
        IMvvmSession session,
        Guid viewId,
        MvvmWebUiTransportOptions? options = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (viewId == Guid.Empty)
        {
            throw new ArgumentException("A view identifier cannot be empty.", nameof(viewId));
        }

        _viewId = viewId;
        _options = options ?? new MvvmWebUiTransportOptions();
        _options.Validate();
    }

    /// <summary>Gets the finite output writer capacity.</summary>
    public int WriterCapacity => _options.WriterCapacity;

    /// <summary>Gets the number of writer entries currently awaiting delivery.</summary>
    public int BufferedFrameCount
    {
        get
        {
            lock (_gate)
            {
                return _output.Count;
            }
        }
    }

    /// <summary>Gets the number of retained terminal tombstones.</summary>
    public int TombstoneCount
    {
        get
        {
            lock (_gate)
            {
                return _tombstones.Count;
            }
        }
    }

    /// <summary>Gets whether an authoritative snapshot must replace client state before mutation.</summary>
    public bool SnapshotRequired
    {
        get
        {
            lock (_gate)
            {
                return _snapshotRequired;
            }
        }
    }

    /// <summary>Gets the last authoritative replacement snapshot without merging stale state.</summary>
    public JsonElement? LocalSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _localSnapshot?.Clone();
            }
        }
    }

    /// <summary>Gets whether this v1 transport promises retained patch replay.</summary>
    public static bool ReplaySupported => false;

    /// <summary>
    /// Strictly decodes a handshake, records its closed capability set, and returns canonical bytes.
    /// Invalid input never reaches session dispatch.
    /// </summary>
    public bool TryAcceptHandshake(ReadOnlySpan<byte> utf8, out byte[]? canonicalFrame)
    {
        ThrowIfDisposed();
        if (!MvvmMessageCodec.TryDecodeClient(
            utf8,
            out MvvmWireMessage? message,
            out _,
            _options.CodecLimits)
            || !string.Equals(message!.Kind, "handshake", StringComparison.Ordinal))
        {
            canonicalFrame = null;
            Observe(MvvmTransportDiagnostic.CodecRejected);
            return false;
        }

        string[] capabilities = message.Payload
            .GetProperty("capabilities")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        canonicalFrame = MvvmMessageCodec.Encode(message, _options.CodecLimits);
        lock (_gate)
        {
            if (_closed)
            {
                canonicalFrame = null;
                return false;
            }

            _negotiatedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal);
            _handshakeAccepted = true;
        }

        return true;
    }

    /// <summary>Begins retained-session reconnect and blocks mutation traffic until replacement.</summary>
    public bool BeginReconnect(MvvmTransportRoute route)
    {
        ThrowIfDisposed();
        if (!Authenticates(route))
        {
            Observe(MvvmTransportDiagnostic.AuthenticationFailed);
            return false;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            _handshakeAccepted = false;
            _negotiatedCapabilities.Clear();
            _snapshotRequired = true;
            _localSnapshot = null;
            return true;
        }
    }

    /// <summary>Dispatches one authenticated request after reserving its complete writer bound.</summary>
    public async ValueTask<MvvmTransportDispatchResult> DispatchAsync(
        MvvmTransportRoute route,
        MvvmRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        MvvmTransportRejection rejection = Admit(route, request);
        if (rejection != MvvmTransportRejection.None)
        {
            ObserveRejection(rejection);
            return MvvmTransportDispatchResult.Rejected(rejection);
        }

        int reservation = MaximumFrameCount(request);
        MvvmResponse response;
        try
        {
            response = await _session.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseReservation(reservation);
            throw;
        }

        PublishReserved(
            response,
            request is MvvmMutationRequest mutation ? mutation.BaseRevision : response.Revision,
            reservation,
            response.Fault?.Code == MvvmFaultCodes.RevisionStale);

        return MvvmTransportDispatchResult.Dispatched(response);
    }

    /// <summary>
    /// Requests an authoritative retained-session snapshot and atomically replaces local state.
    /// </summary>
    public async ValueTask<MvvmTransportDispatchResult> RecoverFromSnapshotAsync(
        MvvmTransportRoute route,
        MvvmRequestId requestId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var request = new MvvmSnapshotRequest(requestId);
        MvvmTransportRejection rejection = Admit(route, request, allowSnapshotRecovery: true);
        if (rejection != MvvmTransportRejection.None)
        {
            ObserveRejection(rejection);
            return MvvmTransportDispatchResult.Rejected(rejection);
        }

        MvvmResponse response;
        try
        {
            response = await _session.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseReservation(1);
            throw;
        }

        PublishReserved(response, response.Revision, 1);
        if (response.Succeeded && response.Payload is JsonElement replacement)
        {
            lock (_gate)
            {
                _localSnapshot = replacement.Clone();
                _snapshotRequired = false;
            }
        }

        return MvvmTransportDispatchResult.Dispatched(response);
    }

    /// <summary>Removes all currently buffered frames in publication order for the actual writer.</summary>
    public IReadOnlyList<MvvmTransportFrame> DrainOutput()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            MvvmTransportFrame[] drained = _output.ToArray();
            _output.Clear();
            return new ReadOnlyCollection<MvvmTransportFrame>(drained);
        }
    }

    /// <summary>Tests whether a terminal request identifier remains within the finite retention window.</summary>
    public bool HasTombstone(MvvmRequestId requestId)
    {
        lock (_gate)
        {
            return _tombstones.Contains(requestId);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _closed = true;
            _output.Clear();
            _reservedOutput = 0;
            _negotiatedCapabilities.Clear();
        }

        await _session.DisposeAsync().ConfigureAwait(false);
    }

    private MvvmTransportRejection Admit(
        MvvmTransportRoute route,
        MvvmRequest request,
        bool allowSnapshotRecovery = false)
    {
        if (!Authenticates(route))
        {
            return MvvmTransportRejection.AuthenticationFailed;
        }

        int reservation = MaximumFrameCount(request);
        lock (_gate)
        {
            if (_closed)
            {
                return MvvmTransportRejection.SessionClosed;
            }

            if (!_handshakeAccepted)
            {
                return MvvmTransportRejection.AuthenticationFailed;
            }

            if (_snapshotRequired && request is MvvmMutationRequest && !allowSnapshotRecovery)
            {
                return MvvmTransportRejection.SnapshotRequired;
            }

            if (request is MvvmMutationRequest && !_negotiatedCapabilities.Contains(PatchesCapability))
            {
                return MvvmTransportRejection.CapabilityNotNegotiated;
            }

            if (request is MvvmCancelRequest && !_negotiatedCapabilities.Contains(CancellationCapability))
            {
                return MvvmTransportRejection.CapabilityNotNegotiated;
            }

            if (_output.Count + _reservedOutput + reservation > _options.WriterCapacity)
            {
                return MvvmTransportRejection.OutputLimitExceeded;
            }

            _reservedOutput += reservation;
            return MvvmTransportRejection.None;
        }
    }

    private bool Authenticates(MvvmTransportRoute route)
    {
        bool sessionMatches = route.SessionId == _session.Id;
        bool viewMatches = route.ViewId == _viewId;
        bool capabilityMatches = _session.Authorizes(route.CapabilityToken);
        return sessionMatches & viewMatches & capabilityMatches;
    }

    private void PublishReserved(
        MvvmResponse response,
        long fromRevision,
        int reservation,
        bool requireSnapshot = false)
    {
        lock (_gate)
        {
            if (_closed)
            {
                _reservedOutput = Math.Max(0, _reservedOutput - reservation);
                return;
            }

            if (response.Patches.Count > 0)
            {
                _output.Add(new MvvmTransportFrame(
                    MvvmTransportFrameKind.Patch,
                    response.RequestId,
                    fromRevision,
                    response.Revision,
                    response));
            }

            _output.Add(new MvvmTransportFrame(
                MvvmTransportFrameKind.Terminal,
                response.RequestId,
                response.Revision,
                response.Revision,
                response));
            _reservedOutput -= reservation;
            RememberTombstone(response.RequestId);
            _snapshotRequired |= requireSnapshot;
        }
    }

    private void RememberTombstone(MvvmRequestId requestId)
    {
        _tombstones.Enqueue(requestId);
        while (_tombstones.Count > _options.TombstoneCapacity)
        {
            _tombstones.Dequeue();
        }
    }

    private void ReleaseReservation(int reservation)
    {
        lock (_gate)
        {
            _reservedOutput -= reservation;
        }
    }

    private static int MaximumFrameCount(MvvmRequest request) =>
        request is MvvmMutationRequest ? 2 : 1;

    private void ObserveRejection(MvvmTransportRejection rejection)
    {
        switch (rejection)
        {
            case MvvmTransportRejection.AuthenticationFailed:
                Observe(MvvmTransportDiagnostic.AuthenticationFailed);
                break;
            case MvvmTransportRejection.OutputLimitExceeded:
                Observe(MvvmTransportDiagnostic.OutputLimited);
                break;
            case MvvmTransportRejection.SnapshotRequired:
                Observe(MvvmTransportDiagnostic.SnapshotRequired);
                break;
            case MvvmTransportRejection.CapabilityNotNegotiated:
                Observe(MvvmTransportDiagnostic.CapabilityNotNegotiated);
                break;
        }
    }

    private void Observe(MvvmTransportDiagnostic diagnostic)
    {
        try
        {
            _options.DiagnosticSink?.Invoke(diagnostic);
        }
        catch
        {
            // Diagnostics cannot change protocol admission or disclose request data.
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
