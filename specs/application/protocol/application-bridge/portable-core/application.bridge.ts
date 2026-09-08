import { Schema } from "effect";
import {
  bridge,
  defineApplicationBridgeContract,
} from "../../../../../packages/web/application-bridge/dist/esm/index.js";

const RecursiveNode = Schema.suspend(() => Schema.Struct({
  value: Schema.String,
  next: Schema.optional(RecursiveNode),
})).annotate({ identifier: "RecursiveNode" });
const TextChoice = Schema.TaggedStruct("TextChoice", { value: Schema.String });
const NumericChoice = Schema.TaggedStruct("NumericChoice", { value: Schema.Int });
const PortableSnapshot = Schema.Struct({
  pair: Schema.Tuple([Schema.String, Schema.Int]),
  optionalPair: Schema.Tuple([Schema.String, Schema.optionalKey(Schema.Int)]),
  valuesByName: Schema.Record(Schema.String.pipe(Schema.check(Schema.isPattern(/^[a-z]+$/))), Schema.Int),
  choice: Schema.Union([TextChoice, NumericChoice]),
  uniqueChoices: Schema.Array(TextChoice).pipe(Schema.check(Schema.makeFilter(
    (items) => new Set(items.map((item) => JSON.stringify(item))).size === items.length,
    { toJsonSchema: () => ({ uniqueItems: true }) },
  ))),
  node: RecursiveNode,
  nullableNote: Schema.NullOr(Schema.String),
}).annotate({ identifier: "PortableSnapshot" });
const QuotaExceeded = Schema.TaggedStruct("QuotaExceeded", {
  limit: Schema.Int.pipe(Schema.check(Schema.isGreaterThan(0))),
});

export default defineApplicationBridgeContract({
  protocol: { identity: "runic.artifex.portable-core", version: 1 },
  csharp: { namespace: "Runic.Application.PortableCore.Contract", contractName: "PortableCore" },
  snapshot: PortableSnapshot,
  commands: [],
  events: [],
  errors: [QuotaExceeded],
});
