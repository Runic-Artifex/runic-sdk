using System.Text.Json;

namespace Runic.Application.Testing;

/// <summary>The reply of a generated setter or command route.</summary>
public sealed class RunicCallReply<TModel> where TModel : class
{
    internal RunicCallReply(string route, string json)
    {
        Route = route;
        Json = json;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Ok = root.GetProperty("ok").GetBoolean();
        if (root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object) State = new(state);
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            ErrorKind = error.GetProperty("kind").GetString();
            ErrorMessage = error.GetProperty("message").GetString();
        }
    }

    /// <summary>The route that was called.</summary>
    public string Route { get; }
    /// <summary>The reply as .NET serialized it.</summary>
    public string Json { get; }
    /// <summary>Whether the call succeeded.</summary>
    public bool Ok { get; }
    /// <summary>The BridgeError kind of a failed call, such as <c>rejected</c> or <c>failed</c>.</summary>
    public string? ErrorKind { get; }
    /// <summary>The message of a failed call.</summary>
    public string? ErrorMessage { get; }
    /// <summary>The state after the call, when the reply carries one.</summary>
    public RunicViewState<TModel>? State { get; }

    /// <summary>Returns this reply, or throws when the call failed.</summary>
    public RunicCallReply<TModel> EnsureOk() => Ok ? this
        : throw new InvalidOperationException($"{Route} failed ({ErrorKind}): {ErrorMessage}");

    /// <inheritdoc />
    public override string ToString() => Json;
}

/// <summary>The receipt of a checked field write.</summary>
public sealed class RunicFieldWriteReceipt<TModel> where TModel : class
{
    internal RunicFieldWriteReceipt(string route, string json)
    {
        Route = route;
        Json = json;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object) State = new(state);
        if (!root.TryGetProperty("receipt", out var receipt) || receipt.ValueKind != JsonValueKind.Object)
        {
            Kind = "failed";
            Message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                ? error.GetProperty("message").GetString() : null;
            return;
        }
        Kind = receipt.GetProperty("kind").GetString()!;
        Message = receipt.TryGetProperty("message", out var message) ? message.GetString() : null;
        if (receipt.TryGetProperty("snapshot", out var snapshot) || receipt.TryGetProperty("incoming", out snapshot))
        {
            Value = snapshot.GetProperty("value").Clone();
            Version = snapshot.GetProperty("version").GetInt64();
        }
    }

    /// <summary>The write route that was called.</summary>
    public string Route { get; }
    /// <summary>The reply as .NET serialized it.</summary>
    public string Json { get; }
    /// <summary>
    /// <c>applied</c>, <c>committed-with-error</c>, <c>conflict</c> or <c>rejected</c>;
    /// <c>failed</c> when the route replied with an error instead of a receipt.
    /// </summary>
    public string Kind { get; }
    /// <summary>Why the write was rejected or conflicted.</summary>
    public string? Message { get; }
    /// <summary>The field value after the write, or the current value of a conflict.</summary>
    public JsonElement? Value { get; }
    /// <summary>The field version that goes with <see cref="Value"/>.</summary>
    public long? Version { get; }
    /// <summary>The state after the write, when the reply carries one.</summary>
    public RunicViewState<TModel>? State { get; }

    /// <inheritdoc />
    public override string ToString() => Json;
}

/// <summary>The status of an operation, as the generated client reads it.</summary>
/// <param name="Kind"><c>running</c>, <c>succeeded</c>, <c>failed</c>, <c>cancelled</c>, <c>expired</c> or <c>unknown</c>.</param>
/// <param name="Result">The encoded result of a succeeded operation with a result.</param>
/// <param name="ErrorMessage">Why a failed operation failed.</param>
/// <param name="Json">The status as .NET serialized it.</param>
public sealed record RunicOperationStatus(string Kind, JsonElement? Result, string? ErrorMessage, string Json)
{
    internal static RunicOperationStatus Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new(root.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "unknown" : "unknown",
            root.TryGetProperty("result", out var result) ? result.Clone() : null,
            root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error.GetProperty("message").GetString() : null,
            json);
    }
}

/// <summary>An operation started through a generated <c>Start{Command}</c> route.</summary>
public sealed class RunicTestOperation
{
    private readonly InMemoryViewTransport _transport;
    private readonly string _identity;

    internal RunicTestOperation(InMemoryViewTransport transport, string contract, string member, string requestId, string admissionJson)
    {
        _transport = transport;
        Contract = contract;
        Member = member;
        RequestId = requestId;
        AdmissionJson = admissionJson;
        using (var document = JsonDocument.Parse(admissionJson))
        {
            var root = document.RootElement;
            Admission = root.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "" : "";
            Reason = root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
        }
        _identity = RunicJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("contract", contract);
            writer.WriteString("member", member);
            writer.WriteString("requestId", requestId);
            writer.WriteEndObject();
        });
    }

    /// <summary>The operation contract: ViewModel type, contract fingerprint and route.</summary>
    public string Contract { get; }
    /// <summary>The command member, such as <c>Save</c>.</summary>
    public string Member { get; }
    /// <summary>The request id the operation was started with.</summary>
    public string RequestId { get; }
    /// <summary><c>accepted</c>, <c>duplicate</c>, <c>expired</c>, <c>rejected</c> or <c>failed</c>.</summary>
    public string Admission { get; }
    /// <summary>Why the operation was not accepted.</summary>
    public string? Reason { get; }
    /// <summary>The admission reply as .NET serialized it.</summary>
    public string AdmissionJson { get; }

    /// <summary>Reads the current status.</summary>
    public RunicOperationStatus Status() => RunicOperationStatus.Parse(_transport.Call("__runicOperationStatus", new(StringValue: _identity)));

    /// <summary>Waits for the terminal status.</summary>
    public async ValueTask<RunicOperationStatus> WaitAsync(CancellationToken cancellationToken = default) =>
        RunicOperationStatus.Parse(await _transport.CallAsync("__runicOperationWait", new(StringValue: _identity), cancellationToken).ConfigureAwait(false));

    /// <summary>Requests cancellation and returns how .NET answered, such as <c>cancellation-requested</c>.</summary>
    public string Cancel()
    {
        using var document = JsonDocument.Parse(_transport.Call("__runicOperationCancel", new(StringValue: _identity)));
        return document.RootElement.GetProperty("kind").GetString()!;
    }
}
