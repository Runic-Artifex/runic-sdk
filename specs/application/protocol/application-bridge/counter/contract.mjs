import { Schema } from "effect";

const Revision = Schema.Int.pipe(Schema.nonNegative()).annotations({ identifier: "Revision" });
const CounterSnapshot = Schema.Struct({
  count: Schema.Int,
  history: Schema.Array(Schema.Int),
  revision: Revision,
});

const commands = [
  {
    tag: "InitializeApplication",
    schema: Schema.TaggedStruct("InitializeApplication", {}),
    receipt: "ApplicationInitialized",
    startsOperation: false,
    cancellable: false,
    advancesRevision: false,
  },
  {
    tag: "IncrementCounter",
    schema: Schema.TaggedStruct("IncrementCounter", { step: Schema.Int.pipe(Schema.between(1, 10)) }),
    receipt: "CounterIncremented",
    startsOperation: false,
    cancellable: false,
    advancesRevision: true,
  },
  {
    tag: "ResetCounter",
    schema: Schema.TaggedStruct("ResetCounter", {}),
    receipt: "CounterReset",
    startsOperation: false,
    cancellable: false,
    advancesRevision: true,
  },
];

const receipts = [
  { tag: "ApplicationInitialized", schema: Schema.TaggedStruct("ApplicationInitialized", { snapshot: CounterSnapshot }) },
  { tag: "CounterIncremented", schema: Schema.TaggedStruct("CounterIncremented", { snapshot: CounterSnapshot }) },
  { tag: "CounterReset", schema: Schema.TaggedStruct("CounterReset", { snapshot: CounterSnapshot }) },
];

const events = [
  { tag: "CounterChanged", schema: Schema.TaggedStruct("CounterChanged", { snapshot: CounterSnapshot }) },
];

const errors = [
  "TransportUnavailable",
  "TransportClosed",
  "ProtocolVersionMismatch",
  "ProtocolDecodeError",
  "CommandRejected",
  "StaleRevision",
  "OperationFailed",
  "OperationCancelled",
  "OperationTimedOut",
].map((tag) => ({
  tag,
  schema: Schema.TaggedStruct(tag, { message: Schema.String, retryable: Schema.Boolean }),
}));

export default {
  formatVersion: 1,
  protocol: { identity: "runic.artifex.counter", version: 1 },
  csharp: { namespace: "RunicToolkitStarter.Contract", contractName: "Counter" },
  limits: {
    maxFrameBytes: 262144,
    maxDepth: 32,
    maxStringBytes: 65536,
    maxCollectionItems: 4096,
    maxPendingCommands: 64,
  },
  schemas: { CounterSnapshot },
  commands,
  receipts,
  events,
  errors,
};
