using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Runic.Application.Views;

// Internal window/session plumbing for generated operation handles. Generated
// command routes call Accept; status, wait, and cancel are window routes so
// they remain available after a content presentation detaches.
//
// Protocol 4 (specs/application/README.md, "Operations"): a route's handler checks the member of status, watch,
// unwatch, cancel and stream and passes the request to Handle; start replies are written by EncodeStart. Watches push
// {"kind":"operation",…} through BridgeOperationWatches. The protocol-3 window routes (__runicOperation*) stay until
// the dispatcher's Legacy table is removed.
internal sealed class BridgeOperationRouter : IDisposable
{
    internal const string StatusRoute = "__runicOperationStatus";
    internal const string WaitRoute = "__runicOperationWait";
    internal const string CancelRoute = "__runicOperationCancel";
    internal const string StreamRoute = "__runicOperationStream";

    private readonly BridgeOperationRegistry _operations;
    private readonly BridgeOperationWatches _watches;
    private readonly IDisposable _connectionObserver;
    private readonly IDisposable[] _bindings;
    private bool _disposed;

    internal BridgeOperationRouter(
        IBridgeTransport transport,
        string ownerId,
        int maximumOperations = 64,
        int maximumRetainedTerminals = 32,
        int maximumRetainedExpiredIds = 128,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        bool? includeFailureDetail = null,
        CancellationToken ownerShutdown = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _operations = new BridgeOperationRegistry(
            ownerId, maximumOperations, maximumRetainedTerminals, maximumRetainedExpiredIds, ownerShutdown: ownerShutdown, logger: logger,
            timeProvider: timeProvider, includeFailureDetail: includeFailureDetail);
        _watches = new BridgeOperationWatches(transport, timeProvider ?? TimeProvider.System);
        _operations.StatusChanged = _watches.OnStatusChanged;
        // A connection's watches end when it closes; its operations do not, they belong to the window.
        _connectionObserver = BridgeDispatcher.For(transport).OnConnectionClosed(_watches.ReleaseConnection);
        var bindings = new List<IDisposable>();
        try
        {
            bindings.Add(BridgeDispatcher.For(transport).Legacy.Bind(StatusRoute, Status));
            bindings.Add(BridgeDispatcher.For(transport).Legacy.BindAsync(WaitRoute, WaitAsync));
            bindings.Add(BridgeDispatcher.For(transport).Legacy.Bind(CancelRoute, Cancel));
            bindings.Add(BridgeDispatcher.For(transport).Legacy.Bind(StreamRoute, Stream));
            _bindings = [.. bindings];
        }
        catch
        {
            foreach (var binding in bindings) binding.Dispose();
            _connectionObserver.Dispose();
            _watches.Dispose();
            _operations.Dispose();
            throw;
        }
    }

    // The registry's status of a protocol-4 operation for a member.
    internal BridgeOperationStatus Lookup(BridgeOperationIdentity identity, string member) =>
        _operations.Lookup(identity.RegistryKey, member);

    // The revision an accepted start replied with: a terminal status keeps it when the route cannot report a later one.
    internal void SetAdmissionRevision(BridgeOperationIdentity identity, long revision) =>
        _operations.SetAdmissionRevision(identity.RegistryKey, revision);

