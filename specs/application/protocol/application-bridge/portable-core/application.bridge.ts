import { Schema } from "effect";
import {
  bridge,
  defineApplicationBridgeContract,
} from "../../../web/packages/application-bridge/dist/esm/index.js";

const RecursiveNode = Schema.suspend(() => Schema.Struct({
  value: Schema.String,
  next: Schema.optional(RecursiveNode),
})).annotations({ identifier: "RecursiveNode" });
const TextChoice = Schema.TaggedStruct("TextChoice", { value: Schema.String });
const NumericChoice = Schema.TaggedStruct("NumericChoice", { value: Schema.Int });
const PortableSnapshot = Schema.Struct({
  pair: Schema.Tuple(Schema.String, Schema.Int),
  optionalPair: Schema.Tuple(Schema.String, Schema.optionalElement(Schema.Int)),
  valuesByName: Schema.Record({ key: Schema.String.pipe(Schema.pattern(/^[a-z]+$/)), value: Schema.Int }),
  choice: Schema.Union(TextChoice, NumericChoice),
  uniqueChoices: Schema.Array(TextChoice).pipe(Schema.filter(
    (items) => new Set(items.map((item) => JSON.stringify(item))).size === items.length,
    { jsonSchema: { uniqueItems: true } },
  )),
  node: RecursiveNode,
  nullableNote: Schema.NullOr(Schema.String),
}).annotations({ identifier: "PortableSnapshot" });
const InitializeApplication = Schema.TaggedStruct("InitializeApplication", {});
const ApplicationInitialized = Schema.TaggedStruct("ApplicationInitialized", { snapshot: PortableSnapshot });
const QuotaExceeded = Schema.TaggedStruct("QuotaExceeded", {
  limit: Schema.Int.pipe(Schema.positive()),
});

export default defineApplicationBridgeContract({
  protocol: { identity: "runic.artifex.portable-core", version: 1 },
  csharp: { namespace: "Runic.Application.PortableCore.Contract", contractName: "PortableCore" },
  snapshot: PortableSnapshot,
  commands: [bridge.command(InitializeApplication, { receipt: ApplicationInitialized })],
  events: [],
  errors: [QuotaExceeded],
  initialize: { _tag: "InitializeApplication" },
});
