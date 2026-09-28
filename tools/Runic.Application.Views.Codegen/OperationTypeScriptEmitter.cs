using System.Text;

// Operation emission is deliberately separate from Program's view/state
// emission. Typed command inputs, result decoders and stream cursors all use
// the same small plan, so adding a command kind does not require another copy
// of recovery and idempotency handling.
internal sealed record OperationTypeScriptPlan(
    string Name,
    string InputType,
    string ResultType,
    string DecodeResultExpression,
    string EncodeInputExpression,
    bool HasInput,
    bool IsStream = false);

internal static class OperationTypeScriptEmitter
{
    internal static void AppendDefinitions(StringBuilder ts, IEnumerable<OperationTypeScriptPlan> plans, string shortName)
    {
        var operations = plans.ToArray();
        ts.AppendLine("export type BridgeOperationStatusKind = \"running\" | \"succeeded\" | \"failed\" | \"cancelled\" | \"expired\" | \"unknown\";");
        ts.AppendLine("export type BridgeOperationDeliveryKind = \"result-too-large\" | \"result-encoding-failed\" | \"stream-overflow\";");
        ts.AppendLine("export interface BridgeOperationStatus<TResult = never> { readonly contract: string; readonly requestId: string; readonly kind: BridgeOperationStatusKind; readonly error?: { readonly kind: \"failed\"; readonly message: string }; readonly result?: TResult; readonly delivery?: { readonly kind: BridgeOperationDeliveryKind; readonly message: string }; readonly stream?: true; }");
        ts.AppendLine("export type BridgeOperationCancelKind = \"cancellation-requested\" | \"not-running\" | \"unknown\" | \"expired\";");
        ts.AppendLine("export interface BridgeOperationCancelResult { readonly contract: string; readonly requestId: string; readonly kind: BridgeOperationCancelKind; }");
        ts.AppendLine("export interface BridgeOperationStreamItem<TResult> { readonly sequence: number; readonly value: TResult; }");
        ts.AppendLine("export interface BridgeOperationStreamPage<TResult> { readonly contract: string; readonly requestId: string; readonly kind: BridgeOperationStatusKind; readonly cursor?: number; readonly completed?: boolean; readonly items?: readonly BridgeOperationStreamItem<TResult>[]; readonly delivery?: { readonly kind: BridgeOperationDeliveryKind; readonly message: string }; }");
        ts.AppendLine("export class BridgeOperationUncertainError extends Error { constructor(readonly contract: string, readonly requestId: string, message: string) { super(message); this.name = \"BridgeOperationUncertainError\"; } }");
        foreach (var operation in operations)
        {
            ts.AppendLine($"export interface {shortName}{operation.Name}Operation {{");
            ts.AppendLine("  readonly requestId: string;");
            ts.AppendLine($"  status(): Promise<BridgeOperationStatus<{operation.ResultType}>>;");
            ts.AppendLine($"  readonly completion: Promise<BridgeOperationStatus<{operation.ResultType}>>;");
            ts.AppendLine($"  wait(): Promise<BridgeOperationStatus<{operation.ResultType}>>;");
            ts.AppendLine("  cancel(): Promise<BridgeOperationCancelResult>;");
            if (operation.IsStream)
                ts.AppendLine($"  stream(cursor?: number): Promise<BridgeOperationStreamPage<{operation.ResultType}>>;");
            ts.AppendLine("}");
        }
    }

    // Emits the helpers inside connectXAt. They intentionally depend only on
    // the established bridge/runtime variables already created by Program:
    // route, bridge, lease, isLive and contractId.
    internal static void AppendRuntime(StringBuilder ts, IEnumerable<OperationTypeScriptPlan> plans, string shortName)
    {
        var operations = plans.ToArray();
        ts.AppendLine("  function parseOperationStatus<TResult>(json: string, requestId: string, decode: (value: unknown) => TResult): BridgeOperationStatus<TResult> {");
        ts.AppendLine("    let status: BridgeOperationStatus<TResult>; try { status = JSON.parse(json) as BridgeOperationStatus<TResult>; } catch { throw new BridgeError(\"failed\", \"The operation service returned invalid JSON.\"); }");
        ts.AppendLine("    if (status.contract !== contractId || status.requestId !== requestId || !([\"running\", \"succeeded\", \"failed\", \"cancelled\", \"expired\", \"unknown\"] as const).includes(status.kind)) throw new BridgeError(\"failed\", \"The operation service returned a mismatched status.\");");
        ts.AppendLine("    if (status.result !== undefined) status = { ...status, result: decode(status.result) }; return status;");
        ts.AppendLine("  }");
        ts.AppendLine("  async function operationStatus<TResult>(requestId: string, wait: boolean, decode: (value: unknown) => TResult): Promise<BridgeOperationStatus<TResult>> {");
        ts.AppendLine("    const identity = JSON.stringify({ contract: contractId, requestId }); let reply: string;");
        ts.AppendLine("    try { reply = await bridge.call(wait ? \"__runicOperationWait\" : \"__runicOperationStatus\", identity); } catch { throw new BridgeOperationUncertainError(contractId, requestId, \"The operation status could not be observed.\"); }");
        ts.AppendLine("    return parseOperationStatus(reply, requestId, decode);");
        ts.AppendLine("  }");
        ts.AppendLine("  async function operationCancel(requestId: string): Promise<BridgeOperationCancelResult> {");
        ts.AppendLine("    let reply: string; try { reply = await bridge.call(\"__runicOperationCancel\", JSON.stringify({ contract: contractId, requestId })); } catch { throw new BridgeOperationUncertainError(contractId, requestId, \"The cancellation request could not be observed.\"); }");
        ts.AppendLine("    let result: BridgeOperationCancelResult; try { result = JSON.parse(reply) as BridgeOperationCancelResult; } catch { throw new BridgeError(\"failed\", \"The cancellation service returned invalid JSON.\"); }");
        ts.AppendLine("    if (result.contract !== contractId || result.requestId !== requestId) throw new BridgeError(\"failed\", \"The cancellation service returned a mismatched result.\"); return result;");
        ts.AppendLine("  }");
        foreach (var operation in operations)
            AppendOperationRuntime(ts, operation, shortName);
    }

