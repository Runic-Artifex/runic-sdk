using System.Text;
using System.Text.Json;

// Operation emission is deliberately separate from Program's view/state
// emission. Typed command inputs, result decoders and stream cursors all use
// the same small plan; admission, recovery and idempotency handling live in
// the shared @runic-artifex/views runtime (`bridgeOperations`).
internal sealed record OperationTypeScriptPlan(
    string Name,
    string InputType,
    string ResultType,
    string DecodeResultExpression,
    string EncodeInputExpression,
    bool HasInput,
    bool IsStream = false,
    string InputName = "input",
    string? FailureType = null,
    string? DecodeFailureExpression = null)
{
    // Type arguments of the handle, and the trailing start/recover arguments
    // that pass the declared failure's decoder.
    internal string TypeArguments => FailureType is null ? ResultType : $"{ResultType}, {FailureType}";
    internal string FailureArguments => DecodeFailureExpression is null ? (IsStream ? ", true" : "")
        : $", {(IsStream ? "true" : "false")}, value => {BridgeTypeGraph.ArrowBody(DecodeFailureExpression)}";
}

internal static class OperationTypeScriptEmitter
{
    internal static void AppendDefinitions(StringBuilder ts, IEnumerable<OperationTypeScriptPlan> plans, string shortName)
    {
        var operations = plans.ToArray();
        foreach (var operation in operations)
        {
            var handle = operation.IsStream ? "BridgeStreamOperation" : "BridgeOperation";
            ts.AppendLine($"export interface {shortName}{operation.Name}Operation extends {handle}<{operation.TypeArguments}> {{}}");
        }
        if (operations.Length > 0) ts.AppendLine();
    }

    // Emits one starter per operation inside connectXAt. It depends only on
    // the `view` connection created there.
    internal static void AppendRuntime(StringBuilder ts, IEnumerable<OperationTypeScriptPlan> plans)
    {
        foreach (var operation in plans)
        {
            var parameter = operation.HasInput ? $", {operation.InputName}: {operation.InputType}" : string.Empty;
            var payload = operation.HasInput ? $"JSON.stringify({{ requestId, input: {operation.EncodeInputExpression} }})" : "requestId";
            ts.AppendLine($"  const start{operation.Name}Operation = (requestId: string{parameter}) => view.startOperation<{operation.TypeArguments}>({JsonSerializer.Serialize(operation.Name)}, requestId, () => {payload}, value => {BridgeTypeGraph.ArrowBody(operation.DecodeResultExpression)}{operation.FailureArguments});");
        }
    }

    internal static void AppendClientMethods(StringBuilder ts, IEnumerable<OperationTypeScriptPlan> plans)
    {
        foreach (var operation in plans)
        {
            var parameter = operation.HasInput ? $"{operation.InputName}: {operation.InputType}" : string.Empty;
            var argument = operation.HasInput ? $", {operation.InputName}" : string.Empty;
            ts.AppendLine($"    start{operation.Name}({parameter}) {{ return start{operation.Name}Operation(globalThis.crypto.randomUUID(){argument}); }},");
            ts.AppendLine($"    start{operation.Name}WithRequestId(requestId: string{(operation.HasInput ? $", {parameter}" : string.Empty)}) {{ return start{operation.Name}Operation(requestId{argument}); }},");
            ts.AppendLine($"    recover{operation.Name}WithRequestId(requestId: string) {{ return view.recoverOperation<{operation.TypeArguments}>({JsonSerializer.Serialize(operation.Name)}, requestId, value => {BridgeTypeGraph.ArrowBody(operation.DecodeResultExpression)}{operation.FailureArguments}); }},");
        }
    }
}