    // status, watch, unwatch, cancel and stream (protocol 4) of an operation member the route has; the route checked
    // the member. Returns the reply.
    internal ReadOnlyMemory<byte> Handle(BridgeRouteRequest request)
    {
        var root = request.Root;
        var member = request.Member!;
        var contract = root.GetProperty("contract").GetString()!;
        var operation = root.GetProperty("operation").GetString()!;
        BridgeOperationIdentity identity;
        try { identity = BridgeOperationIdentity.Create(contract, request.Route, operation); }
        catch (ArgumentException)
        {
            // An identity no operation can have, such as a contract longer than any route's: the window does not know it.
            return request.Kind switch
            {
                "cancel" => EncodeCancellation(BridgeOperationStatusKind.Unknown, requested: false),
                "stream" => EncodePage(BridgeOperationStatus.Unknown(operation), null, ReadCursor(root)),
                _ => EncodeStatusReply(BridgeOperationStatus.Unknown(operation)),
            };
        }
        var key = identity.RegistryKey;
        switch (request.Kind)
        {
            case "status":
                return EncodeStatusReply(_operations.Lookup(key, member));
            case "watch":
                var watched = _watches.Add(identity, member, request.Connection.Key, () => _operations.Lookup(key, member));
                return watched is null
                    ? BridgeMessages.Rejected("capacity",
                        $"The connection already watches {BridgeOperationWatches.MaximumWatchesPerConnection} operations.")
                    : EncodeStatusReply(watched);
            case "unwatch":
                _watches.Remove(identity, member, request.Connection.Key);
                return EncodeStatusReply(_operations.Lookup(key, member));
            case "cancel":
                var status = _operations.Lookup(key, member);
                return EncodeCancellation(status.Kind,
                    status.Kind is BridgeOperationStatusKind.Running && _operations.RequestCancellation(key, member));
            case "stream":
                var cursor = ReadCursor(root);
                var lookup = _operations.ReadStream(key, cursor, member);
                return EncodePage(lookup.Status, lookup.Stream, cursor);
            default:
                throw new ArgumentException($"'{request.Kind}' is not an operation request.", nameof(request));
        }
    }

    private static long ReadCursor(JsonElement root) =>
        BridgeRequestEnvelope.TryReadInteger(root.GetProperty("cursor"), out var cursor) ? cursor : 0;

    internal BridgeOperationAdmissionReply Accept(
        string contract,
        string requestId,
        Func<CancellationToken, Task> work)
    {
        var identity = BridgeOperationIdentity.Create(contract, requestId);
        var admission = _operations.Accept(identity.RegistryKey, work);
        return new(identity, admission.Kind, admission.Status, admission.Reason, admission.Terminal);
    }

    // The availability callback is intentionally supplied to the registry.
    // It is evaluated only for a newly admitted identity; a duplicate retry
    // returns the original operation before a newer CanExecute value (or key
    // resolution) can reject it.
    internal BridgeOperationAdmissionReply Accept(
        BridgeOperationRequest request,
        Func<bool> canStart,
        Func<CancellationToken, Task<BridgeOperationResult>> work) =>
        AcceptUnlessRefused(request, () => canStart() ? null : "unavailable", work);

    // refusal returns the rejection reason of a new request, such as "unavailable"
    // or "unresolved-key", or null when the operation can start.
    internal BridgeOperationAdmissionReply AcceptUnlessRefused(
        BridgeOperationRequest request,
        Func<string?> refusal,
        Func<CancellationToken, Task<BridgeOperationResult>> work,
        BridgeOperationRetirement? retirement = null,
        Func<long?>? terminalRevision = null)
    {
        var admission = _operations.AcceptUnlessRefused(request, refusal, work, retirement, terminalRevision);
        return new(request.Identity, admission.Kind, admission.Status, admission.Reason, admission.Terminal);
    }

    internal BridgeOperationAdmissionReply AcceptUnlessRefused(
        BridgeOperationRequest request,
        Func<string?> refusal,
        BridgeOperationStream stream,
        Func<BridgeOperationExecution, CancellationToken, Task<BridgeOperationResult>> work,
        BridgeOperationRetirement? retirement = null,
        Func<long?>? terminalRevision = null)
    {
        var admission = _operations.AcceptUnlessRefused(request, refusal, stream, work, retirement, terminalRevision);
        return new(request.Identity, admission.Kind, admission.Status, admission.Reason, admission.Terminal);
    }

    // `member` is optional only for the established public routes. Generated
    // clients always supply it, which prevents a recovery handle for one
    // command from observing a same-shaped result produced by another.
    internal BridgeOperationStatusReply Lookup(string contract, string requestId, string? member = null)
    {
        var identity = BridgeOperationIdentity.Create(contract, requestId);
        ValidateOptionalMember(member);
        return new(identity, _operations.Lookup(identity.RegistryKey, member));
    }

