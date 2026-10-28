using System.Text.Json;
using System.Windows.Input;
namespace Runic.Application.Views.Generated;

// The operation and interaction requests of a generated Bridge's route (Views protocol 4: start, status, watch,
// unwatch, cancel, stream, interactions and interaction-reply). This part of ViewModelBridge belongs to P5 of the
// protocol-4 plan; BridgeRuntime.cs (P4) forwards those request kinds to HandleOperationAsync and calls
// RegisterOpenHandlers for the handlers of an open. The start code below is protocol 3's, moved here unchanged.
public partial class ViewModelBridge<T>
{
#pragma warning disable CA1822 // Stubs until P5 implements them.
    // Seam (P4 -> P5): every request of the kinds start, status, watch, unwatch, cancel, stream, interactions and
    // interaction-reply on this Bridge's route. The envelope was checked against the message catalogue. P5 replaces
    // this stub.
    private partial ValueTask<ReadOnlyMemory<byte>> HandleOperationAsync(BridgeRouteRequest request, CancellationToken cancellationToken) =>
        new(BridgeMessages.Rejected("invalid-request", $"'{request.Kind}' is not served by this producer yet."));

    // Seam (P4 -> P5): the handlers of an open, registered as generation 0 after the presentation was opened. The
    // result is written as the open reply's registration; null writes none. P5 replaces this stub.
    private partial string? RegisterOpenHandlers(string presentation, BridgeConnection connection, JsonElement handlers) => null;
#pragma warning restore CA1822

    private string StartOperation(WindowContentSession content, CommandDescriptor<T> descriptor, IBridgeArguments arguments)
    {
        lock (_modelGate)
        {
            if (IsInactive) return EncodeOperationStartFailure(BridgeFailure.Released, "The Bridge is closed.");
            var call = BridgeCall.Start(BridgeCallKind.OperationStart, ModelName, _name, descriptor.Name);
            try
            {
                var request = ReadOperationStart(descriptor, arguments, out var key);
                // The registry asks only for a new request id, so a repeated start observes its operation
                // before the key is resolved again.
                var admission = AcceptOperation(content, descriptor, arguments, request, () =>
                    !TryResolve(descriptor, key, out var argument) ? (BridgeFailure.UnresolvedKey, null)
                    : IsAvailable(descriptor, argument) ? (null, argument) : (BridgeFailure.Unavailable, argument));
                var reply = BridgeOperationRouter.EncodeAdmission(admission);
                call.Complete(BridgeCallOutcome.Of(admission.Kind, admission.Reason));
                return reply;
            }
            catch (Exception error)
            {
                return OperationStartFailure(ref call, descriptor, error);
            }
        }
    }

    // start<Command>(key) of a command with an asynchronous key resolver. The resolution starts in a model turn and
    // is awaited window work; the operation is admitted in a new turn, which still lets a repeated request id observe
    // its operation, whatever the key resolves to now.
    private async ValueTask<string> StartOperationAsync(WindowContentSession content, CommandDescriptor<T> descriptor,
        IBridgeArguments arguments, CancellationToken token)
    {
        using var awaited = content.Operations.TryBeginAwaited(token, out var atCapacity);
        if (awaited is null)
            return atCapacity
                ? EncodeOperationStartFailure("rejected", $"{descriptor.Name} was not started: the window is already running {BridgeOperationRegistry.MaximumAwaitedExecutions} awaited commands.")
                : EncodeOperationStartFailure(BridgeFailure.Released, "The window is closing.");
        var call = BridgeCall.Start(BridgeCallKind.OperationStart, ModelName, _name, descriptor.Name);
        try
        {
            BridgeOperationRequest? request = null;
            Task<object?>? resolution = null;
            var released = _modelTurn.Run(() =>
            {
                if (IsInactive) return true;
                request = ReadOperationStart(descriptor, arguments, out var key);
                resolution = descriptor.ResolveArgumentAsync!(_vm, key, awaited.Token).AsTask();
                return false;
            });
            object? resolved = null;
            if (!released) resolved = await resolution!.ConfigureAwait(false);
            var admission = released ? null : _modelTurn.Run(() => IsInactive ? null
                : AcceptOperation(content, descriptor, arguments, request!.Value, () =>
                    resolved is null ? (BridgeFailure.UnresolvedKey, null)
                    : IsAvailable(descriptor, resolved) ? (null, resolved) : (BridgeFailure.Unavailable, resolved)));
            if (admission is null)
            {
                call.Complete(BridgeCallOutcome.Released);
                return EncodeOperationStartFailure(BridgeFailure.Released, "The Bridge is closed.");
            }
            call.Complete(BridgeCallOutcome.Of(admission.Kind, admission.Reason));
            return BridgeOperationRouter.EncodeAdmission(admission);
        }
        catch (Exception error)
        {
            return OperationStartFailure(ref call, descriptor, error);
        }
    }

