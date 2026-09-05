import { Schema } from "effect";
import {
  bridge,
  defineApplicationBridgeContract,
} from "../../../../web/application-bridge/dist/esm/index.js";

export const CounterSnapshot = Schema.Struct({
  count: Schema.Number.pipe(Schema.int(), Schema.between(-2147483648, 2147483647)),
  history: Schema.Array(Schema.Number.pipe(Schema.int(), Schema.between(-2147483648, 2147483647))),
  revision: Schema.Number.pipe(Schema.int(), Schema.between(0, Number.MAX_SAFE_INTEGER)),
}).annotations({ identifier: "CounterSnapshot" });

export const IncrementCounter = Schema.TaggedStruct("IncrementCounter", {
  step: Schema.Int.pipe(Schema.between(1, 10)),
});
export const ResetCounter = Schema.TaggedStruct("ResetCounter", {});
export const CounterIncremented = Schema.TaggedStruct("CounterIncremented", { snapshot: CounterSnapshot });
export const CounterReset = Schema.TaggedStruct("CounterReset", { snapshot: CounterSnapshot });
export const CounterChanged = Schema.TaggedStruct("CounterChanged", { snapshot: CounterSnapshot });

export default defineApplicationBridgeContract({
  protocol: { identity: "runic.artifex.counter", version: 1 },
  csharp: { namespace: "Runic.Application.Template.Contract", contractName: "Counter" },
  snapshot: CounterSnapshot,
  commands: [
    bridge.command(IncrementCounter, { receipt: CounterIncremented, advancesRevision: true }),
    bridge.command(ResetCounter, { receipt: CounterReset, advancesRevision: true }),
  ],
  events: [CounterChanged],
  errors: [],
});

export type CounterCommand =
  | typeof IncrementCounter.Type
  | typeof ResetCounter.Type;
export type CounterReceipt =
  | typeof CounterIncremented.Type
  | typeof CounterReset.Type;
export type CounterEvent = typeof CounterChanged.Type;
export type CounterSnapshot = typeof CounterSnapshot.Type;