    internal BridgeOperationCancelReply RequestCancellation(string contract, string requestId, string? member = null)
    {
        var identity = BridgeOperationIdentity.Create(contract, requestId);
        ValidateOptionalMember(member);
        var status = _operations.Lookup(identity.RegistryKey, member);
        if (status.Kind is BridgeOperationStatusKind.Unknown or BridgeOperationStatusKind.Expired)
            return new(identity, status.Kind, false);
        if (status.Kind is not BridgeOperationStatusKind.Running)
            return new(identity, status.Kind, false);
        return new(identity, status.Kind, _operations.RequestCancellation(identity.RegistryKey, member));
    }

    internal async ValueTask<BridgeOperationStatusReply> WaitForTerminalAsync(
        string contract,
        string requestId,
        string? member = null,
        CancellationToken observerCancellation = default)
    {
        var identity = BridgeOperationIdentity.Create(contract, requestId);
        ValidateOptionalMember(member);
        var status = await _operations.WaitForTerminalAsync(identity.RegistryKey, member, observerCancellation).ConfigureAwait(false);
        return new(identity, status);
    }

    // Keep the original host-facing overload for callers that pass the
    // observer token positionally.
    internal ValueTask<BridgeOperationStatusReply> WaitForTerminalAsync(
        string contract, string requestId, CancellationToken observerCancellation) =>
        WaitForTerminalAsync(contract, requestId, member: null, observerCancellation: observerCancellation);

    internal BridgeOperationStreamReply ReadStream(string contract, string requestId, long cursor, string? member = null)
    {
        var identity = BridgeOperationIdentity.Create(contract, requestId);
        ValidateOptionalMember(member);
        var lookup = _operations.ReadStream(identity.RegistryKey, cursor, member);
        return new(identity, lookup.Status, lookup.Stream);
    }

    // The running operations that retiring owner's entry ends (RunicOperationRetirement).
    internal Task Retire(object owner) => _operations.Retire(owner);

    internal ValueTask<BridgeOperationCloseResult> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _operations.BeginCloseAsync(timeout, cancellationToken);

    internal BridgeOperationRegistry.BridgeAwaitedExecution? TryBeginAwaited(CancellationToken callerCancellation,
        out bool atCapacity) => _operations.TryBeginAwaited(callerCancellation, out atCapacity);

    private string Status(IBridgeArguments arguments) =>
        TryReadIdentity(arguments, out var routeIdentity)
            ? EncodeStatus(new BridgeOperationStatusReply(routeIdentity.Identity,
                _operations.Lookup(routeIdentity.Identity.RegistryKey, routeIdentity.Member)))
            : InvalidRequest();

    private async ValueTask<string> WaitAsync(IBridgeArguments arguments, CancellationToken observerCancellation)
    {
        if (!TryReadIdentity(arguments, out var routeIdentity)) return InvalidRequest();
        // Do not catch OperationCanceledException: callback cancellation is an
        // observer/transport event, not a command terminal result.
        var status = await _operations.WaitForTerminalAsync(routeIdentity.Identity.RegistryKey, routeIdentity.Member,
            observerCancellation).ConfigureAwait(false);
        return EncodeStatus(new BridgeOperationStatusReply(routeIdentity.Identity, status));
    }

    private string Cancel(IBridgeArguments arguments) =>
        TryReadIdentity(arguments, out var routeIdentity)
            ? EncodeCancel(RequestCancellation(routeIdentity.Identity.Contract, routeIdentity.Identity.RequestId, routeIdentity.Member))
            : InvalidRequest();

