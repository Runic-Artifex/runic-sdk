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
        Succeeded = root.GetProperty("ok").GetBoolean();
        if (root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object) State = new(state);
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            ErrorKind = error.GetProperty("kind").GetString();
            ErrorMessage = error.GetProperty("message").GetString();
            if (error.TryGetProperty("failure", out var failure)) Failure = failure.Clone();
        }
    }

    /// <summary>The route that was called.</summary>
    public string Route { get; }
    /// <summary>The reply as .NET serialized it.</summary>
    public string Json { get; }
    /// <summary>Whether the call succeeded.</summary>
    public bool Succeeded { get; }
    /// <summary>
    /// The BridgeError kind of a failed call, such as <c>rejected</c> or <c>failed</c>, <c>released</c> for a
    /// released View or closed window, or <c>domain-failed</c> for a command's declared failure (<see cref="Failure"/>).
    /// Error kinds are an open set: a minor release may add kinds, so compare with the ones a test expects. A
    /// <c>rejected</c> reply's <c>reason</c> (<c>unavailable</c>, <c>invalid-input</c>) is in <see cref="Json"/>.
    /// </summary>
    public string? ErrorKind { get; }
    /// <summary>The message of a failed call.</summary>
    public string? ErrorMessage { get; }
    /// <summary>The encoded declared failure of a <c>domain-failed</c> reply, such as <c>{"$case":"titleRequired"}</c>.</summary>
    public JsonElement? Failure { get; }
    /// <summary>The state after the call, when the reply carries one.</summary>
    public RunicViewState<TModel>? State { get; }

    /// <summary>Returns this reply, or throws when the call failed.</summary>
    /// <exception cref="InvalidOperationException">The call failed; the message names the route, error kind and message.</exception>
    public RunicCallReply<TModel> ThrowIfFailed() => Succeeded ? this
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
            Kind = RunicFieldWriteReceiptKind.Failed;
            ErrorMessage = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                ? error.GetProperty("message").GetString() : null;
            return;
        }
        Kind = RunicOperationWire.Receipt(receipt.GetProperty("kind").GetString());
        ErrorMessage = receipt.TryGetProperty("message", out var message) ? message.GetString() : null;
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
    /// <summary>How the write ended.</summary>
    public RunicFieldWriteReceiptKind Kind { get; }
    /// <summary>
    /// Why the write was rejected, conflicted or failed, or why its validation did not complete; null for an
    /// applied write.
    /// </summary>
    public string? ErrorMessage { get; }
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
/// <param name="Kind">The status.</param>
/// <param name="Result">The encoded result of a succeeded operation with a result.</param>
/// <param name="ErrorMessage">Why a failed operation failed.</param>
/// <param name="Json">The status as .NET serialized it.</param>
public sealed record RunicOperationStatus(RunicOperationStatusKind Kind, JsonElement? Result, string? ErrorMessage, string Json)
{
    /// <summary>The encoded declared failure of a <c>domain-failed</c> operation, when it was retained.</summary>
    public JsonElement? Failure { get; init; }

    internal static RunicOperationStatus Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new(root.TryGetProperty("kind", out var kind) ? RunicOperationWire.Status(kind.GetString()) : RunicOperationStatusKind.Unknown,
            root.TryGetProperty("result", out var result) ? result.Clone() : null,
            root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? error.GetProperty("message").GetString() : null,
            json)
        {
            Failure = root.TryGetProperty("failure", out var failure) ? failure.Clone() : null,
        };
    }
}

/// <summary>An operation started through a generated <c>Start{Command}</c> route.</summary>
public sealed class RunicTestOperation
{
    private readonly InMemoryBridgeTransport _transport;
    private readonly string _identity;