    // The request of an operation start and its decoded argument, the key of a key-resolved command. The digest
    // that identifies the request is the argument's canonical JSON.
    private BridgeOperationRequest ReadOperationStart(CommandDescriptor<T> descriptor, IBridgeArguments arguments, out object? argument)
    {
        string requestId;
        argument = null;
        if (descriptor.ReadArgument is null) requestId = arguments.GetString();
        else
        {
            var payload = arguments.GetString();
            if (!BridgeInputLimit.Fits(payload, MaximumInputBytes)) throw new FormatException("The command input is too large.");
            using var document = JsonDocument.Parse(payload);
            requestId = document.RootElement.GetProperty("requestId").GetString() ?? "";
            argument = ReadArgument(descriptor, new JsonBridgeArguments(document.RootElement.GetProperty("input").GetRawText(), arguments));
        }
        _ = BridgeOperationIdentity.Create(OperationContract(), requestId);
        var decoded = argument;
        var canonicalInput = descriptor.EncodeArgument?.Invoke(decoded) ?? WriteJson(writer =>
        {
            switch (decoded)
            {
                case null: writer.WriteNullValue(); break;
                case string value: writer.WriteStringValue(value); break;
                case int value: writer.WriteNumberValue(value); break;
                case bool value: writer.WriteBooleanValue(value); break;
                default: throw new FormatException("The command needs a generated input codec.");
            }
        });
        return new BridgeOperationRequest(OperationContract(), descriptor.Name, requestId, BridgeOperationRequest.CanonicalDigest(canonicalInput));
    }

    // Admits an operation. admit runs only for a new request id and returns why it cannot start (null when it can)
    // and the command's parameter.
    private BridgeOperationAdmissionReply AcceptOperation(WindowContentSession content, CommandDescriptor<T> descriptor,
        IBridgeArguments arguments, BridgeOperationRequest request, Func<(string? Refusal, object? Argument)> admit)
    {
        object? argument = null;
        Func<string?> refusal = () =>
        {
            var (reason, value) = admit();
            argument = value;
            return reason;
        };
        // RunicOperationRetirement: the operation's owner is this Bridge's model, and the models that present it,
        // read when it starts; retiring one of them cancels or waits for it.
        var retirement = descriptor.Retirement is RunicOperationRetirement.Keep ? null
            : new BridgeOperationRetirement(descriptor.Retirement, _vm, content.PresentingOwners(_vm), _name);
        return descriptor.ExecuteStreamAsync is { } executeStream
            ? content.Operations.AcceptUnlessRefused(request, refusal, descriptor.CreateStream!(),
                (execution, cancellation) => ObserveOperationAsync(_logger, descriptor.Name, _name, descriptor.EncodeFailure, () =>
                {
                    using var invocation = EnterInvocation(descriptor, arguments, cancellation);
                    return executeStream(_vm, execution, cancellation, argument);
                }), retirement)
            : content.Operations.AcceptUnlessRefused(request, refusal,
                cancellation => ObserveOperationAsync(_logger, descriptor.Name, _name, descriptor.EncodeFailure, () =>
                {
                    using var invocation = EnterInvocation(descriptor, arguments, cancellation);
                    return InvokeCommandAsync(descriptor, argument, cancellation);
                }), retirement);
    }

