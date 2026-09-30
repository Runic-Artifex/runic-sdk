using System.Text;

// Checked receipts contain the same wire values as state and operations. Decode
// them before exposing a typed receipt, including conflicts and committed errors.
internal static class CheckedWriteTypeScriptEmitter
{
    internal static void AppendRuntime(StringBuilder source) => source.AppendLine("""
  async function invokeFieldWrite<T>(name: string, payload: string, decode: (value: unknown) => T): Promise<FieldWriteReceipt<T>> {
    if (lease.disposed || !isLive() || !bridge.isConnected()) throw new BridgeError("disconnected", "The Bridge is disconnected.");
    let json: string;
    try { json = await bridge.call(name, payload); }
    catch { throw new BridgeError(bridge.isConnected() ? "failed" : "disconnected", "The Bridge call could not complete."); }
    if (lease.disposed || !isLive()) throw new BridgeError("disconnected", "This view was disposed. Reconnect for the current state.");
    let reply: FieldWriteReply;
    try { reply = bridgeWire.object(JSON.parse(json), value => value) as unknown as FieldWriteReply; }
    catch { throw new BridgeError("failed", "The Bridge returned an invalid response."); }
    if (reply.state !== null) shared.accept(reply.state);
    if (!reply.ok) throw new BridgeError(reply.error?.kind ?? "failed", reply.error?.message ?? "The checked write failed.");
    try {
      const receipt = bridgeWire.object(reply.receipt, value => value);
      const baseline = (value: unknown): FieldBaseline<T> => bridgeWire.object(value, field => ({
        value: decode(field["value"]), version: bridgeWire.integer(field["version"], 0, Number.MAX_SAFE_INTEGER),
      }));
      switch (receipt["kind"]) {
        case "applied": return { kind: "applied", snapshot: baseline(receipt["snapshot"]),
          ...(receipt["validation"] === undefined ? {} : { validation: bridgeWire.string(receipt["validation"]) }) };
        case "committed-with-error": return { kind: "committed-with-error", snapshot: baseline(receipt["snapshot"]), message: bridgeWire.string(receipt["message"]) };
        case "conflict": return { kind: "conflict", incoming: baseline(receipt["incoming"]), message: bridgeWire.string(receipt["message"]) };
        case "rejected": return { kind: "rejected", message: bridgeWire.string(receipt["message"]) };
        default: throw new TypeError("Unknown checked write receipt kind.");
      }
    } catch { throw new BridgeError("failed", "The Bridge returned an invalid checked write receipt."); }
  }
""");
}
