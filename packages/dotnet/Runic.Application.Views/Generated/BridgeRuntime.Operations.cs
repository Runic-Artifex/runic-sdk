using System.Text.Json;
using System.Windows.Input;
namespace Runic.Application.Views.Generated;

// The operation and interaction requests of a generated Bridge's route (Views protocol 4, specs/application/README.md,
// "Operations" and "Interactions"): start, status, watch, unwatch, cancel, stream, interactions and interaction-reply.
// BridgeRuntime.cs forwards those request kinds to HandleOperationAsync and calls RegisterOpenHandlers for the handlers
// of an open.
//
// - An operation is keyed by (contract, route, operation id). `contract` is "{ViewModel full name}:{fingerprint}"; a
//   start with another contract comes from a generated client of another build and is refused (invalid-request).
// - start admits in one model turn: a known key is a duplicate when member and input are canonically equal, and an
//   identity conflict otherwise; a new one is checked (input, key, availability) and started, and the reply carries
//   the revision that includes its effects (PublishForReply).
// - status, watch, unwatch, cancel and stream go to the window's BridgeOperationRouter; a terminal status carries the
//   revision of the route's first state that includes the operation's effects, read in a turn once the work ended.
// - interactions and interaction-reply go to the window's BridgeInteractionRouter.
public partial class ViewModelBridge<T>
{
    // The digest of a start without an input; a canonical JSON text never equals it.
    private const string NoInputDigest = "no-input";