    internal static void AppendClientMethods(StringBuilder ts, IEnumerable<OperationTypeScriptPlan> plans)
    {
        foreach (var operation in plans)
        {
            var method = LowerFirst(operation.Name);
            var parameter = operation.HasInput ? $"input: {operation.InputType}" : string.Empty;
            var argument = operation.HasInput ? "input" : "undefined";
            ts.AppendLine($"    start{operation.Name}({parameter}) {{ return start{operation.Name}WithRequestId(globalThis.crypto.randomUUID(), {argument}); }},");
            ts.AppendLine($"    start{operation.Name}WithRequestId(requestId: string{(operation.HasInput ? $", input: {operation.InputType}" : string.Empty)}) {{ return start{operation.Name}WithRequestId(requestId, {argument}); }},");
            ts.AppendLine($"    recover{operation.Name}WithRequestId(requestId: string) {{ return recover{operation.Name}WithRequestId(requestId); }},");
        }
    }

    private static void AppendOperationRuntime(StringBuilder ts, OperationTypeScriptPlan operation, string shortName)
    {
        var method = LowerFirst(operation.Name);
        var decoder = operation.DecodeResultExpression;
        var inputParameter = operation.HasInput ? $", input: {operation.InputType}" : ", _input?: never";
        var inputValue = operation.HasInput ? operation.EncodeInputExpression : "undefined";
        var startPayload = operation.HasInput ? $"JSON.stringify({{ requestId, input: {inputValue} }})" : "requestId";
        ts.AppendLine($"  function {method}Operation(requestId: string, terminal?: BridgeOperationStatus<{operation.ResultType}>): {shortName}{operation.Name}Operation {{");
        ts.AppendLine($"    const completion: Promise<BridgeOperationStatus<{operation.ResultType}>> = terminal === undefined ? operationStatus(requestId, true, value => {decoder}) : Promise.resolve(terminal);");
        ts.AppendLine("    return { requestId, status: () => terminal === undefined ? operationStatus(requestId, false, value => " + decoder + ") : Promise.resolve(terminal), completion, wait: () => completion, cancel: () => operationCancel(requestId)," +
            (operation.IsStream ? $" stream: async (cursor = 0) => {{ let reply: string; try {{ reply = await bridge.call(\"__runicOperationStream\", JSON.stringify({{ contract: contractId, requestId, cursor }})); }} catch {{ throw new BridgeOperationUncertainError(contractId, requestId, \"The operation stream could not be observed.\"); }} const page = JSON.parse(reply) as BridgeOperationStreamPage<{operation.ResultType}>; if (page.contract !== contractId || page.requestId !== requestId) throw new BridgeError(\"failed\", \"The operation stream returned a mismatched identity.\"); return {{ ...page, items: page.items?.map(item => ({{ ...item, value: ((value: unknown) => {decoder})(item.value) }})) }}; }}," : string.Empty) + " };");
        ts.AppendLine("  }");
        ts.AppendLine($"  async function start{operation.Name}WithRequestId(requestId: string{inputParameter}): Promise<{shortName}{operation.Name}Operation> {{");
        ts.AppendLine("    if (requestId.length === 0) throw new RangeError(\"Operation requestId is required.\"); if (lease.disposed || !isLive() || !bridge.isConnected()) throw new BridgeError(\"disconnected\", \"The Bridge is disconnected.\");");
        ts.AppendLine("    let reply: string; try { reply = await bridge.call(`${route}Start" + operation.Name + "`, " + startPayload + "); } catch { const recovered = await operationStatus(requestId, false, value => " + decoder + "); if (recovered.kind === \"unknown\" || recovered.kind === \"expired\") throw new BridgeOperationUncertainError(contractId, requestId, \"The operation admission could not be recovered.\"); return " + method + "Operation(requestId, recovered.kind === \"running\" ? undefined : recovered); }");
        ts.AppendLine("    const admission = JSON.parse(reply) as { readonly kind?: string; readonly reason?: string; readonly terminal?: unknown }; if (admission.kind === \"accepted\" || admission.kind === \"duplicate\") { const terminal = admission.terminal === null || admission.terminal === undefined ? undefined : parseOperationStatus(JSON.stringify(admission.terminal), requestId, value => " + decoder + "); return " + method + "Operation(requestId, terminal); } throw new BridgeError(admission.kind === \"rejected\" ? \"rejected\" : \"failed\", admission.reason ?? \"The operation was not accepted.\");");
        ts.AppendLine("  }");
        ts.AppendLine($"  async function recover{operation.Name}WithRequestId(requestId: string): Promise<{shortName}{operation.Name}Operation> {{ const status = await operationStatus(requestId, false, value => {decoder}); if (status.kind === \"unknown\" || status.kind === \"expired\") throw new BridgeOperationUncertainError(contractId, requestId, \"The operation admission could not be recovered.\"); return {method}Operation(requestId, status.kind === \"running\" ? undefined : status); }}");
    }

    private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];
}
