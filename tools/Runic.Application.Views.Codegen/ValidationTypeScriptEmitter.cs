using System.Text;

internal static class ValidationTypeScriptEmitter
{
    internal static void AppendRuntime(StringBuilder source) => source.AppendLine("""
export interface BridgeValidationMessage {
  readonly path: readonly (string | number)[];
  readonly message: string;
  readonly code?: string;
  readonly severity?: string;
}
export interface BridgeValidationState {
  readonly hasErrors: boolean;
  readonly truncated: boolean;
  readonly errors: readonly BridgeValidationMessage[];
}
function decodeBridgeValidation(value: unknown): BridgeValidationState {
  return bridgeWire.object(value, state => ({
    hasErrors: bridgeWire.boolean(state["hasErrors"]),
    truncated: bridgeWire.boolean(state["truncated"]),
    errors: bridgeWire.array(state["errors"], value => bridgeWire.object(value, error => ({
      path: bridgeWire.array(error["path"], part => typeof part === "string" ? part : bridgeWire.integer(part, 0, Number.MAX_SAFE_INTEGER)),
      message: bridgeWire.string(error["message"]),
      ...(error["code"] === undefined ? {} : { code: bridgeWire.string(error["code"]) }),
      ...(error["severity"] === undefined ? {} : { severity: bridgeWire.string(error["severity"]) }),
    }))),
  }));
}
""");
}