    private partial ValueTask<ReadOnlyMemory<byte>> HandleOperationAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case "interactions" or "interaction-reply":
                // A Bridge without a window has no presentations to register or answer.
                if (_content is null)
                    return new(request.Kind == "interactions"
                        ? """{"ok":true,"registration":"ignored"}"""u8.ToArray()
                        : """{"ok":true,"result":"stale"}"""u8.ToArray());
                return new(_content.Interactions.Handle(request));
        }
        if (_content is null || FindOperation(request.Member!) is not { } descriptor)
            return new(BridgeMessages.Rejected("invalid-request",
                $"The route '{_name}' has no operation '{request.Member}'."));
        return request.Kind == "start"
            ? StartAsync(_content, descriptor, request, cancellationToken)
            : new(_content.Operations.Handle(request));
    }

    private partial string? RegisterOpenHandlers(string presentation, BridgeConnection connection, JsonElement handlers) =>
        _content?.Interactions.RegisterOpenHandlers(_name, presentation, connection, handlers) ?? "ignored";

    // The route's operation contract: "{view}:{fingerprint}".
    private string ViewContract => $"{typeof(T).FullName}:{_contractFingerprint}";

    // The asynchronous command a member names, when this Bridge can run it as an operation: it belongs to a window
    // and has a generated contract fingerprint.
    private CommandDescriptor<T>? FindOperation(string member)
    {
        if (_content is null || _contractFingerprint is null) return null;
        foreach (var command in _commands)
            if (command.Member == member
                && (command.ExecuteAsync is not null || command.ExecuteResultAsync is not null || command.ExecuteStreamAsync is not null))
                return command;
        return null;
    }

    private async ValueTask<ReadOnlyMemory<byte>> StartAsync(WindowContentSession content, CommandDescriptor<T> descriptor,
        BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        var root = request.Root;
        var hasInput = root.TryGetProperty("input", out var input);
        if (hasInput != descriptor.HasInput)
            return BridgeMessages.Rejected("invalid-request", hasInput
                ? $"The operation '{descriptor.Member}' takes no input."
                : $"The operation '{descriptor.Member}' needs an input.");
        var contract = root.GetProperty("contract").GetString()!;
        if (contract != ViewContract)
            return BridgeMessages.Rejected("invalid-request",
                $"The operation contract '{contract}' is not this route's; the page's generated client is from another build.");
        var identity = BridgeOperationIdentity.Create(contract, _name, root.GetProperty("operation").GetString()!);
        string digest;
        try { digest = hasInput ? BridgeOperationRequest.CanonicalDigest(input) : NoInputDigest; }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return await ReplyToStartAsync(content, BridgeOperationRouter.Rejection(identity, BridgeFailure.InvalidInput),
                cancellationToken).ConfigureAwait(false);
        }
        var operation = BridgeOperationRequest.Create(identity, descriptor.Member, digest) with { Name = descriptor.Name };
        var call = BridgeCall.Start(BridgeCallKind.OperationStart, ModelName, _name, descriptor.Name);
        try
        {
            var admission = descriptor.ResolveArgumentAsync is null
                ? _modelTurn.Run(() => IsInactive ? null : Admit(content, descriptor, request.Connection, operation, () =>
                    TryDecodeInput(descriptor, hasInput ? input : null, out var key) is { } invalid ? (invalid, null)
                    : !TryResolve(descriptor, key, out var argument) ? (BridgeFailure.UnresolvedKey, null)
                    : IsAvailable(descriptor, argument) ? (null, argument) : (BridgeFailure.Unavailable, argument)))
                : await AdmitResolvingAsync(content, descriptor, request.Connection, operation, hasInput ? input : null,
                    cancellationToken).ConfigureAwait(false);
            if (admission is null)
            {
                call.Complete(BridgeCallOutcome.Released);
                return BridgeMessages.Released.ToArray();
            }
            call.Complete(BridgeCallOutcome.Of(admission.Kind, admission.Reason));
            return await ReplyToStartAsync(content, admission, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            call.Complete(BridgeCallOutcome.Failed, error);
            ViewsLog.OperationAdmissionFailed(_logger, error, ModelName, descriptor.Name, _name, BridgeTelemetry.ErrorType(error));
            return BridgeMessages.Failure("failed", null, $"{descriptor.Member} could not start.");
        }
    }

    // A start of a command with an asynchronous key resolver: the input is decoded and its resolution started in a
    // model turn, awaited as window work, and the operation admitted in a new turn, which still lets a repeated
    // operation id observe its operation, whatever the key resolves to now. Null when the route was released.
    private async ValueTask<BridgeOperationAdmissionReply?> AdmitResolvingAsync(WindowContentSession content,
        CommandDescriptor<T> descriptor, BridgeConnection connection, BridgeOperationRequest operation, JsonElement? input,
        CancellationToken cancellationToken)
    {
        using var awaited = content.Operations.TryBeginAwaited(cancellationToken, out var atCapacity);
        if (awaited is null)
            return BridgeOperationRouter.Rejection(operation.Identity, atCapacity ? "capacity" : "owner-closing");
        string? invalid = null;
        Task<object?>? resolution = null;
        var released = _modelTurn.Run(() =>
        {
            if (IsInactive) return true;
            invalid = TryDecodeInput(descriptor, input, out var key);
            if (invalid is null) resolution = descriptor.ResolveArgumentAsync!(_vm, key, awaited.Token).AsTask();
            return false;
        });
        if (released) return null;
        var resolved = resolution is null ? null : await resolution.ConfigureAwait(false);
        return _modelTurn.Run(() => IsInactive ? null : Admit(content, descriptor, connection, operation, () =>
            invalid is not null ? (invalid, null)
            : resolved is null ? (BridgeFailure.UnresolvedKey, null)
            : IsAvailable(descriptor, resolved) ? (null, resolved) : (BridgeFailure.Unavailable, resolved)));
    }

    // The reply of a start the window answered: {ok, revision, admission, reason?, status?}, after the pushes that
    // the admission's turn requested were handed to the transport. A start that released its route replies
    // released: true without a revision; one whose state is withheld replies state-withheld.
    private async ValueTask<ReadOnlyMemory<byte>> ReplyToStartAsync(WindowContentSession content,
        BridgeOperationAdmissionReply admission, CancellationToken cancellationToken)
    {
        var revision = await ReplyRevisionAsync(cancellationToken).ConfigureAwait(false);
        var status = admission.Kind is BridgeOperationAdmissionKind.Accepted or BridgeOperationAdmissionKind.Duplicate
            ? admission.Terminal ?? content.Operations.Lookup(admission.Identity, MemberOf(admission))
            : null;
        if (revision is { } published)
        {
            if (admission.Kind is BridgeOperationAdmissionKind.Accepted)
                content.Operations.SetAdmissionRevision(admission.Identity, published);
            return BridgeOperationRouter.EncodeStart(admission, status, published);
        }
        if (IsInactive) return BridgeOperationRouter.EncodeStart(admission, status, revision: null);
        return BridgeMessages.Failure("failed", "state-withheld",
            "The start ran, but the route cannot publish a state that includes its effects.");
    }

    private static string MemberOf(BridgeOperationAdmissionReply admission) => admission.Member ?? "";

    // Admits a protocol-4 operation. admit runs only for a new operation id and returns why it cannot start (null
    // when it can) and the command's parameter. The work runs in the invoking connection's interaction scope, and its
    // terminal status reads the route's revision in a turn once it ended.
    private BridgeOperationAdmissionReply Admit(WindowContentSession content, CommandDescriptor<T> descriptor,
        BridgeConnection connection, BridgeOperationRequest operation, Func<(string? Refusal, object? Argument)> admit)
    {
        object? argument = null;
        Func<string?> refusal = () =>
        {
            var (reason, value) = admit();
            argument = value;
            return reason;
        };
        var retirement = descriptor.Retirement is RunicOperationRetirement.Keep ? null
            : new BridgeOperationRetirement(descriptor.Retirement, _vm, content.PresentingOwners(_vm), _name);
        Func<long?> terminalRevision = () => _modelTurn.Run(PublishForReply);
        IDisposable Enter(CancellationToken cancellation) =>
            RunicInteractionInvocation.Enter(content, _name, connection.ClientKey, connection.Key, descriptor.Name, cancellation);
        var admission = descriptor.ExecuteStreamAsync is { } executeStream
            ? content.Operations.AcceptUnlessRefused(operation, refusal, descriptor.CreateStream!(),
                (execution, cancellation) => ObserveOperationAsync(_logger, descriptor.Name, _name, descriptor.EncodeFailure, () =>
                {
                    using var invocation = Enter(cancellation);
                    return executeStream(_vm, execution, cancellation, argument);
                }), retirement, terminalRevision)
            : content.Operations.AcceptUnlessRefused(operation, refusal,
                cancellation => ObserveOperationAsync(_logger, descriptor.Name, _name, descriptor.EncodeFailure, () =>
                {
                    using var invocation = Enter(cancellation);
                    return InvokeCommandAsync(descriptor, argument, cancellation);
                }), retirement, terminalRevision);
        return admission with { Member = operation.Member };
    }

    // Decodes a start's input; returns invalid-input when it does not decode, or null with the decoded value.
    private static string? TryDecodeInput(CommandDescriptor<T> descriptor, JsonElement? input, out object? value)
    {
        value = null;
        if (input is not { } element) return null;
        try
        {
            value = descriptor.ReadInput?.Invoke(element);
            return null;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or OverflowException
            or InvalidOperationException)
        {
            return BridgeFailure.InvalidInput;
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
}