    private string OperationStartFailure(ref BridgeCall call, CommandDescriptor<T> descriptor, Exception error)
    {
        switch (error)
        {
            case OperationCanceledException:
                call.Complete(BridgeCallOutcome.Cancelled);
                return EncodeOperationStartFailure("cancelled", $"{descriptor.Name} was cancelled.");
            case ArgumentException or FormatException or JsonException or InvalidOperationException:
                call.Complete(BridgeCallOutcome.Rejected);
                return EncodeOperationStartFailure("rejected", $"{descriptor.Name} has an invalid argument.", BridgeFailureDetails.Capture(error, IncludesFailureDetail));
            default:
                call.Complete(BridgeCallOutcome.Failed, error);
                ViewsLog.OperationAdmissionFailed(_logger, error, ModelName, descriptor.Name, _name, BridgeTelemetry.ErrorType(error));
                return EncodeOperationStartFailure("failed", $"{descriptor.Name} could not start.", BridgeFailureDetails.Capture(error, IncludesFailureDetail));
        }
    }

    // Runs the admitted work of an operation inside its span. The registry
    // owns the terminal state and logs a failure. A declared failure reaches
    // the registry already encoded, so the registry stays type-agnostic; one
    // that cannot be sent is logged here (1008), where the model is known.
    private static async Task<BridgeOperationResult> ObserveOperationAsync(Microsoft.Extensions.Logging.ILogger logger,
        string member, string route,
        Func<object, string?>? encodeFailure, Func<Task<BridgeOperationResult>> run)
    {
        var call = BridgeCall.Start(BridgeCallKind.Operation, ModelName, route, member);
        try
        {
            var result = await run().ConfigureAwait(false);
            call.Complete(result.DeliveryFailure is null ? BridgeCallOutcome.Ok : BridgeCallOutcome.DeliveryFailed);
            return result;
        }
        catch (Exception error) when (BridgeDomainFailures.Find(error) is { } domain)
        {
            // Classified by exception type, like success: a declared failure
            // thrown while cancellation is requested is still domain-failed.
            var failureType = BridgeDomainFailures.TypeName(domain);
            var encoded = BridgeDomainFailures.TryEncode(encodeFailure, domain, out var reason, out var logged);
            if (encoded is null)
            {
                call.Complete(BridgeCallOutcome.Failed, domain);
                ViewsLog.DomainFailureNotEncoded(logger, logged, ModelName, member, route, failureType, reason);
                throw new BridgeDomainFailureNotEncodedException(domain);
            }
            call.Complete(BridgeCallOutcome.DomainFailed);
            throw new BridgeDomainFailedException(encoded, failureType, domain);
        }
        catch (OperationCanceledException)
        {
            call.Complete(BridgeCallOutcome.Cancelled);
            throw;
        }
        catch (Exception error)
        {
            call.Complete(BridgeCallOutcome.Failed, error);
            throw;
        }
    }

    private sealed class JsonBridgeArguments(string json, IBridgeArguments source) : IBridgeArguments
    {
        public string GetString() => json;
        public long GetInt64() { using var document = JsonDocument.Parse(json); return document.RootElement.GetInt64(); }
        public bool GetBoolean() { using var document = JsonDocument.Parse(json); return document.RootElement.GetBoolean(); }
        public string? ClientKey => source.ClientKey;
        public string? ConnectionKey => source.ConnectionKey;
    }

    // A ViewModel type and generated fingerprint describe its wire shape, but
    // a window can host several instances of that shape. The route/reference
    // separates their advanced request-id namespaces.
    private string OperationContract() => $"{typeof(T).FullName}:{_contractFingerprint}:{_name}";

    private static string EncodeOperationStartFailure(string kind, string reason, BridgeFailureDetail? detail = null) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", kind);
        writer.WriteString("reason", reason);
        BridgeFailureDetails.Write(writer, detail);
        writer.WriteEndObject();
    });
}