    private string Stream(IBridgeArguments arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments.GetString());
            var root = document.RootElement;
            var routeIdentity = ReadRouteIdentity(root);
            var reply = ReadStream(routeIdentity.Identity.Contract, routeIdentity.Identity.RequestId,
                root.GetProperty("cursor").GetInt64(), routeIdentity.Member);
            return EncodeStream(reply);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return InvalidRequest();
        }
    }

    private static bool TryReadIdentity(IBridgeArguments arguments, out BridgeOperationRouteIdentity routeIdentity)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments.GetString());
            var root = document.RootElement;
            routeIdentity = ReadRouteIdentity(root);
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            routeIdentity = default;
            return false;
        }
    }

    private static BridgeOperationRouteIdentity ReadRouteIdentity(JsonElement root)
    {
        var member = root.TryGetProperty("member", out var memberElement)
            ? memberElement.ValueKind is JsonValueKind.String
                ? memberElement.GetString()
                : throw new ArgumentException("The command member is invalid.", nameof(root))
            : null;
        ValidateOptionalMember(member);
        return new(
            BridgeOperationIdentity.Create(
                root.GetProperty("contract").GetString() ?? "",
                root.GetProperty("requestId").GetString() ?? ""),
            member);
    }

    private static void ValidateOptionalMember(string? member)
    {
        if (member is not null) _ = BridgeOperationRequest.ValidateMember(member);
    }

    internal static string EncodeAdmission(BridgeOperationAdmissionReply reply) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        WriteIdentity(writer, reply.Identity);
        writer.WriteString("kind", Wire(reply.Kind));
        writer.WriteString("status", Wire(reply.Status));
        if (reply.Reason is null) writer.WriteNull("reason");
        else writer.WriteString("reason", reply.Reason);
        writer.WritePropertyName("terminal");
        if (reply.Terminal is null) writer.WriteNullValue();
        else WriteStatusPayload(writer, reply.Identity, reply.Terminal);
        writer.WriteEndObject();
    });

    private static string EncodeStatus(BridgeOperationStatusReply reply) =>
        WriteJson(writer => WriteStatusPayload(writer, reply.Identity, reply.Status));

    private static string EncodeCancel(BridgeOperationCancelReply reply) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        WriteIdentity(writer, reply.Identity);
        writer.WriteString("kind", reply.Requested ? "cancellation-requested" : reply.Status switch
        {
            BridgeOperationStatusKind.Unknown => "unknown",
            BridgeOperationStatusKind.Expired => "expired",
            _ => "not-running",
        });
        writer.WriteEndObject();
    });

    private static string EncodeStream(BridgeOperationStreamReply reply) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        WriteIdentity(writer, reply.Identity);
        writer.WriteString("kind", Wire(reply.Status.Kind));
        if (reply.Stream is { } stream)
        {
            writer.WriteNumber("cursor", stream.NextCursor);
            writer.WriteBoolean("completed", stream.Completed);
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (var item in stream.Items)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", item.Sequence);
                writer.WritePropertyName("value");
                writer.WriteRawValue(item.EncodedJson, skipInputValidation: true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (stream.Failure is { } failure)
            {
                writer.WritePropertyName("delivery");
                writer.WriteStartObject();
                writer.WriteString("kind", Wire(failure.Kind));
                writer.WriteString("message", failure.Message);
                writer.WriteEndObject();
            }
        }
        writer.WriteEndObject();
    });

    private static void WriteStatusPayload(Utf8JsonWriter writer, BridgeOperationIdentity identity,
        BridgeOperationStatus status)
    {
        writer.WriteStartObject();
        WriteIdentity(writer, identity);
        writer.WriteString("kind", Wire(status.Kind));
        if (status.Kind is BridgeOperationStatusKind.Failed)
        {
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("kind", "failed");
            writer.WriteString("message", status.Failure ?? "The operation failed.");
            BridgeFailureDetails.Write(writer, status.FailureDetail);
            writer.WriteEndObject();
        }
        if (status.Kind is BridgeOperationStatusKind.DomainFailed)
        {
            // No detail (D-1): the declared failure is the contract. A failure
            // the retention budget dropped is reported as its delivery.
            if (status.DomainFailure is { } failure)
            {
                writer.WritePropertyName("failure");
                writer.WriteRawValue(failure, skipInputValidation: true);
            }
            if ((status.FailureDelivery ?? status.Result?.DeliveryFailure) is { } failureDelivery)
                WriteDelivery(writer, failureDelivery);
            if (status.Result?.Kind is BridgeOperationResultKind.Stream)
                writer.WriteBoolean("stream", true);
        }
        if (status.Kind is BridgeOperationStatusKind.Succeeded && status.Result is { } result)
        {
            if (result.Kind is BridgeOperationResultKind.Value && result.EncodedJson is { } encodedJson)
            {
                writer.WritePropertyName("result");
                writer.WriteRawValue(encodedJson, skipInputValidation: true);
            }
            if (result.DeliveryFailure is { } delivery) WriteDelivery(writer, delivery);
            if (result.Kind is BridgeOperationResultKind.Stream)
                writer.WriteBoolean("stream", true);
        }
        writer.WriteEndObject();
    }

    private static void WriteDelivery(Utf8JsonWriter writer, BridgeOperationDeliveryFailure delivery)
    {
        writer.WritePropertyName("delivery");
        writer.WriteStartObject();
        writer.WriteString("kind", Wire(delivery.Kind));
        writer.WriteString("message", delivery.Message);
        writer.WriteEndObject();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, BridgeOperationIdentity identity)
    {
        writer.WriteString("contract", identity.Contract);
        writer.WriteString("requestId", identity.RequestId);
    }

    private static string InvalidRequest() => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "invalid-request");
        writer.WriteEndObject();
    });

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }
    private static string Wire(BridgeOperationAdmissionKind kind) => kind switch
    {
        BridgeOperationAdmissionKind.Accepted => "accepted",
        BridgeOperationAdmissionKind.Duplicate => "duplicate",
        BridgeOperationAdmissionKind.Expired => "expired",
        BridgeOperationAdmissionKind.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    private static string Wire(BridgeOperationStatusKind kind) =>
        kind is BridgeOperationStatusKind.DomainFailed ? "domain-failed" : kind.ToString().ToLowerInvariant();
    private static string Wire(BridgeOperationDeliveryFailureKind kind) => kind switch
    {
        BridgeOperationDeliveryFailureKind.ResultTooLarge => "result-too-large",
        BridgeOperationDeliveryFailureKind.ResultEncodingFailed => "result-encoding-failed",
        BridgeOperationDeliveryFailureKind.StreamOverflow => "stream-overflow",
        BridgeOperationDeliveryFailureKind.StreamRetentionTooLarge => "stream-retention-too-large",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // {"ok":true, revision, admission, reason?, status?}: the reply of a protocol-4 start that the window answered.
    // status is the operation's current status for accepted and duplicate; reason names a rejection.
    // Without a revision, the start released its own route (released: true).
    internal static byte[] EncodeStart(BridgeOperationAdmissionReply admission, BridgeOperationStatus? status, long? revision) =>
        BridgeMessages.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            if (revision is { } published) writer.WriteNumber("revision", published);
            else writer.WriteBoolean("released", true);
            writer.WriteString("admission", Wire(admission.Kind));
            if (admission.Kind is BridgeOperationAdmissionKind.Rejected && admission.Reason is { } reason)
                writer.WriteString("reason", reason);
            if (admission.Kind is BridgeOperationAdmissionKind.Accepted or BridgeOperationAdmissionKind.Duplicate
                && (admission.Terminal ?? status) is { } current)
            {
                writer.WritePropertyName("status");
                WriteStatus(writer, current);
            }
            writer.WriteEndObject();
        });

    // A rejected start that the window answered before admission, such as an input that does not decode.
    internal static BridgeOperationAdmissionReply Rejection(BridgeOperationIdentity identity, string reason) =>
        new(identity, BridgeOperationAdmissionKind.Rejected, BridgeOperationStatusKind.Unknown, reason, null);

    // {"kind":"operation", route, member, operation, contract, status}: a watched operation's changed status.
    internal static byte[] EncodeOperationPush(BridgeOperationIdentity identity, string member, BridgeOperationStatus status) =>
        BridgeMessages.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "operation");
            writer.WriteString("route", identity.Route);
            writer.WriteString("member", member);
            writer.WriteString("operation", identity.RequestId);
            writer.WriteString("contract", identity.Contract);
            writer.WritePropertyName("status");
            WriteStatus(writer, status);
            writer.WriteEndObject();
        });

    private static byte[] EncodeStatusReply(BridgeOperationStatus status) => BridgeMessages.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        writer.WritePropertyName("status");
        WriteStatus(writer, status);
        writer.WriteEndObject();
    });

    private static byte[] EncodeCancellation(BridgeOperationStatusKind status, bool requested) => BridgeMessages.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        writer.WriteString("cancellation", requested ? "cancellation-requested" : status switch
        {
            BridgeOperationStatusKind.Unknown => "unknown",
            BridgeOperationStatusKind.Expired => "expired",
            _ => "not-running",
        });
        writer.WriteEndObject();
    });

    // {"ok":true, page:{kind, cursor, completed, items:[{sequence, value}], delivery?}}. An operation without a stream
    // has an empty, completed page at the requested cursor, unless it still runs.
    private static byte[] EncodePage(BridgeOperationStatus status, BridgeOperationStreamRead? stream, long cursor) =>
        BridgeMessages.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteStartObject("page");
            writer.WriteString("kind", Wire(status.Kind));
            writer.WriteNumber("cursor", stream?.NextCursor ?? cursor);
            writer.WriteBoolean("completed", stream?.Completed ?? status.Kind is not BridgeOperationStatusKind.Running);
            writer.WriteStartArray("items");
            if (stream is not null)
                foreach (var item in stream.Items)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("sequence", item.Sequence);
                    writer.WritePropertyName("value");
                    writer.WriteRawValue(item.EncodedJson, skipInputValidation: true);
                    writer.WriteEndObject();
                }
            writer.WriteEndArray();
            if (stream?.Failure is { } failure) WriteDelivery(writer, failure);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    // A protocol-4 status object (specs/application/README.md, "Status").
    internal static void WriteStatus(Utf8JsonWriter writer, BridgeOperationStatus status)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", Wire(status.Kind));
        switch (status.Kind)
        {
            case BridgeOperationStatusKind.Running:
                if (status.Progress is { } progress) writer.WriteNumber("progress", progress);
                break;
            case BridgeOperationStatusKind.Succeeded:
                WriteRevision(writer, status);
                if (status.Result is { Kind: BridgeOperationResultKind.Value, EncodedJson: { } result })
                {
                    writer.WritePropertyName("result");
                    writer.WriteRawValue(result, skipInputValidation: true);
                }
                if (status.Result?.DeliveryFailure is { } delivery) WriteDelivery(writer, delivery);
                WriteStream(writer, status);
                break;
            case BridgeOperationStatusKind.Failed:
                WriteRevision(writer, status);
                writer.WriteStartObject("error");
                writer.WriteString("kind", "failed");
                writer.WriteString("message", status.Failure ?? "The operation failed.");
                BridgeFailureDetails.Write(writer, status.FailureDetail);
                writer.WriteEndObject();
                if (status.Result?.DeliveryFailure is { } failedDelivery) WriteDelivery(writer, failedDelivery);
                WriteStream(writer, status);
                break;
            case BridgeOperationStatusKind.DomainFailed:
                // No detail (D-1): the declared failure is the contract. A failure the retention budget dropped is
                // reported as its delivery.
                WriteRevision(writer, status);
                if (status.DomainFailure is { } failure)
                {
                    writer.WritePropertyName("failure");
                    writer.WriteRawValue(failure, skipInputValidation: true);
                }
                if ((status.FailureDelivery ?? status.Result?.DeliveryFailure) is { } failureDelivery)
                    WriteDelivery(writer, failureDelivery);
                WriteStream(writer, status);
                break;
            case BridgeOperationStatusKind.Cancelled:
                WriteRevision(writer, status);
                WriteStream(writer, status);
                break;
        }
        writer.WriteEndObject();
    }

    private static void WriteRevision(Utf8JsonWriter writer, BridgeOperationStatus status)
    {
        if (status.Revision is { } revision) writer.WriteNumber("revision", revision);
    }

    private static void WriteStream(Utf8JsonWriter writer, BridgeOperationStatus status)
    {
        if (status.Result?.Kind is BridgeOperationResultKind.Stream) writer.WriteBoolean("stream", true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var binding in _bindings) binding.Dispose();
        _connectionObserver.Dispose();
        _watches.Dispose();
        _operations.Dispose();
    }
}

