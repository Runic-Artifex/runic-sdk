using System.Text.Json.Nodes;
using Runic.Application.Views.Generated;

namespace Runic.Application.Testing.Tests.Conformance;

// The operations and the interaction of the notes test View: slowImport, confirmClear, the progress and finish
// mutations, the provided ids and the confirm interaction. This file belongs to P5 of the protocol-4 plan; these are
// minimal bodies that keep the notes View's state members.
#pragma warning disable CA1822 // P5 replaces these minimal bodies with ones that use the model.
internal sealed partial class NotesViewModel
{
    private TaskCompletionSource<int>? _import;

    private partial Task<int> SlowImportAsync(SlowImportInput input, CancellationToken token)
    {
        _import = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _import.Task.WaitAsync(token);
    }

    private partial Task ConfirmClearAsync(CancellationToken token)
    {
        Rows.ResetTo([]);
        return Task.CompletedTask;
    }

    partial void OnProgress(JsonObject mutation) { }

    partial void OnFinish(JsonObject mutation)
    {
        var rows = mutation["rows"] is JsonArray labels ? labels.Select(label => label!.GetValue<string>()).ToArray() : [];
        foreach (var label in rows) Rows.Add(new((Rows.Count == 0 ? 0 : Rows.Max(row => row.Id)) + 1, label));
        _import?.TrySetResult(rows.Length);
    }

    private partial JsonNode? ProvideOperationId(string id) => null;

    internal static partial BridgeInteractionDescriptor<NotesViewModel>[]? CreateInteractions() => null;
}
#pragma warning restore CA1822
