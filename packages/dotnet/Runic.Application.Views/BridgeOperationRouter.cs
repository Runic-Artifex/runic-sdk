using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("BridgeOperationRouterProbe")]
[assembly: InternalsVisibleTo("WindowOperationRouterProbe")]
[assembly: InternalsVisibleTo("CsWebUiGracefulCloseProbe")]

namespace Runic.Application.Views;

// Internal window/session plumbing for generated operation handles. Generated
// command routes call Accept; status, wait, and cancel are window routes so
// they remain available after a content presentation detaches.
internal sealed class BridgeOperationRouter : IDisposable
{
    internal const string StatusRoute = "__runicOperationStatus";
    internal const string WaitRoute = "__runicOperationWait";
    internal const string CancelRoute = "__runicOperationCancel";

    private readonly BridgeOperationRegistry _operations;
    private readonly IDisposable[] _bindings;
    private bool _disposed;

    internal BridgeOperationRouter(
        IBridgeTransport transport,
        string ownerId,
        CancellationToken ownerShutdown = default,
        int maximumOperations = 64,
        int maximumRetainedTerminals = 32,
        int maximumRetainedExpiredIds = 128)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _operations = new BridgeOperationRegistry(
            ownerId, maximumOperations, maximumRetainedTerminals, maximumRetainedExpiredIds, ownerShutdown);
        var bindings = new List<IDisposable>();
        try
        {
            bindings.Add(transport.Bind(StatusRoute, Status));
            bindings.Add(transport.BindAsync(WaitRoute, WaitAsync));
            bindings.Add(transport.Bind(CancelRoute, Cancel));
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

    internal BridgeOperationStatusReply Lookup(string contract, string requestId)
    {
        var identity = BridgeOperationIdentity.Create(contract, requestId);
        return new(identity, _operations.Lookup(identity.RegistryKey));
    }

    internal BridgeOperationCancelReply RequestCancellation(string contract, string requestId)
    {
        var identity = BridgeOperationIdentity.Create(contract, requestId);
        var status = _operations.Lookup(identity.RegistryKey);
        if (status.Kind is BridgeOperationStatusKind.Unknown or BridgeOperationStatusKind.Expired)
            return new(identity, status.Kind, false);
        if (status.Kind is not BridgeOperationStatusKind.Running)
            return new(identity, status.Kind, false);
        return new(identity, status.Kind, _operations.RequestCancellation(identity.RegistryKey));
    }

    internal async ValueTask<BridgeOperationStatusReply> WaitForTerminalAsync(
        string contract,
        string requestId,
        CancellationToken observerCancellation = default)
    {
        var identity = BridgeOperationIdentity.Create(contract, requestId);
        var status = await _operations.WaitForTerminalAsync(identity.RegistryKey, observerCancellation).ConfigureAwait(false);
        return new(identity, status);
    }

    internal ValueTask<BridgeOperationCloseResult> BeginCloseAsync(TimeSpan timeout) =>
        _operations.BeginCloseAsync(timeout);

    private string Status(IBridgeArguments arguments) =>
        TryReadIdentity(arguments, out var identity)
            ? EncodeStatus(new BridgeOperationStatusReply(identity, _operations.Lookup(identity.RegistryKey)))
            : InvalidRequest();

    private async ValueTask<string> WaitAsync(IBridgeArguments arguments, CancellationToken observerCancellation)
    {
        if (!TryReadIdentity(arguments, out var identity)) return InvalidRequest();
        // Do not catch OperationCanceledException: callback cancellation is an
        // observer/transport event, not a command terminal result.
        var status = await _operations.WaitForTerminalAsync(identity.RegistryKey, observerCancellation).ConfigureAwait(false);
        return EncodeStatus(new BridgeOperationStatusReply(identity, status));
    }

    private string Cancel(IBridgeArguments arguments) =>
        TryReadIdentity(arguments, out var identity)
            ? EncodeCancel(RequestCancellation(identity.Contract, identity.RequestId))
            : InvalidRequest();

    private static bool TryReadIdentity(IBridgeArguments arguments, out BridgeOperationIdentity identity)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments.GetString());
            var root = document.RootElement;
            identity = BridgeOperationIdentity.Create(
                root.GetProperty("contract").GetString() ?? "",
                root.GetProperty("requestId").GetString() ?? "");
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            identity = default;
            return false;
        }
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
            writer.WriteEndObject();
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var binding in _bindings) binding.Dispose();
        _operations.Dispose();
    }
}

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

internal sealed record BridgeOperationAdmissionReply(
    BridgeOperationIdentity Identity,
    BridgeOperationAdmissionKind Kind,
    BridgeOperationStatusKind Status,
    string? Reason,
    BridgeOperationStatus? Terminal);

internal sealed record BridgeOperationStatusReply(BridgeOperationIdentity Identity, BridgeOperationStatus Status);
internal sealed record BridgeOperationCancelReply(BridgeOperationIdentity Identity, BridgeOperationStatusKind Status, bool Requested);
