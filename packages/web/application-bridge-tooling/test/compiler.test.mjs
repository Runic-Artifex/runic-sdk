import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
  ApplicationBridgeCompilerError,
  checkApplicationBridge,
  compileApplicationBridge,
  compareApplicationBridgeIr,
  generateApplicationBridge,
} from "../dist/esm/index.js";

const effect = fileURLToPath(import.meta.resolve("effect"));

test("generates deterministic IR and a schema-free frontend facade", async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-bridge-"));
  try {
    await writeFile(join(root, "application.bridge.ts"), contractSource(), "utf8");
    const options = {
      root,
      source: "application.bridge.ts",
      ir: "bridge.ir.json",
      facade: "application.bridge.generated.ts",
    };
    const first = await generateApplicationBridge(options);
    const second = await generateApplicationBridge(options);
    assert.equal(first.changed, true);
    assert.equal(second.changed, false);
    assert.equal(first.ir.wire.definitions["command:IncrementCounter"].kind, "object");
    assert.equal(first.ir.wire.definitions["type:CounterSnapshot"].kind, "object");
    assert.equal(first.ir.wire.initialize, "InitializeApplication");
    assert.deepEqual(first.ir.fingerprint.algorithm, "sha256");
    assert.deepEqual(first.ir.fingerprint.scope, "wire");
    assert.match(first.ir.fingerprint.value, /^[a-f0-9]{64}$/);
    assert.equal(first.ir.documentation["type:CounterSnapshot"], "The authoritative counter state.");
    const facade = await readFile(join(root, "application.bridge.generated.ts"), "utf8");
    assert.match(facade, /materializeApplicationBridgeContract/);
    assert.doesNotMatch(facade, /Schema\./);
    await checkApplicationBridge(options);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("rejects refinements without portable wire metadata and preserves last-good output", async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-bridge-"));
  const options = {
    root,
    source: "application.bridge.ts",
    ir: "bridge.ir.json",
    facade: "application.bridge.generated.ts",
  };
  try {
    await writeFile(join(root, "application.bridge.ts"), contractSource(), "utf8");
    await generateApplicationBridge(options);
    const before = await readFile(join(root, "bridge.ir.json"), "utf8");
    await writeFile(join(root, "application.bridge.ts"), contractSource("Schema.String.pipe(Schema.filter(() => true))"), "utf8");
    await assert.rejects(
      generateApplicationBridge(options),
      (error) => error instanceof ApplicationBridgeCompilerError && error.code === "RTKAB1004",
    );
    assert.equal(await readFile(join(root, "bridge.ir.json"), "utf8"), before);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("lowers the portable core, constraints, imported schemas, and named recursion", async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-bridge-"));
  try {
    await writeFile(join(root, "shared.ts"), `
import { Schema } from ${JSON.stringify(effect)};
export const Node = Schema.suspend(() => Schema.Struct({ value: Schema.String, next: Schema.optional(Node) }))
  .annotations({ identifier: "Node" });
export const Payload = Schema.Struct({
  bounded: Schema.Number.pipe(Schema.greaterThan(0), Schema.lessThanOrEqualTo(100), Schema.multipleOf(0.5)),
  text: Schema.String.pipe(Schema.minLength(2), Schema.maxLength(8), Schema.pattern(/^[a-z]+$/)),
  normalized: Schema.Trim,
  values: Schema.Array(Schema.Int).pipe(Schema.minItems(1), Schema.maxItems(3)),
  tuple: Schema.Tuple(Schema.String, Schema.optionalElement(Schema.Int)),
  dictionary: Schema.Record({ key: Schema.String.pipe(Schema.pattern(/^[a-z]+$/)), value: Schema.Boolean }),
  choice: Schema.Union(Schema.Literal("one", "two"), Schema.Null),
  node: Node
}).annotations({ identifier: "Payload" });
`, "utf8");
    await writeFile(join(root, "application.bridge.ts"), `
import { Schema } from ${JSON.stringify(effect)};
import { Payload } from "./shared.js";
const Initialize = Schema.TaggedStruct("InitializeApplication", {});
const Initialized = Schema.TaggedStruct("ApplicationInitialized", { payload: Payload });
export default { protocol:{identity:"runic.core",version:1}, csharp:{namespace:"Runic.Core",contractName:"Core"}, snapshot:Payload,
commands:[{schema:Initialize,receipt:Initialized,startsOperation:false,cancellable:false,advancesRevision:false}], events:[], errors:[], initialize:{_tag:"InitializeApplication"} };
`, "utf8");
    const result = await compileApplicationBridge({ root, source: "application.bridge.ts", ir: "bridge.ir.json", facade: "generated.ts" });
    assert.deepEqual(result.dependencies.map((path) => basename(path)), ["application.bridge.ts", "shared.ts"]);
    const payload = result.ir.wire.definitions["type:Payload"];
    assert.equal(payload.kind, "object");
    assert.deepEqual(payload.properties.bounded.type.constraints, { exclusiveMinimum: 0, maximum: 100, multipleOf: 0.5 });
    assert.deepEqual(payload.properties.text.type.constraints, { maxLength: 8, minLength: 2, pattern: "^[a-z]+$" });
    assert.deepEqual(payload.properties.normalized.type, { kind: "ref", name: "type:Trim" });
    assert.equal(result.ir.wire.definitions["type:Trim"].kind, "string");
    assert.deepEqual(payload.properties.values.type.constraints, { maxItems: 3, minItems: 1 });
    assert.equal(payload.properties.tuple.type.kind, "tuple");
    assert.equal(payload.properties.dictionary.type.kind, "record");
    assert.equal(payload.properties.choice.type.kind, "union");
    assert.equal(result.ir.wire.definitions["type:Node"].kind, "object");
    assert.deepEqual(result.ir.wire.definitions["type:Node"].properties.next.type, { kind: "ref", name: "type:Node" });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("fingerprints only canonical wire semantics", async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-bridge-"));
  try {
    await writeFile(join(root, "application.bridge.ts"), contractSource(), "utf8");
    const options = { root, source: "application.bridge.ts", ir: "bridge.ir.json", facade: "generated.ts" };
    const first = await compileApplicationBridge(options);
    await writeFile(join(root, "application.bridge.ts"), contractSource().replace("Runic.Test\", contractName: \"Counter", "Renamed.Namespace\", contractName: \"Renamed"), "utf8");
    const second = await compileApplicationBridge(options);
    assert.equal(second.ir.fingerprint.value, first.ir.fingerprint.value);
    assert.notDeepEqual(second.ir.csharp, first.ir.csharp);
    await writeFile(join(root, "application.bridge.ts"), contractSource().replace(
      'initialize: { _tag: "InitializeApplication" }',
      'initialize: { _tag: "IncrementCounter", step: 1 }',
    ), "utf8");
    const third = await compileApplicationBridge(options);
    assert.notEqual(third.ir.fingerprint.value, first.ir.fingerprint.value);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("classifies additions separately from changed or removed wire semantics", async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-bridge-"));
  try {
    await writeFile(join(root, "application.bridge.ts"), contractSource(), "utf8");
    const { ir } = await compileApplicationBridge({ root, source: "application.bridge.ts", ir: "bridge.ir.json", facade: "generated.ts" });
    const additive = {
      ...ir,
      fingerprint: { ...ir.fingerprint, value: "1".repeat(64) },
      wire: { ...ir.wire, events: [...ir.wire.events, "NewEvent"] },
    };
    assert.equal(compareApplicationBridgeIr(ir, additive).classification, "additive");

    const changed = {
      ...ir,
      fingerprint: { ...ir.fingerprint, value: "2".repeat(64) },
      wire: {
        ...ir.wire,
        commands: ir.wire.commands.map((command) => command.name === "IncrementCounter"
          ? { ...command, advancesRevision: false }
          : command),
      },
    };
    assert.equal(compareApplicationBridgeIr(ir, changed).classification, "breaking");

    const removed = {
      ...ir,
      fingerprint: { ...ir.fingerprint, value: "3".repeat(64) },
      wire: { ...ir.wire, errors: ir.wire.errors.slice(1) },
    };
    assert.equal(compareApplicationBridgeIr(ir, removed).classification, "breaking");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

for (const [name, expression] of [
  ["custom transformation", "Schema.transform(Schema.String, Schema.String, { decode: value => value, encode: value => value })"],
  ["standard transformation with extra wire validation", "Schema.DateFromString"],
  ["non-JSON values", "Schema.BigInt"],
  ["regular-expression flags", "Schema.String.pipe(Schema.pattern(/value/i))"],
  ["unsupported regular-expression features", "Schema.String.pipe(Schema.pattern(/(?=value)/))"],
]) {
  test(`rejects ${name}`, async () => {
    const root = await mkdtemp(join(tmpdir(), "runic-bridge-"));
    try {
      await writeFile(join(root, "application.bridge.ts"), contractSource(expression), "utf8");
      await assert.rejects(
        compileApplicationBridge({ root, source: "application.bridge.ts", ir: "bridge.ir.json", facade: "generated.ts" }),
        (error) => error instanceof ApplicationBridgeCompilerError && error.code === "RTKAB1004" && error.schemaPath?.includes("commands"),
      );
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });
}

test("rejects duplicate tags, untagged receipts, and anonymous recursion", async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-bridge-"));
  const options = { root, source: "application.bridge.ts", ir: "bridge.ir.json", facade: "generated.ts" };
  try {
    await writeFile(join(root, "application.bridge.ts"), contractSource().replace('Schema.TaggedStruct("IncrementCounter"', 'Schema.TaggedStruct("InitializeApplication"'), "utf8");
    await assert.rejects(compileApplicationBridge(options), (error) => error?.code === "RTKAB1005");
    await writeFile(join(root, "application.bridge.ts"), contractSource().replace(
      'Schema.TaggedStruct("CounterIncremented", { snapshot: CounterSnapshot })',
      "Schema.Struct({ snapshot: CounterSnapshot })",
    ), "utf8");
    await assert.rejects(compileApplicationBridge(options), (error) => error?.code === "RTKAB1004");
    await writeFile(join(root, "application.bridge.ts"), contractSource("Schema.suspend(() => Schema.Struct({ next: Schema.optional(Recursive) }))")
      .replace("const IncrementCounter =", "const Recursive = Schema.suspend(() => Schema.Struct({ next: Schema.optional(Recursive) }));\nconst IncrementCounter ="), "utf8");
    await assert.rejects(compileApplicationBridge(options), (error) => error?.code === "RTKAB1004");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

function contractSource(step = "Schema.Int.pipe(Schema.between(1, 10))") {
  return `
import { Schema } from ${JSON.stringify(effect)};
const CounterSnapshot = Schema.Struct({ count: Schema.Int }).annotations({ identifier: "CounterSnapshot", description: "The authoritative counter state." });
const InitializeApplication = Schema.TaggedStruct("InitializeApplication", {});
const ApplicationInitialized = Schema.TaggedStruct("ApplicationInitialized", { snapshot: CounterSnapshot });
const IncrementCounter = Schema.TaggedStruct("IncrementCounter", { step: ${step} });
const CounterIncremented = Schema.TaggedStruct("CounterIncremented", { snapshot: CounterSnapshot });
export default {
  protocol: { identity: "runic.test", version: 1 },
  csharp: { namespace: "Runic.Test", contractName: "Counter" },
  snapshot: CounterSnapshot,
  commands: [
    { schema: InitializeApplication, receipt: ApplicationInitialized, startsOperation: false, cancellable: false, advancesRevision: false },
    { schema: IncrementCounter, receipt: CounterIncremented, startsOperation: false, cancellable: false, advancesRevision: true }
  ],
  events: [], errors: [], initialize: { _tag: "InitializeApplication" }
};\n`;
}