internal readonly record struct BridgeOperationRouteIdentity(BridgeOperationIdentity Identity, string? Member);

// The registry accepts a string internally. Length prefixes make the composed
// key unambiguous without exposing an operation registry key to generated TS.
//
// Protocol 4 keys an operation by (contract, route, operation) (specs/application/README.md, "Identity"): Route is the
// route the request named, and RequestId the operation id. A protocol-3 identity has no route; its contract ends with
// the route instead, so the two never share a key.
internal readonly record struct BridgeOperationIdentity(string Contract, string RequestId, string? Route = null)
{
    internal const int MaximumContractLength = 256;
    internal const int MaximumRequestIdLength = 128;

    internal string RegistryKey => Route is null
        ? $"{Contract.Length}:{Contract}{RequestId.Length}:{RequestId}"
        : $"{Contract.Length}:{Contract}{RequestId.Length}:{RequestId}{Route.Length}:{Route}";

    // A protocol-4 identity, from a request whose envelope was checked.
    internal static BridgeOperationIdentity Create(string contract, string route, string operation) =>
        Create(contract, operation) with { Route = route };

    internal static BridgeOperationIdentity Create(string contract, string requestId)
    {
        if (string.IsNullOrWhiteSpace(contract)) throw new ArgumentException("A contract identity is required.", nameof(contract));
        if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("A request identity is required.", nameof(requestId));
        if (contract.Length > MaximumContractLength || contract.Any(char.IsControl))
            throw new ArgumentException("The contract identity is invalid.", nameof(contract));
        if (requestId.Length > MaximumRequestIdLength || requestId.Any(char.IsControl))
            throw new ArgumentException("The request identity is invalid.", nameof(requestId));
        return new(contract, requestId);
    }
}