    internal RunicTestOperation(InMemoryBridgeTransport transport, string contract, string member, string requestId, string admissionJson)
    {
        _transport = transport;
        Contract = contract;
        Member = member;
        RequestId = requestId;
        AdmissionJson = admissionJson;
        using (var document = JsonDocument.Parse(admissionJson))
        {
            var root = document.RootElement;
            Admission = RunicOperationWire.Admission(root.TryGetProperty("kind", out var kind) ? kind.GetString() : null);
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
    /// <summary>How the start route answered.</summary>
    public RunicOperationAdmissionKind Admission { get; }
    /// <summary>Why the operation was not accepted.</summary>
    public string? Reason { get; }
    /// <summary>The admission reply as .NET serialized it.</summary>
    public string AdmissionJson { get; }

    /// <summary>Reads the current status through the window's status route.</summary>
    public RunicOperationStatus GetStatus() => RunicOperationStatus.Parse(_transport.Call("__runicOperationStatus", new(StringValue: _identity)));

    /// <summary>Waits for the terminal status.</summary>
    public async Task<RunicOperationStatus> WaitAsync(CancellationToken cancellationToken = default) =>
        RunicOperationStatus.Parse(await _transport.CallAsync("__runicOperationWait", new(StringValue: _identity), cancellationToken).ConfigureAwait(false));

    /// <summary>Requests cancellation and returns how .NET answered.</summary>
    public RunicOperationCancelKind Cancel()
    {
        using var document = JsonDocument.Parse(_transport.Call("__runicOperationCancel", new(StringValue: _identity)));
        return RunicOperationWire.Cancel(document.RootElement.GetProperty("kind").GetString());
    }

    /// <summary>
    /// Reads the retained items of a streamed operation after <paramref name="cursor"/> through the window's stream
    /// route, as the generated client's <c>stream(cursor)</c> does.
    /// </summary>
    /// <param name="cursor">The <see cref="RunicOperationStreamPage.Cursor"/> of the previous page, or 0 for the first.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cursor"/> is negative.</exception>
    public RunicOperationStreamPage ReadStream(long cursor = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        var request = RunicJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("contract", Contract);
            writer.WriteString("member", Member);
            writer.WriteString("requestId", RequestId);
            writer.WriteNumber("cursor", cursor);
            writer.WriteEndObject();
        });
        return new(_transport.Call("__runicOperationStream", new(StringValue: request)), cursor);
    }
}

/// <summary>A page of a streamed operation's items, read with <see cref="RunicTestOperation.ReadStream"/>.</summary>
public sealed class RunicOperationStreamPage
{
    internal RunicOperationStreamPage(string json, long cursor)
    {
        Json = json;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Kind = root.TryGetProperty("kind", out var kind) ? RunicOperationWire.Status(kind.GetString()) : RunicOperationStatusKind.Unknown;
        Cursor = root.TryGetProperty("cursor", out var next) ? next.GetInt64() : cursor;
        Completed = root.TryGetProperty("completed", out var completed) && completed.GetBoolean();
        Items = root.TryGetProperty("items", out var items)
            ? [.. items.EnumerateArray().Select(item => item.GetProperty("value").Clone())]
            : [];
        if (root.TryGetProperty("delivery", out var delivery) && delivery.ValueKind == JsonValueKind.Object)
        {
            DeliveryKind = delivery.GetProperty("kind").GetString();
            DeliveryMessage = delivery.GetProperty("message").GetString();
        }
    }

    /// <summary>The operation's status: <see cref="RunicOperationStatusKind.Unknown"/> or <see cref="RunicOperationStatusKind.Expired"/> when the window no longer has it.</summary>
    public RunicOperationStatusKind Kind { get; }
    /// <summary>The encoded items after the requested cursor, in the order the command produced them.</summary>
    public IReadOnlyList<JsonElement> Items { get; }
    /// <summary>The cursor to read the next page with; the requested cursor when the page has no stream.</summary>
    public long Cursor { get; }
    /// <summary>Whether the stream ended, so later pages add no items.</summary>
    public bool Completed { get; }
    /// <summary>
    /// Why items were dropped, such as <c>stream-overflow</c> or <c>stream-retention-too-large</c>; null when none
    /// were. The kinds are an open set.
    /// </summary>
    public string? DeliveryKind { get; }
    /// <summary>The message that goes with <see cref="DeliveryKind"/>.</summary>
    public string? DeliveryMessage { get; }
    /// <summary>The page as .NET serialized it.</summary>
    public string Json { get; }

    /// <inheritdoc />
    public override string ToString() => Json;
}
