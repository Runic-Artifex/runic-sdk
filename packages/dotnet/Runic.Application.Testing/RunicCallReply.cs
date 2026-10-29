using System.Text.Json;

namespace Runic.Application.Testing;

/// <summary>The reply of a <c>set</c> or <c>command</c> request.</summary>
public sealed class RunicCallReply<TModel> where TModel : class
{
    internal RunicCallReply(string route, string json, RunicViewState<TModel>? state)
    {
        Route = route;
        Json = json;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Succeeded = root.GetProperty("ok").GetBoolean();
        State = state;
        if (root.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number) Revision = revision.GetInt64();
        Released = root.TryGetProperty("released", out var released) && released.ValueKind == JsonValueKind.True;
        Superseded = root.TryGetProperty("superseded", out var superseded) && superseded.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            ErrorKind = error.GetProperty("kind").GetString();
            ErrorMessage = error.GetProperty("message").GetString();
            if (error.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String) ErrorReason = reason.GetString();
            if (error.TryGetProperty("failure", out var failure)) Failure = failure.Clone();
        }
    }

    /// <summary>The route, request kind and member that were called.</summary>
    public string Route { get; }
    /// <summary>The reply as .NET serialized it.</summary>
    public string Json { get; }
    /// <summary>Whether the call succeeded.</summary>
    public bool Succeeded { get; }
    /// <summary>The revision of the first state of the route that includes the request's effects, when the request ran.</summary>
    public long? Revision { get; }
    /// <summary>Whether the request released its own route while it ran.</summary>
    public bool Released { get; }
    /// <summary>Whether a <c>set</c> was not applied because a later edit of the member was admitted before it.</summary>
    public bool Superseded { get; }
    /// <summary>
    /// The BridgeError kind of a failed call, such as <c>rejected</c> or <c>failed</c>, <c>released</c> for a
    /// released View or closed window, or <c>domain-failed</c> for a command's declared failure (<see cref="Failure"/>).
    /// Error kinds are an open set: a minor release may add kinds, so compare with the ones a test expects. A
    /// <c>rejected</c> reply's reason is <see cref="ErrorReason"/>.
    /// </summary>
    public string? ErrorKind { get; }
    /// <summary>
    /// The reason that refines <see cref="ErrorKind"/>, or null: <c>unavailable</c> (the command's <c>CanExecute</c>
    /// is false), <c>invalid-input</c> (the value or argument does not decode or is refused) or <c>unresolved-key</c>
    /// (a <see cref="Runic.Application.Views.RunicCommandKeyAttribute"/> command's key names no object) for
    /// <c>rejected</c>. Reasons are an open set: a minor release may add some.
    /// </summary>
    public string? ErrorReason { get; }
    /// <summary>The message of a failed call.</summary>
    public string? ErrorMessage { get; }
    /// <summary>The encoded declared failure of a <c>domain-failed</c> reply, such as <c>{"$case":"titleRequired"}</c>.</summary>
    public JsonElement? Failure { get; }
    /// <summary>The route's state after the call, read when the reply carries a revision.</summary>
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
    internal RunicFieldWriteReceipt(string route, string json, RunicViewState<TModel>? state)
    {
        Route = route;
        Json = json;
        State = state;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
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
    /// <summary>The route's state after the write, read when the reply carries a revision.</summary>
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

/// <summary>An operation started with a <c>start</c> request.</summary>
public sealed class RunicTestOperation
{
    private readonly InMemoryBridgeTransport _transport;
    private readonly string _route;

    internal RunicTestOperation(InMemoryBridgeTransport transport, string route, string contract, string member, string requestId, string admissionJson)
    {
        _transport = transport;
        _route = route;
        Contract = contract;
        Member = member;
        RequestId = requestId;
        AdmissionJson = admissionJson;
        using var document = JsonDocument.Parse(admissionJson);
        var root = document.RootElement;
        if (root.GetProperty("ok").GetBoolean())
        {
            Admission = RunicOperationWire.Admission(root.TryGetProperty("admission", out var admission) ? admission.GetString() : null);
            Reason = root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
        }
        else
        {
            var error = root.GetProperty("error");
            Admission = RunicOperationWire.Admission(error.GetProperty("kind").GetString());
            Reason = error.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString() : error.GetProperty("message").GetString();
        }
    }

    /// <summary>The operation contract: <c>{view}:{fingerprint}</c>.</summary>
    public string Contract { get; }
    /// <summary>The command member, such as <c>save</c>.</summary>
    public string Member { get; }
    /// <summary>The operation id the operation was started with.</summary>
    public string RequestId { get; }
    /// <summary>How the start request was answered.</summary>
    public RunicOperationAdmissionKind Admission { get; }
    /// <summary>Why the operation was not accepted.</summary>
    public string? Reason { get; }
    /// <summary>The admission reply as .NET serialized it.</summary>
    public string AdmissionJson { get; }

    private string Request(string kind, long? cursor = null) =>
        RequestAsync(kind, cursor).AsTask().GetAwaiter().GetResult();

    private ValueTask<string> RequestAsync(string kind, long? cursor = null, CancellationToken cancellationToken = default)
    {
        _transport.EnsureHello(InMemoryBridgeTransport.DefaultConnectionKey);
        return _transport.RequestAsync(RunicJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteString("route", _route);
            writer.WriteString("member", Member);
            writer.WriteString("operation", RequestId);
            writer.WriteString("contract", Contract);
            if (cursor is { } value) writer.WriteNumber("cursor", value);
            writer.WriteEndObject();
        }), cancellationToken: cancellationToken);
    }

    // The status of a status, watch or unwatch reply.
    private static RunicOperationStatus StatusOf(string reply)
    {
        using var document = JsonDocument.Parse(reply);
        var root = document.RootElement;
        return root.TryGetProperty("status", out var status)
            ? RunicOperationStatus.Parse(status.GetRawText())
            : throw new InvalidOperationException($"The operation request failed: {reply}");
    }

    /// <summary>Reads the current status with a <c>status</c> request.</summary>
    public RunicOperationStatus GetStatus() => StatusOf(Request("status"));

    /// <summary>Waits for the terminal status: <c>watch</c>, then the operation's pushes.</summary>
    public async Task<RunicOperationStatus> WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var next = _transport.NextMessage;
            var status = StatusOf(await RequestAsync("watch", cancellationToken: cancellationToken).ConfigureAwait(false));
            if (status.Kind is not RunicOperationStatusKind.Running) return status;
            await next.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Requests cancellation with a <c>cancel</c> request and returns how .NET answered.</summary>
    public RunicOperationCancelKind Cancel()
    {
        var reply = Request("cancel");
        using var document = JsonDocument.Parse(reply);
        return document.RootElement.TryGetProperty("cancellation", out var cancellation)
            ? RunicOperationWire.Cancel(cancellation.GetString())
            : throw new InvalidOperationException($"The cancel request failed: {reply}");
    }

    /// <summary>
    /// Reads the stream page that starts at <paramref name="cursor"/> with a <c>stream</c> request, as the generated
    /// client's <c>stream()</c> does.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cursor"/> is negative.</exception>
    public RunicOperationStreamPage ReadStream(long cursor = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        var reply = Request("stream", cursor);
        using var document = JsonDocument.Parse(reply);
        return document.RootElement.TryGetProperty("page", out var page)
            ? new(page.GetRawText(), cursor)
            : throw new InvalidOperationException($"The stream request failed: {reply}");
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
