import { Schema } from "effect";
import {
  bridge,
  defineApplicationBridgeContract,
} from "@runic-artifex/application-bridge";

export const CounterSnapshot = Schema.Struct({
  count: Schema.Int,
  history: Schema.Array(Schema.Int),
  revision: Schema.Int.pipe(Schema.nonNegative()),
}).annotations({ identifier: "CounterSnapshot" });

export const InitializeApplication = Schema.TaggedStruct("InitializeApplication", {});
export const IncrementCounter = Schema.TaggedStruct("IncrementCounter", {
  step: Schema.Int.pipe(Schema.between(1, 10)),
});
export const ResetCounter = Schema.TaggedStruct("ResetCounter", {});
export const ApplicationInitialized = Schema.TaggedStruct("ApplicationInitialized", { snapshot: CounterSnapshot });
export const CounterIncremented = Schema.TaggedStruct("CounterIncremented", { snapshot: CounterSnapshot });
export const CounterReset = Schema.TaggedStruct("CounterReset", { snapshot: CounterSnapshot });
export const CounterChanged = Schema.TaggedStruct("CounterChanged", { snapshot: CounterSnapshot });

export default defineApplicationBridgeContract({
  protocol: { identity: "runic.artifex.counter", version: 1 },
  csharp: { namespace: "Runic.Application.Template.Contract", contractName: "Counter" },
  snapshot: CounterSnapshot,
  commands: [
    bridge.command(InitializeApplication, { receipt: ApplicationInitialized }),
    bridge.command(IncrementCounter, { receipt: CounterIncremented, advancesRevision: true }),
    bridge.command(ResetCounter, { receipt: CounterReset, advancesRevision: true }),
  ],
  events: [CounterChanged],
  errors: [],
  initialize: { _tag: "InitializeApplication" },
});

export type CounterCommand =
  | typeof InitializeApplication.Type
  | typeof IncrementCounter.Type
  | typeof ResetCounter.Type;
export type CounterReceipt =
  | typeof ApplicationInitialized.Type
  | typeof CounterIncremented.Type
  | typeof CounterReset.Type;
export type CounterEvent = typeof CounterChanged.Type;
export type CounterSnapshot = typeof CounterSnapshot.Type;

