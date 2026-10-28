using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests.Conformance;

// The operations and the interaction of the notes test View (specs/application/conformance/README.md, "notes"):
//
// - slowImport runs until a finish mutation and reports each progress mutation from its own flow
//   (RunicOperationProgress). A finish appends its rows and succeeds with their number, fails with the declared
//   failure sourceMissing, or ends cancelled, which the command honours only after cancel.
// - confirmClear asks the confirm interaction {question} and clears the rows, as a reset, when the output accepts.
//
// Each command ends inside one publication unit with its effects, so the frame that shows them also shows the command
// no longer executing, and the operation's terminal status carries that frame's revision.
internal sealed partial class NotesViewModel
{
    internal const string ConfirmContract = "conformance.notes.confirm.v1";
    internal const string ContractFingerprint = "conformance-notes";

    // The step the running slowImport waits for. Its continuation runs inline, in the mutate step's turn and unit.
    private TaskCompletionSource<JsonObject>? _step;
    private WindowContentSession? _session;
    private string? _route;

    private partial async Task<int> SlowImportAsync(SlowImportInput input, CancellationToken token)
    {
        while (true)
        {
            _step = new TaskCompletionSource<JsonObject>();
            var mutation = await _step.Task.ConfigureAwait(false);
            if (mutation["op"]!.GetValue<string>() == "progress")
            {
                RunicOperationProgress.Report(mutation["value"]!.GetValue<double>());
                continue;
            }
            _step = null;
            if (mutation["failure"] is not null)
                throw new RunicFailureException<NotesImportFailure>(new NotesImportFailure());
            if (mutation["outcome"]?.GetValue<string>() == "cancelled")
            {
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("slowImport ends cancelled only after cancel.");
            }
            var rows = mutation["rows"] is JsonArray labels ? labels.Select(label => label!.GetValue<string>()).ToArray() : [];
            var first = (Rows.Count == 0 ? 0 : Rows.Max(row => row.Id)) + 1;
            Rows.AddRange([.. rows.Select((label, index) => new ConformanceRow(first + index, label))]);
            return rows.Length;
        }
    }

    private partial async Task ConfirmClearAsync(CancellationToken token)
    {
        if (_session?.Interactions.TryRequest<string, bool>(_route!, "confirm", ConfirmContract, "Clear all rows?",
            EncodeQuestion, DecodeAnswer) is not { } answer) return;
        if (!await answer.ConfigureAwait(false)) return;
        // The rest of the command, and the end of its execution, run in one turn and publication unit.
        await new InUnit(_session.ModelContext!, this);
        Rows.ResetTo([]);
    }

    partial void OnProgress(JsonObject mutation) => Inline(() => _step?.TrySetResult(mutation));

    partial void OnFinish(JsonObject mutation) => Inline(() => _step?.TrySetResult(mutation));

    // Runs the awaiting command's continuations on this thread, in the current turn and unit. The model context's
    // thread has a synchronization context, which would otherwise make every ConfigureAwait(false) continuation of
    // the command's chain run later on the thread pool.
    private static void Inline(Action complete)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { complete(); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

#pragma warning disable CA1822 // The partial's declaration is an instance member.
    private partial JsonNode? ProvideOperationId(string id) => id switch
    {
        "contract" => $"{typeof(NotesViewModel).FullName}:{ContractFingerprint}",
        "confirmContract" => ConfirmContract,
        _ => null,
    };
#pragma warning restore CA1822

    internal static partial BridgeInteractionDescriptor<NotesViewModel>[]? CreateInteractions() => [new ConfirmInteraction()];

    private static string EncodeQuestion(string question) =>
        new JsonObject { ["question"] = question }.ToJsonString();

    private static bool DecodeAnswer(JsonElement output) =>
        output.ValueKind == JsonValueKind.Object && output.TryGetProperty("accepted", out var accepted)
            && accepted.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? accepted.GetBoolean()
            : throw new FormatException("Expected {accepted: boolean}.");

    // The confirm interaction: input {question: string}, output {accepted: boolean}.
    private sealed class ConfirmInteraction : BridgeInteractionDescriptor<NotesViewModel>
    {
        public override IDisposable Attach(WindowContentSession session, NotesViewModel model, string route)
        {
            model._session = session;
            model._route = route;
            return session.Interactions.Register<string, bool>(route, "confirm", ConfirmContract, EncodeQuestion, DecodeAnswer);
        }
    }

    // Resumes the awaiting method in a model turn, inside a publication unit of the model, so that what it does next
    // and what its callers do when it returns synchronously are one unit.
    private readonly struct InUnit(IRunicModelContext context, NotesViewModel model) : INotifyCompletion
    {
        public InUnit GetAwaiter() => this;
        public bool IsCompleted => false;
        public void GetResult() { }

        public void OnCompleted(Action continuation)
        {
            var (turns, owner) = (context, model);
            _ = Task.Run(() => RunicModelTurns.Run(turns, () =>
            {
                using (BridgeSnapshotBatch.Begin(owner)) Inline(continuation);
            }));
        }
    }
}
