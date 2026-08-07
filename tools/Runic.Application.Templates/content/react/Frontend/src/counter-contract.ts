import { Schema } from "effect";
import { defineApplicationContract } from "@runic-artifex/application-bridge";

export const CounterSnapshot = Schema.Struct({
  count: Schema.Int,
  history: Schema.Array(Schema.Int),
  revision: Schema.Int.pipe(Schema.nonNegative()),
});
export const CounterCommand = Schema.Union(
  Schema.TaggedStruct("InitializeApplication", {}),
  Schema.TaggedStruct("IncrementCounter", { step: Schema.Int.pipe(Schema.between(1, 10)) }),
  Schema.TaggedStruct("ResetCounter", {}),
);
export const CounterReceipt = Schema.Union(
  Schema.TaggedStruct("ApplicationInitialized", { snapshot: CounterSnapshot }),
  Schema.TaggedStruct("CounterIncremented", { snapshot: CounterSnapshot }),
  Schema.TaggedStruct("CounterReset", { snapshot: CounterSnapshot }),
);
export const CounterEvent = Schema.TaggedStruct("CounterChanged", { snapshot: CounterSnapshot });

export const CounterContract = defineApplicationContract({
  identity: "runic.artifex.counter",
  version: 1,
  command: CounterCommand,
  receipt: CounterReceipt,
  event: CounterEvent,
  snapshot: CounterSnapshot,
  initialize: { _tag: "InitializeApplication" } as const,
});

export type CounterCommand = typeof CounterCommand.Type;
export type CounterReceipt = typeof CounterReceipt.Type;
export type CounterEvent = typeof CounterEvent.Type;
export type CounterSnapshot = typeof CounterSnapshot.Type;
