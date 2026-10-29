using System.Text;
using System.Text.Json;
using static TypeScriptModuleBindings;

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
    // Type arguments of the handle and the codec.
    internal string TypeArguments => FailureType is null ? ResultType : $"{ResultType}, {FailureType}";
    // The member name of the start, status and watch requests.
    internal string Member => char.ToLowerInvariant(Name[0]) + Name[1..];
}

internal static class OperationTypeScriptEmitter
{
    internal static void AppendDefinitions(StringBuilder ts, IEnumerable<OperationTypeScriptPlan> plans, string shortName)
    {
        var operations = plans.ToArray();
        foreach (var operation in operations)
        {
            var handle = Ref(operation.IsStream ? "BridgeStreamOperation" : "BridgeOperation");
            ts.AppendLine($"export interface {shortName}{operation.Name}Operation extends {handle}<{operation.TypeArguments}> {{}}");
        }
        if (operations.Length > 0) ts.AppendLine();
    }

    // Emits one module-level codec per operation, which its start and recover methods share.
    internal static void AppendCodecs(StringBuilder ts, IEnumerable<OperationTypeScriptPlan> plans)
    {
        foreach (var operation in plans)
        {
            var failure = operation.DecodeFailureExpression is null ? ""
                : $", decodeFailure: value => {BridgeTypeGraph.ArrowBody(operation.DecodeFailureExpression)}";
            ts.AppendLine($"const {Ref(operation.Member + "Operation")}: {Ref("BridgeOperationCodec")}<{operation.TypeArguments}> = {{ decode: value => {BridgeTypeGraph.ArrowBody(operation.DecodeResultExpression)}{failure}{(operation.IsStream ? ", stream: true" : "")} }};");
        }
    }

    // The start and recover methods of the client. An operation's id is `options.requestId`, or a random UUID.
    internal static void AppendClientMethods(StringBuilder ts, IEnumerable<OperationTypeScriptPlan> plans)
    {
        foreach (var operation in plans)
        {
            var parameter = operation.HasInput ? $"{operation.InputName}: {operation.InputType}, " : string.Empty;
            var input = operation.HasInput ? operation.EncodeInputExpression : "undefined";
            var member = JsonSerializer.Serialize(operation.Member);
            ts.AppendLine($"    start{operation.Name}({parameter}options?: {Ref("BridgeOperationStartOptions")}) {{ return view.start({member}, options?.requestId ?? globalThis.crypto.randomUUID(), {input}, {Ref(operation.Member + "Operation")}); }},");
            ts.AppendLine($"    recover{operation.Name}(requestId: string) {{ return view.recover({member}, requestId, {Ref(operation.Member + "Operation")}); }},");
        }
    }
}
