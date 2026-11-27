using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Generated;

namespace Runic.Application.Testing.Tests.Conformance;

// The retention View (specs/application/conformance/README.md, "Test Views"): operations whose terminal statuses the
// window retains ("Limits and window close"). Its only state member is canHold, which stays true.
//
// - produce takes a length and succeeds at once, before its start is answered, with a string of that many "x"
//   characters, whose encoding is length + 2 bytes, so a case sizes a result against the 256 KiB retention bound.
// - hold runs until a finish mutation of member hold, which ends every running hold succeeded without a result; it
//   ends cancelled when its cancellation is requested. A case uses it to end an operation after others it started later.
//
// Both are always available and announce nothing, so no case of this View receives a frame.
internal sealed class RetentionViewModel : ConformanceModel
{
    // The step every running hold waits for. Its continuations run inline, in the mutate step's turn.
    private TaskCompletionSource? _held;

    public ConformanceCommand ProduceCommand { get; } = new(_ => true, _ => { });

    public ConformanceCommand HoldCommand { get; } = new(_ => true, _ => { });

    internal static RetentionViewModel Create(JsonObject model) =>
        model.Count == 0 ? new RetentionViewModel()
            : throw new NotSupportedException($"The retention View has no members: {model.ToJsonString()}.");

    internal static string Produce(int length) => JsonSerializer.Serialize(new string('x', length));

    internal async Task HoldAsync(CancellationToken token)
    {
        _held ??= new TaskCompletionSource();
        await _held.Task.WaitAsync(token).ConfigureAwait(false);
    }

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        if (mutation["op"]?.GetValue<string>() != "finish" || mutation["member"]?.GetValue<string>() != "hold")
            throw new NotSupportedException($"The retention View has no mutation {mutation.ToJsonString()}.");
        var held = _held;
        _held = null;
        // Runs the holds' continuations on this thread, in the current turn: the model context's synchronization
        // context would otherwise make every ConfigureAwait(false) continuation run later on the thread pool.
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { held?.TrySetResult(); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    internal override JsonNode? Read(string member) =>
        throw new NotSupportedException($"The retention View has no member {member}.");

    // contract: the View's operation contract, which a start names.
    internal override JsonNode? Provide(string id) => id == "contract" ? ConformanceContracts.Contract(typeof(RetentionViewModel)) : null;
}

internal sealed class RetentionBridge(IBridgeTransport transport, RetentionViewModel vm, WindowContentSession content)
    : ViewModelBridge(transport, vm, typeof(RetentionViewModel), "retention",
        [new("canHold", ConformanceWire.Boolean<RetentionViewModel>(model => model.HoldCommand.CanExecute(null)))],
        [], [],
        [
            new CommandDescriptor("Produce", model => ((RetentionViewModel)model).ProduceCommand)
            {
                ReadInput = static value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var length) && length >= 0
                    ? length : throw new FormatException("Expected a non-negative int32."),
                EncodeArgument = static argument => JsonSerializer.Serialize((int)argument!),
                ExecuteResultAsync = static (_, _, argument) =>
                    Task.FromResult(BridgeOperationResult.Succeeded(RetentionViewModel.Produce((int)argument!))),
            },
            new CommandDescriptor("Hold", model => ((RetentionViewModel)model).HoldCommand)
            {
                ExecuteAsync = static (model, token, _) => ((RetentionViewModel)model).HoldAsync(token),
            },
        ],
        ConformanceContracts.Fingerprint(typeof(RetentionViewModel)), content);