// The request keeps the public recovery identity (contract + request ID)
// small, while binding that identity to the command member and canonical input
// at admission. Reusing a request ID for different work is a protocol error,
// not a second idempotency namespace.
internal readonly record struct BridgeOperationRequest(
    BridgeOperationIdentity Identity,
    string Member,
    string InputDigest)
{
    internal const int MaximumMemberLength = 256;
    internal const int MaximumDigestLength = 128;

    internal BridgeOperationRequest(string contract, string member, string requestId, string inputDigest)
        : this(BridgeOperationIdentity.Create(contract, requestId), ValidateMember(member), ValidateInputDigest(inputDigest))
    {
    }

    internal static BridgeOperationRequest Create(string contract, string member, string requestId, string inputDigest) =>
        new(contract, member, requestId, inputDigest);

    internal static BridgeOperationRequest Create(BridgeOperationIdentity identity, string member, string inputDigest)
    {
        return new(identity, ValidateMember(member), ValidateInputDigest(inputDigest));
    }

    internal static string ValidateMember(string member)
    {
        if (string.IsNullOrWhiteSpace(member) || member.Length > MaximumMemberLength || member.Any(char.IsControl))
            throw new ArgumentException("The command member is invalid.", nameof(member));
        return member;
    }

    private static string ValidateInputDigest(string inputDigest)
    {
        if (string.IsNullOrWhiteSpace(inputDigest) || inputDigest.Length > MaximumDigestLength || inputDigest.Any(char.IsControl))
            throw new ArgumentException("The command input digest is invalid.", nameof(inputDigest));
        return inputDigest;
    }

    internal static string CanonicalDigest(string encodedJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedJson);
        return CanonicalDigest(Encoding.UTF8.GetBytes(encodedJson));
    }

    internal static string CanonicalDigest(JsonElement element) => CanonicalDigest(JsonMarshal.GetRawUtf8Value(element));

    // BridgeCanonicalJson is the single canonical form; see its comment.
    private static string CanonicalDigest(ReadOnlySpan<byte> json)
    {
        using var canonical = BridgeCanonicalJson.Canonical(json);
        return Convert.ToHexStringLower(SHA256.HashData(canonical.Written));
    }
}

internal sealed record BridgeOperationAdmissionReply(
    BridgeOperationIdentity Identity,
    BridgeOperationAdmissionKind Kind,
    BridgeOperationStatusKind Status,
    string? Reason,
    BridgeOperationStatus? Terminal)
{
    // The command member of a protocol-4 start.
    public string? Member { get; init; }
}

internal sealed record BridgeOperationStatusReply(BridgeOperationIdentity Identity, BridgeOperationStatus Status);
internal sealed record BridgeOperationCancelReply(BridgeOperationIdentity Identity, BridgeOperationStatusKind Status, bool Requested);
internal sealed record BridgeOperationStreamReply(
    BridgeOperationIdentity Identity,
    BridgeOperationStatus Status,
    BridgeOperationStreamRead? Stream);
