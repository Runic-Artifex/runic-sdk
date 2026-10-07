using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Runic.Application.Views;

// Internal window/session plumbing for generated operation handles. Generated
// command routes call Accept; status, wait, and cancel are window routes so
// they remain available after a content presentation detaches.
internal sealed class BridgeOperationRouter : IDisposable
{
    internal const string StatusRoute = "__runicOperationStatus";
    internal const string WaitRoute = "__runicOperationWait";
    internal const string CancelRoute = "__runicOperationCancel";
    internal const string StreamRoute = "__runicOperationStream";

    private readonly BridgeOperationRegistry _operations;
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
        CancellationToken ownerShutdown = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _operations = new BridgeOperationRegistry(
            ownerId, maximumOperations, maximumRetainedTerminals, maximumRetainedExpiredIds, ownerShutdown: ownerShutdown, logger: logger,
            timeProvider: timeProvider);
        var bindings = new List<IDisposable>();
        try
        {
            bindings.Add(transport.Bind(StatusRoute, Status));
            bindings.Add(transport.BindAsync(WaitRoute, WaitAsync));
            bindings.Add(transport.Bind(CancelRoute, Cancel));
            bindings.Add(transport.Bind(StreamRoute, Stream));
            _bindings = [.. bindings];
        }
        catch
        {
            foreach (var binding in bindings) binding.Dispose();
            _operations.Dispose();
            throw;
        }
    }

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
    // returns the original operation before a newer CanExecute value can
    // reject it.
    internal BridgeOperationAdmissionReply Accept(
        BridgeOperationRequest request,
        Func<bool> canStart,
        Func<CancellationToken, Task<BridgeOperationResult>> work)
    {
        var admission = _operations.Accept(request, canStart, work);
        return new(request.Identity, admission.Kind, admission.Status, admission.Reason, admission.Terminal);
    }

    internal BridgeOperationAdmissionReply Accept(
        BridgeOperationRequest request,
        Func<bool> canStart,
        BridgeOperationStream stream,
        Func<BridgeOperationExecution, CancellationToken, Task<BridgeOperationResult>> work)
    {
        var admission = _operations.Accept(request, canStart, stream, work);
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

    internal ValueTask<BridgeOperationCloseResult> BeginCloseAsync(TimeSpan timeout) =>
        _operations.BeginCloseAsync(timeout);

    internal BridgeOperationRegistry.BridgeAwaitedExecution? TryBeginAwaited(CancellationToken callerCancellation) =>
        _operations.TryBeginAwaited(callerCancellation);

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
            BridgeDiagnostics.Write(writer, status.FailureDetail);
            writer.WriteEndObject();
        }
        if (status.Kind is BridgeOperationStatusKind.Succeeded && status.Result is { } result)
        {
            if (result.Kind is BridgeOperationResultKind.Value && result.EncodedJson is { } encodedJson)
            {
                writer.WritePropertyName("result");
                writer.WriteRawValue(encodedJson, skipInputValidation: true);
            }
            if (result.DeliveryFailure is { } delivery)
            {
                writer.WritePropertyName("delivery");
                writer.WriteStartObject();
                writer.WriteString("kind", Wire(delivery.Kind));
                writer.WriteString("message", delivery.Message);
                writer.WriteEndObject();
            }
            if (result.Kind is BridgeOperationResultKind.Stream)
                writer.WriteBoolean("stream", true);
        }
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
    private static string Wire(BridgeOperationAdmissionKind kind) => kind.ToString().ToLowerInvariant();
    private static string Wire(BridgeOperationStatusKind kind) => kind.ToString().ToLowerInvariant();
    private static string Wire(BridgeOperationDeliveryFailureKind kind) => kind switch
    {
        BridgeOperationDeliveryFailureKind.ResultTooLarge => "result-too-large",
        BridgeOperationDeliveryFailureKind.ResultEncodingFailed => "result-encoding-failed",
        BridgeOperationDeliveryFailureKind.StreamOverflow => "stream-overflow",
        BridgeOperationDeliveryFailureKind.StreamRetentionTooLarge => "stream-retention-too-large",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var binding in _bindings) binding.Dispose();
        _operations.Dispose();
    }
}

internal readonly record struct BridgeOperationRouteIdentity(BridgeOperationIdentity Identity, string? Member);

// The registry accepts a string internally. Length prefixes make the composed
// key unambiguous without exposing an operation registry key to generated TS.
internal readonly record struct BridgeOperationIdentity(string Contract, string RequestId)
{
    internal const int MaximumContractLength = 256;
    internal const int MaximumRequestIdLength = 128;

    internal string RegistryKey => $"{Contract.Length}:{Contract}{RequestId.Length}:{RequestId}";

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
    BridgeOperationStatus? Terminal);

internal sealed record BridgeOperationStatusReply(BridgeOperationIdentity Identity, BridgeOperationStatus Status);
internal sealed record BridgeOperationCancelReply(BridgeOperationIdentity Identity, BridgeOperationStatusKind Status, bool Requested);
internal sealed record BridgeOperationStreamReply(
    BridgeOperationIdentity Identity,
    BridgeOperationStatus Status,
    BridgeOperationStreamRead? Stream);
