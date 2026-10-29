using System.Text.Json;
using System.Windows.Input;
namespace Runic.Application.Views.Generated;

// The requests of a Bridge's route (specs/application/README.md, "Requests and replies"): open, close and snapshot,
// set, write, command and can. The operation and interaction kinds go to HandleOperationAsync
// (BridgeRuntime.Operations.cs).
//
// A request that changes state runs in one model turn: its effects, then the publication of everything pending on the
// route, whose revision the reply carries (PublishForReply). The reply then waits, at most 5 seconds, until the pushes
// the window requested before it was ready were handed to the transport.
public partial class ViewModelBridge<T>
{
    private sealed class RouteHandler(ViewModelBridge<T> owner) : IBridgeRouteHandler
    {
        public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken) =>
            request.Kind switch
            {
                "open" => new(owner.Open(request)),
                "close" => new(owner.Close(request)),
                "snapshot" => new(owner.Snapshot()),
                "set" => owner.SetAsync(request, cancellationToken),
                "write" => owner.WriteAsync(request, cancellationToken),
                "command" => owner.CommandAsync(request, cancellationToken),
                "can" => owner.CanAsync(request, cancellationToken),
                _ => owner.HandleOperationAsync(request, cancellationToken),
            };
    }

    private ReadOnlyMemory<byte> Open(BridgeRouteRequest request)
    {
        var presentation = request.Root.GetProperty("presentation").GetString()!;
        BridgePresentationOutcome outcome;
        try { outcome = OpenPresentation(presentation, request.Connection); }
        catch (Exception error)
        {
            return EncodeFailure(new("failed", "The presentation could not be opened.", BridgeFailureDetails.Capture(error, IncludesFailureDetail)));
        }
        if (outcome is BridgePresentationOutcome.Released or BridgePresentationOutcome.Capacity)
            return BridgeMessages.Presentation(outcome);
        string? registration = null;
        if (outcome == BridgePresentationOutcome.Ok && request.Root.TryGetProperty("handlers", out var handlers))
            registration = RegisterOpenHandlers(presentation, request.Connection, handlers);
        return ReadState(writer =>
        {
            writer.WriteString("presentation", outcome == BridgePresentationOutcome.Ok ? "ok" : "ignored");
            if (registration is not null) writer.WriteString("registration", registration);
        });
    }

    private BridgePresentationOutcome OpenPresentation(string presentation, BridgeConnection connection)
    {
        if (_content is not null) return _content.OpenPresentation(_name, presentation, connection);
        lock (_modelGate)
        {
            if (IsInactive) return BridgePresentationOutcome.Released;
            return (_presentations ??= new()).Open(presentation, connection, out _);
        }
    }

    private ReadOnlyMemory<byte> Close(BridgeRouteRequest request)
    {
        var presentation = request.Root.GetProperty("presentation").GetString()!;
        if (_content is not null) return BridgeMessages.Presentation(_content.ClosePresentation(_name, presentation, request.Connection));
        lock (_modelGate)
            return BridgeMessages.Presentation(_presentations?.Close(presentation, request.Connection, out _) ?? BridgePresentationOutcome.Ok);
    }

    private ReadOnlyMemory<byte> Snapshot() => ReadState(static _ => { });

    // The reply of open and snapshot: the route's full state, after everything pending was published.
    private ReadOnlyMemory<byte> ReadState(Action<Utf8JsonWriter> writeMore)
    {
        try
        {
            return _modelTurn.Run<ReadOnlyMemory<byte>>(() =>
            {
                lock (_modelGate)
                {
                    if (IsInactive) return BridgeMessages.Released;
                    // Before the route's first state, the state this read writes is that state.
                    if (_baselines is not null) PublishUnitCore();
                    try
                    {
                        var reply = WriteMessage(writer =>
                        {
                            writer.WriteStartObject();
                            writer.WriteBoolean("ok", true);
                            WriteState(writer, updateBaselines: !_delivery.StateRequested);
                            writeMore(writer);
                            writer.WriteEndObject();
                        });
                        return IsInactive ? BridgeMessages.Released : reply;
                    }
                    catch (Exception released) when (released is BridgeSnapshotDetachedException || IsInactive)
                    {
                        return BridgeMessages.Released;
                    }
                    catch (BridgeCollectionKeyException keys)
                    {
                        LogRejectedKeys(keys);
                        var failure = RejectedKeysReply(keys);
                        return EncodeFailure(failure with { Reason = BridgeFailure.StateWithheld });
                    }
                }
            });
        }
        // The model context is gone: the window closed.
        catch (ObjectDisposedException) { return BridgeMessages.Released; }
    }

    private async ValueTask<ReadOnlyMemory<byte>> SetAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        var member = request.Member!;
        if (!_settable.TryGetValue(member, out var property))
            return BridgeMessages.Rejected("invalid-request", $"The route {_name} has no settable member '{member}'.");
        BridgeRequestEnvelope.TryReadInteger(request.Root.GetProperty("edit"), out var edit);
        var value = request.Root.GetProperty("value");
        var waits = true;
        ReadOnlyMemory<byte> reply;
        try
        {
            reply = _modelTurn.Run<ReadOnlyMemory<byte>>(() =>
            {
                lock (_modelGate)
                {
                    if (IsInactive) return BridgeMessages.Released;
                    // A set whose edit is not higher than one admitted before is not applied.
                    if (!request.Connection.AdmitEdit(_name, member, edit))
                    {
                        waits = false;
                        return WriteMessage(writer =>
                        {
                            writer.WriteStartObject();
                            writer.WriteBoolean("ok", true);
                            writer.WriteNumber("revision", _revision);
                            writer.WriteBoolean("superseded", true);
                            writer.WriteEndObject();
                        });
                    }
                    var call = BridgeCall.Start(BridgeCallKind.Set, ModelName, _name, member);
                    try
                    {
                        property.Set(_vm, value);
                        call.Complete(BridgeCallOutcome.Ok);
                        return RanReply(null);
                    }
                    catch (Exception error) when (error is ArgumentException or FormatException or JsonException or OverflowException)
                    {
                        call.Complete(BridgeCallOutcome.Rejected);
                        waits = false;
                        return EncodeFailure(new("rejected", $"{property.Name} has an invalid value.",
                            BridgeFailureDetails.Capture(error, IncludesFailureDetail), Reason: BridgeFailure.InvalidInput));
                    }
                    catch (Exception error)
                    {
                        call.Complete(BridgeCallOutcome.Failed, error);
                        ViewsLog.SetterFailed(_logger, error, ModelName, property.Name, _name, BridgeTelemetry.ErrorType(error));
                        return RanReply(new("failed", $"Could not update {property.Name}.", BridgeFailureDetails.Capture(error, IncludesFailureDetail)));
                    }
                }
            });
        }
        catch (ObjectDisposedException) { return BridgeMessages.Released; }
        if (!waits) return reply;
        await WaitForPublishedAsync(cancellationToken).ConfigureAwait(false);
        return ReleasedIfInactive(reply);
    }

    private async ValueTask<ReadOnlyMemory<byte>> WriteAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        var member = request.Member!;
        if (_unownedCheckedFields.Contains(member))
            return EncodeFailure(new("failed", "Acknowledged field writes require a WindowContentSession."));
        if (!_checkedByMember.TryGetValue(member, out var property))
            return BridgeMessages.Rejected("invalid-request", $"The route {_name} has no checked field '{member}'.");
        BridgeFieldWriteRequest<object?> write;
        try
        {
            var root = request.Root;
            var requestId = root.GetProperty("requestId").GetString()!;
            if (requestId.Length > MaximumFieldWriteRequestIdLength) throw new FormatException("The requestId is too long.");
            BridgeRequestEnvelope.TryReadInteger(root.GetProperty("expectedVersion"), out var expectedVersion);
            write = new(requestId, expectedVersion,
                ReadCheckedValue(root.GetProperty("expectedValue"), property.Descriptor),
                ReadCheckedValue(root.GetProperty("value"), property.Descriptor));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidOperationException
            or KeyNotFoundException or OverflowException)
        {
            return EncodeFailure(new("rejected", $"{property.Descriptor.Name} has an invalid checked write.",
                BridgeFailureDetails.Capture(error, IncludesFailureDetail), Reason: BridgeFailure.InvalidInput));
        }
        ReadOnlyMemory<byte> reply;
        try
        {
            reply = _modelTurn.Run<ReadOnlyMemory<byte>>(() =>
            {
                lock (_modelGate)
                {
                    if (IsInactive) return BridgeMessages.Released;
                    var call = BridgeCall.Start(BridgeCallKind.Write, ModelName, _name, member);
                    try
                    {
                        var receipt = property.Registry.Apply(write);
                        call.Complete(BridgeCallOutcome.Ok);
                        return RanReply(null, writer => WriteReceipt(writer, receipt, property));
                    }
                    catch (Exception error)
                    {
                        call.Complete(BridgeCallOutcome.Failed, error);
                        ViewsLog.FieldWriteFailed(_logger, error, ModelName, property.Descriptor.Name, _name, BridgeTelemetry.ErrorType(error));
                        return RanReply(new("failed", $"Could not update {property.Descriptor.Name}.", BridgeFailureDetails.Capture(error, IncludesFailureDetail)));
                    }
                }
            });
        }
        catch (ObjectDisposedException) { return BridgeMessages.Released; }
        await WaitForPublishedAsync(cancellationToken).ConfigureAwait(false);
        return ReleasedIfInactive(reply);
    }

    private static void WriteReceipt(Utf8JsonWriter writer, BridgeFieldWriteReceipt<object?> receipt, CheckedPropertyBinding property)
    {
        writer.WritePropertyName("receipt");
        writer.WriteStartObject();
        switch (receipt.Kind)
        {
            case BridgeFieldWriteReceiptKind.Applied:
                writer.WriteString("kind", "applied");
                WriteFieldSnapshot(writer, "snapshot", receipt.Current, property.Descriptor);
                if (receipt.Validation is not null) writer.WriteString("validation", receipt.Validation);
                break;
            case BridgeFieldWriteReceiptKind.PostApplyValidationFailed:
                writer.WriteString("kind", "committed-with-error");
                WriteFieldSnapshot(writer, "snapshot", receipt.Current, property.Descriptor);
                writer.WriteString("message", receipt.Message ?? "The field changed but its validation did not complete.");
                break;
            case BridgeFieldWriteReceiptKind.Conflict:
                writer.WriteString("kind", "conflict");
                WriteFieldSnapshot(writer, "incoming", receipt.Current, property.Descriptor);
                writer.WriteString("message", receipt.Message ?? "The field baseline no longer matches.");
                break;
            default:
                writer.WriteString("kind", "rejected");
                writer.WriteString("message", receipt.Message ?? "The field write was rejected.");
                break;
        }
        writer.WriteEndObject();
    }

    private static void WriteFieldSnapshot(Utf8JsonWriter writer, string name, BridgeFieldSnapshot<object?> snapshot,
        CheckedPropertyDescriptor<T> descriptor)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WritePropertyName("value");
        WriteCheckedValue(writer, snapshot.Value, descriptor);
        writer.WriteNumber("version", snapshot.Version);
        writer.WriteEndObject();
    }

    // The command of a command, can or start request, and its decoded input: null with the reply when the member is not
    // a command, or the input is missing, extra or invalid.
    private CommandDescriptor<T>? ReadCommand(BridgeRouteRequest request, out object? input, out ReadOnlyMemory<byte> refusal)
    {
        input = null;
        refusal = default;
        var member = request.Member!;
        if (!_commandsByMember.TryGetValue(member, out var descriptor))
        {
            refusal = BridgeMessages.Rejected("invalid-request", $"The route {_name} has no command '{member}'.");
            return null;
        }
        var present = request.Root.TryGetProperty("input", out var element);
        if (present != descriptor.HasInput)
        {
            refusal = BridgeMessages.Rejected("invalid-request", descriptor.HasInput
                ? $"The command {member} takes an input." : $"The command {member} takes no input.");
            return null;
        }
        if (!present) return descriptor;
        try { input = ReadInput(descriptor, element); }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidOperationException)
        {
            refusal = EncodeFailure(new("rejected", $"{descriptor.Name} has an invalid input.",
                BridgeFailureDetails.Capture(error, IncludesFailureDetail), Reason: BridgeFailure.InvalidInput));
            return null;
        }
        return descriptor;
    }

    // Decodes a command's input. An out-of-range number is an invalid input; an OverflowException from the command
    // itself stays a failure.
    private static object? ReadInput(CommandDescriptor<T> descriptor, JsonElement input)
    {
        try
        {
            return descriptor.ReadInput!(input);
        }
        catch (OverflowException error) { throw new FormatException("The command input is out of range.", error); }
    }

    private static bool IsAsynchronous(CommandDescriptor<T> descriptor) =>
        descriptor.ExecuteAsync is not null || descriptor.ExecuteResultAsync is not null || descriptor.ExecuteStreamAsync is not null
        || descriptor.ResolveArgumentAsync is not null;

    private async ValueTask<ReadOnlyMemory<byte>> CommandAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        if (ReadCommand(request, out var key, out var refusal) is not { } descriptor) return refusal;
        var connection = request.Connection;
        ReadOnlyMemory<byte> reply;
        bool waits;
        if (IsAsynchronous(descriptor))
            (reply, waits) = await ExecuteAsync(descriptor, key, connection, cancellationToken).ConfigureAwait(false);
        else
        {
            try { (reply, waits) = _modelTurn.Run(() => Execute(descriptor, key, connection)); }
            catch (ObjectDisposedException) { return BridgeMessages.Released; }
        }
        if (!waits) return reply;
        await WaitForPublishedAsync(cancellationToken).ConfigureAwait(false);
        return ReleasedIfInactive(reply);
    }

    // A request can release its own route while it runs, as a command that navigates away from its page does: the
    // parent route's publication, which the reply waited for, detaches it. Such a reply has released: true instead of
    // its revision, and keeps its error (specs/application/README.md, "Released by the call").
    private ReadOnlyMemory<byte> ReleasedIfInactive(ReadOnlyMemory<byte> reply)
    {
        if (!IsInactive) return reply;
        using var document = JsonDocument.Parse(reply);
        var root = document.RootElement;
        if (!root.TryGetProperty("revision", out _)) return reply;
        return WriteMessage(writer =>
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("revision")) writer.WriteBoolean("released", true);
                else property.WriteTo(writer);
            }
            writer.WriteEndObject();
        });
    }

    // A synchronous command, in the request's model turn. The flag says whether the command ran.
    private (ReadOnlyMemory<byte> Reply, bool Ran) Execute(CommandDescriptor<T> descriptor, object? key, BridgeConnection connection)
    {
        lock (_modelGate)
        {
            if (IsInactive) return (BridgeMessages.Released, false);
            var call = BridgeCall.Start(BridgeCallKind.Command, ModelName, _name, descriptor.Name);
            try
            {
                if (!TryResolve(descriptor, key, out var argument))
                {
                    call.Complete(BridgeCallOutcome.Rejected);
                    return (EncodeFailure(BridgeFailure.KeyNotResolved(descriptor.Name)), false);
                }
                var command = descriptor.Get(_vm);
                if (!IsAvailable(descriptor, argument))
                {
                    call.Complete(BridgeCallOutcome.Rejected);
                    return (EncodeFailure(new("rejected", $"{descriptor.Name} is unavailable.", Reason: BridgeFailure.Unavailable)), false);
                }
                using (EnterInvocation(descriptor, connection, CancellationToken.None))
                    ((ICommand)command).Execute(argument);
                call.Complete(BridgeCallOutcome.Ok);
                return (RanReply(null), true);
            }
            catch (Exception error) when (BridgeDomainFailures.Find(error) is { } domain)
            {
                return (RanReply(CommandDomainFailure(ref call, descriptor, domain)), true);
            }
            catch (OperationCanceledException)
            {
                call.Complete(BridgeCallOutcome.Cancelled);
                return (RanReply(new("cancelled", $"{descriptor.Name} was cancelled.")), true);
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
            {
                call.Complete(BridgeCallOutcome.Rejected);
                return (EncodeFailure(new("rejected", $"{descriptor.Name} has an invalid input.",
                    BridgeFailureDetails.Capture(error, IncludesFailureDetail), Reason: BridgeFailure.InvalidInput)), false);
            }
            catch (Exception error)
            {
                call.Complete(BridgeCallOutcome.Failed, error);
                ViewsLog.CommandFailed(_logger, error, ModelName, descriptor.Name, _name, BridgeTelemetry.ErrorType(error));
                return (RanReply(new("failed", $"{descriptor.Name} failed.", BridgeFailureDetails.Capture(error, IncludesFailureDetail))), true);
            }
        }
    }

    // An awaited command: admitted in a model turn, awaited outside it, and answered in a new turn.
    private async ValueTask<(ReadOnlyMemory<byte> Reply, bool Ran)> ExecuteAsync(CommandDescriptor<T> descriptor, object? key,
        BridgeConnection connection, CancellationToken token)
    {
        // The awaited command is window work like an admitted operation: closing the window waits for it before the
        // host disposes its scope, and cancels it after the close timeout.
        var atCapacity = false;
        using var admission = _content is null ? null : _content.Operations.TryBeginAwaited(token, out atCapacity);
        if (atCapacity)
        {
            var refused = BridgeCall.Start(BridgeCallKind.Command, ModelName, _name, descriptor.Name);
            refused.Complete(BridgeCallOutcome.Rejected);
            return (EncodeFailure(new("rejected",
                $"{descriptor.Name} was not run: the window is already running {BridgeOperationRegistry.MaximumAwaitedExecutions} awaited commands.",
                Reason: "capacity")), false);
        }
        if (_content is not null && admission is null)
            return (EncodeFailure(new(BridgeFailure.Released, "The window is closing.")), false);
        var cancellation = admission?.Token ?? token;
        Task<BridgeOperationResult>? execution = null;
        Task<object?>? resolution = null;
        var call = BridgeCall.Start(BridgeCallKind.Command, ModelName, _name, descriptor.Name);
        try
        {
            var rejected = BridgeCallOutcome.Released;
            ReadOnlyMemory<byte>? rejection = _modelTurn.Run<ReadOnlyMemory<byte>?>(() =>
            {
                lock (_modelGate)
                {
                    if (IsInactive) return BridgeMessages.Released;
                    rejected = BridgeCallOutcome.Rejected;
                    // An asynchronous resolver starts in this turn; the command is admitted in the next.
                    if (descriptor.ResolveArgumentAsync is { } resolveAsync)
                    {
                        resolution = resolveAsync(_vm, key, cancellation).AsTask();
                        return null;
                    }
                    return TryResolve(descriptor, key, out var argument) ? Begin(argument)
                        : EncodeFailure(BridgeFailure.KeyNotResolved(descriptor.Name));
                }
            });
            if (rejection is null && resolution is not null)
            {
                var resolved = await resolution.ConfigureAwait(false);
                rejected = BridgeCallOutcome.Released;
                rejection = _modelTurn.Run<ReadOnlyMemory<byte>?>(() =>
                {
                    lock (_modelGate)
                    {
                        if (IsInactive) return BridgeMessages.Released;
                        rejected = BridgeCallOutcome.Rejected;
                        return resolved is null ? EncodeFailure(BridgeFailure.KeyNotResolved(descriptor.Name)) : Begin(resolved);
                    }
                });
            }
            if (rejection is { } refused)
            {
                call.Complete(rejected);
                return (refused, false);
            }
            await execution!.ConfigureAwait(false);
            call.Complete(BridgeCallOutcome.Ok);
            return (ReplyAfterCommand(null), true);
        }
        catch (Exception error) when (BridgeDomainFailures.Find(error) is { } domain)
        {
            var failure = CommandDomainFailure(ref call, descriptor, domain);
            return (ReplyAfterCommand(failure), true);
        }
        catch (OperationCanceledException)
        {
            call.Complete(BridgeCallOutcome.Cancelled);
            return (ReplyAfterCommand(new("cancelled", $"{descriptor.Name} was cancelled.")), true);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
        {
            call.Complete(BridgeCallOutcome.Rejected);
            var detail = BridgeFailureDetails.Capture(error, IncludesFailureDetail);
            return (ReplyAfterCommand(new("rejected", $"{descriptor.Name} has an invalid input.", detail, Reason: BridgeFailure.InvalidInput)), true);
        }
        catch (Exception error)
        {
            call.Complete(BridgeCallOutcome.Failed, error);
            ViewsLog.CommandFailed(_logger, error, ModelName, descriptor.Name, _name, BridgeTelemetry.ErrorType(error));
            var detail = BridgeFailureDetails.Capture(error, IncludesFailureDetail);
            return (ReplyAfterCommand(new("failed", $"{descriptor.Name} failed.", detail)), true);
        }

        // In a model turn: checks availability with the command's parameter and starts the command.
        ReadOnlyMemory<byte>? Begin(object? argument)
        {
            if (!IsAvailable(descriptor, argument))
                return EncodeFailure(new("rejected", $"{descriptor.Name} is unavailable.", Reason: BridgeFailure.Unavailable));
            using var invocation = EnterInvocation(descriptor, connection, cancellation);
            execution = InvokeCommandAsync(descriptor, argument, cancellation);
            return null;
        }
    }

    // The reply of an awaited command after it completed. The model context can be gone when the command completes
    // after its window was finalized; there is no state left to publish.
    private ReadOnlyMemory<byte> ReplyAfterCommand(BridgeFailure? error)
    {
        try
        {
            return _modelTurn.Run(() =>
            {
                lock (_modelGate) return RanReply(error);
            });
        }
        catch (ObjectDisposedException)
        {
            return EncodeRanReply(error, revision: null, released: true);
        }
    }

    private async ValueTask<ReadOnlyMemory<byte>> CanAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        if (ReadCommand(request, out var key, out var refusal) is not { } descriptor) return refusal;
        bool available;
        if (descriptor.ResolveArgumentAsync is null)
        {
            try
            {
                available = _modelTurn.Run(() =>
                {
                    lock (_modelGate)
                    {
                        if (IsInactive) return false;
                        try { return TryResolve(descriptor, key, out var argument) && IsAvailable(descriptor, argument); }
                        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidOperationException)
                        { return false; }
                    }
                });
            }
            catch (ObjectDisposedException) { return BridgeMessages.Released; }
        }
        else available = await QueryAvailabilityAsync(descriptor, key, cancellationToken).ConfigureAwait(false);
        return available ? CanTrue : CanFalse;
    }

    private static readonly ReadOnlyMemory<byte> CanTrue = """{"ok":true,"value":true}"""u8.ToArray();
    private static readonly ReadOnlyMemory<byte> CanFalse = """{"ok":true,"value":false}"""u8.ToArray();

    // can of a command with an asynchronous key resolver: an unresolved key, a closing window and a cancelled resolution
    // answer false. The resolution is awaited window work, as an awaited command is.
    private async ValueTask<bool> QueryAvailabilityAsync(CommandDescriptor<T> descriptor, object? key, CancellationToken token)
    {
        using var admission = _content?.Operations.TryBeginAwaited(token, out _);
        if (_content is not null && admission is null) return false;
        try
        {
            Task<object?>? resolution = null;
            var inactive = _modelTurn.Run(() =>
            {
                if (IsInactive) return true;
                resolution = descriptor.ResolveArgumentAsync!(_vm, key, admission?.Token ?? token).AsTask();
                return false;
            });
            if (inactive) return false;
            var resolved = await resolution!.ConfigureAwait(false);
            return _modelTurn.Run(() => !IsInactive && resolved is not null && IsAvailable(descriptor, resolved));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidOperationException
            or OperationCanceledException or ObjectDisposedException)
        { return false; }
    }

    // The reply of a request that ran, in its model turn: the route's revision after publishing everything pending, or
    // released: true when the request released its own route, or the reason state-withheld.
    private byte[] RanReply(BridgeFailure? error, Action<Utf8JsonWriter>? writeMore = null)
    {
        if (IsInactive) return EncodeRanReply(error, revision: null, released: true);
        var revision = PublishForReply();
        if (IsInactive) return EncodeRanReply(error, revision: null, released: true);
        if (revision is null)
        {
            var withheld = error is null
                ? new BridgeFailure("failed", "The call ran, but the updated state could not be published: a keyed collection has invalid keys.",
                    Reason: BridgeFailure.StateWithheld)
                : error with { Reason = BridgeFailure.StateWithheld };
            return EncodeRanReply(withheld, revision: null, released: false);
        }
        return EncodeRanReply(error, revision, released: false, writeMore);
    }

    private static byte[] EncodeRanReply(BridgeFailure? error, long? revision, bool released, Action<Utf8JsonWriter>? writeMore = null) =>
        WriteMessage(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", error is null);
            if (revision is { } value) writer.WriteNumber("revision", value);
            if (released) writer.WriteBoolean("released", true);
            if (error is not null) WriteError(writer, error);
            writeMore?.Invoke(writer);
            writer.WriteEndObject();
        });

    private static byte[] EncodeFailure(BridgeFailure error) => EncodeRanReply(error, revision: null, released: false);

    private static void WriteError(Utf8JsonWriter writer, BridgeFailure error)
    {
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteString("kind", error.Kind);
        writer.WriteString("message", error.Message);
        if (error.Reason is { } reason) writer.WriteString("reason", reason);
        if (error.Failure is { } failure)
        {
            writer.WritePropertyName("failure");
            writer.WriteRawValue(failure, skipInputValidation: true);
        }
        // D-1: a declared failure never carries detail.
        else if (error.Kind != BridgeFailure.DomainFailed) BridgeFailureDetails.Write(writer, error.Detail);
        writer.WriteEndObject();
    }

    // A declared failure is an expected outcome: a Debug log with the exception (D-12) and no failure metric. One that
    // cannot be sent as declared falls back to the failed reply and is logged as an error.
    private BridgeFailure CommandDomainFailure(ref BridgeCall call, CommandDescriptor<T> descriptor, RunicFailureException domain)
    {
        var failureType = BridgeDomainFailures.TypeName(domain);
        var encoded = BridgeDomainFailures.TryEncode(descriptor.EncodeFailure, domain, out var reason, out var logged);
        if (encoded is not null)
        {
            call.Complete(BridgeCallOutcome.DomainFailed);
            ViewsLog.CommandDomainFailed(_logger, domain, ModelName, descriptor.Name, _name, failureType);
            return new(BridgeFailure.DomainFailed, $"{descriptor.Name} failed.", Failure: encoded);
        }
        call.Complete(BridgeCallOutcome.Failed, domain);
        ViewsLog.DomainFailureNotEncoded(_logger, logged, ModelName, descriptor.Name, _name, failureType, reason);
        return new("failed", $"{descriptor.Name} failed.", BridgeFailureDetails.Capture(domain, IncludesFailureDetail));
    }

    // A command invocation reaches the interactions of the requesting connection's presentation.
    private IDisposable? EnterInvocation(CommandDescriptor<T> descriptor, BridgeConnection connection, CancellationToken token) =>
        _content is null ? null : RunicInteractionInvocation.Enter(_content, _name, connection.ClientKey, connection.Key, descriptor.Name, token);

    private bool IsAvailable(CommandDescriptor<T> descriptor, object? argument) =>
        descriptor.CanExecute?.Invoke(_vm, argument)
        ?? (descriptor.Get(_vm) is ICommand command && command.CanExecute(argument));

    // The command's parameter for a decoded input: the input itself, or the object a synchronous key resolver returns
    // for it. False when the key names no object.
    private bool TryResolve(CommandDescriptor<T> descriptor, object? key, out object? argument)
    {
        if (descriptor.ResolveArgument is not { } resolve)
        {
            argument = key;
            return true;
        }
        argument = resolve(_vm, key);
        return argument is not null;
    }

    private async Task<BridgeOperationResult> InvokeCommandAsync(CommandDescriptor<T> descriptor, object? argument, CancellationToken token)
    {
        if (descriptor.ExecuteStreamAsync is { } executeStream)
            return await executeStream(_vm, new BridgeOperationExecution(descriptor.CreateStream!()), token, argument).ConfigureAwait(false);
        if (descriptor.ExecuteResultAsync is { } executeResult)
            return await executeResult(_vm, token, argument).ConfigureAwait(false);
        // A synchronous command is awaited only through an asynchronous key resolver.
        if (descriptor.ExecuteAsync is null)
        {
            ((ICommand)descriptor.Get(_vm)).Execute(argument);
            return BridgeOperationResult.None;
        }
        await descriptor.ExecuteAsync(_vm, token, argument).ConfigureAwait(false);
        return BridgeOperationResult.None;
    }

    private static object? ReadCheckedValue(JsonElement value, CheckedPropertyDescriptor<T> descriptor) => descriptor.ValueKind switch
    {
        CheckedFieldValueKind.String when value.ValueKind is JsonValueKind.String => value.GetString(),
        CheckedFieldValueKind.NullableString when value.ValueKind is JsonValueKind.String => value.GetString(),
        CheckedFieldValueKind.NullableString when value.ValueKind is JsonValueKind.Null => null,
        CheckedFieldValueKind.Int32 when value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var integer) => integer,
        CheckedFieldValueKind.Boolean when value.ValueKind is JsonValueKind.True => true,
        CheckedFieldValueKind.Boolean when value.ValueKind is JsonValueKind.False => false,
        CheckedFieldValueKind.Json => descriptor.Read!(value),
        _ => throw new FormatException("The checked field value does not match its generated type."),
    };

    private static string EncodeCheckedValue(object? value, CheckedPropertyDescriptor<T> descriptor) =>
        BridgeCanonicalJson.Encode(WriteCheckedValue, (value, descriptor));

    private static void WriteCheckedValue(Utf8JsonWriter writer, (object? Value, CheckedPropertyDescriptor<T> Descriptor) checkedValue) =>
        WriteCheckedValue(writer, checkedValue.Value, checkedValue.Descriptor);

    private static void WriteCheckedValue(Utf8JsonWriter writer, object? value, CheckedPropertyDescriptor<T> descriptor)
    {
        switch (descriptor.ValueKind)
        {
            case CheckedFieldValueKind.String:
                writer.WriteStringValue((string)value!);
                return;
            case CheckedFieldValueKind.NullableString:
                if (value is null) writer.WriteNullValue();
                else writer.WriteStringValue((string)value);
                return;
            case CheckedFieldValueKind.Int32:
                writer.WriteNumberValue((int)value!);
                return;
            case CheckedFieldValueKind.Boolean:
                writer.WriteBooleanValue((bool)value!);
                return;
            case CheckedFieldValueKind.Json:
                descriptor.Write!(writer, value);
                return;
            default:
                throw new InvalidOperationException("The checked field type is unsupported.");
        }
    }
}
