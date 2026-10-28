// Runs generated clients against the real generated bridges of GeneratedClientHarness.cs over Views protocol 4: the
// generated decoders must read what .NET writes, and the generated encoders must write what .NET accepts.
import { BridgeError } from "@runic-artifex/views";
import { runToolkitGeneratedClient } from "./ToolkitGeneratedClientHarness.ts";
import { runFailureGeneratedClient } from "./FailureGeneratedClientHarness.ts";
import { installLiveBridge } from "./GeneratedLiveBridge.ts";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const [generatedDirectory] = Bun.argv.slice(2);
if (!generatedDirectory) throw new Error("Usage: GeneratedClientHarness.ts <generated-dir>");
const live = installLiveBridge();
const load = (name: string) => import(pathToFileURL(resolve(generatedDirectory, `${name}.ts`)).href);
const sent = (kind: string, member: string) => live.requests.some(request => request["kind"] === kind && request["member"] === member);

const dataShape = await load("dataShape");
const data = await dataShape.connectDataShape();
expect(data.snapshot["exact-id"] === 9007199254740993n, "Int64 state did not decode to bigint exactly.");
expect(data.snapshot.amount === "1234567890.123456789", "Decimal state changed representation.");
expect(data.snapshot.day === "2026-09-28" && data.snapshot.when === "2026-09-28T10:11:12", "Date state changed representation.");
expect(data.snapshot.duration === "PT12.3456789S", "TimeSpan state did not decode as an ISO 8601 duration.");
expect(data.snapshot.optionalItem === null && data.snapshot.items[0]?.["retry-after"] === 3
  && data.snapshot.items[0]?.["__proto__"] === "first"
  && data.snapshot.optionalItems[0] === null && data.snapshot.optionalItems[1]?.["__proto__"] === "optional-item",
  "Nullable or collection DTO state did not decode.");
expect(data.snapshot.payload.$case === "text" && data.snapshot.payload.text === "payload", "Union state did not decode.");
expect(data.snapshot.money === "12.50", "Custom codec state did not decode.");
expect(Object.getPrototypeOf(data.snapshot.lookup) === null
  && Object.hasOwn(data.snapshot.lookup, "__proto__")
  && Object.hasOwn(data.snapshot.lookup["__proto__"]!, "__proto__")
  && data.snapshot.lookup["__proto__"]?.["__proto__"] === "prototype-safe",
  "A __proto__ dictionary entry changed the generated record prototype.");
expect(!("revision" in data.snapshot), "The public state exposed the revision.");

// Encoders validate before anything is sent.
await expectThrows(() => data.setWhen("yesterday"), "Generated DateTime encode accepted a non-ISO value.");
expect(!sent("set", "when"), "Generated DateTime encode sent a non-ISO value to .NET.");
await expectThrows(() => data.setDuration("1.02:03:04"), "Generated TimeSpan encode accepted the .NET format.");
await expectThrows(() => data.setDuration("P10675199DT2H48M5.4775808S"), "Generated TimeSpan encode accepted overflow.");
expect(!sent("set", "duration"), "Generated TimeSpan encode sent an invalid value to .NET.");

// Setters send edits that .NET decodes; the state at the reply's revision shows what .NET applied.
await data.setDuration("-P1DT2H3M4.5000007S");
expect(data.snapshot.duration === "-P1DT2H3M4.5000007S", "A duration setter did not reach .NET as sent.");
await data.setAmount("99.2500");
expect(data.snapshot.amount === "99.2500", "A decimal setter lost its scale.");
await data.setWhole({ source: "base", ["__proto__"]: "whole-from-client", "retry-after": 11 });
expect(data.snapshot.whole["__proto__"] === "whole-from-client" && data.snapshot.whole["retry-after"] === 11,
  "A DTO setter with special member names did not reach .NET.");
await live.step("dataShape.expectSet");

// Checked writes: applied, conflict, committed-with-error and a union value.
const before = data.fieldBaseline("exact-id");
const applied = await data.writeExactId(9007199254740992n, { requestId: "generated-client-exact-id", baseline: before });
expect(applied.kind === "applied" && applied.snapshot.value === 9007199254740992n && data.snapshot["exact-id"] === 9007199254740992n,
  "An applied checked write did not decode its Int64 receipt.");
const conflict = await data.writeExactId(2n, { requestId: "generated-client-conflict", baseline: before });
expect(conflict.kind === "conflict" && conflict.incoming.value === 9007199254740992n,
  "A conflict receipt did not decode its incoming Int64 value.");
const failed = await data.writeExactId(-1n, { requestId: "generated-client-post-apply", baseline: data.fieldBaseline("exact-id") });
expect(failed.kind === "committed-with-error" && failed.snapshot.value === -1n,
  "A committed-with-error receipt did not decode its post-setter Int64 value.");
const union = await data.writePayload({ $case: "count", count: 42 }, {
  requestId: "generated-client-union", baseline: data.fieldBaseline("payload"),
});
expect(union.kind === "applied" && union.snapshot.value.$case === "count" && union.snapshot.value.count === 42,
  "A checked union write did not decode.");

// Operations: the typed result decodes from .NET's terminal status, and another member's id is unknown.
const typedReactive = await load("typedReactive");
const typed = await typedReactive.connectTypedReactive();
const operation = await typed.startSave({ documentId: "document-1", content: "from-client", expectedVersion: 7 },
  { requestId: "generated-client-save" });
await live.step("typedReactive.releaseSave");
const completion = await operation.completion;
expect(completion.kind === "succeeded" && completion.result?.documentId === "document-1"
  && completion.result.savedVersion === 8 && completion.result.contentLength === "from-client".length,
  "A typed operation result did not decode from the C# terminal status.");
await expectThrows(() => typed.recoverLastResult("generated-client-save"),
  "A recovery of an operation id started for another member was accepted.");
typed.dispose();

// A disposed client keeps its last state and accepts late subscribers, as framework stores expect.
data.dispose();
const lastState = data.snapshot;
let lateDelivery: unknown;
data.subscribe((state: unknown) => { lateDelivery = state; })();
expect(lateDelivery === lastState, "A subscriber after dispose did not receive the last snapshot.");
try {
  await data.setAmount("1");
  throw new Error("A disposed client accepted a call.");
} catch (error) {
  // The Bridge is still connected: only this View was released.
  expect(error instanceof BridgeError && error.kind === "released", "A disposed client did not reject with released.");
}

await runToolkitGeneratedClient(generatedDirectory);
await runFailureGeneratedClient(live, generatedDirectory);

const validationView = await (await load("validation")).connectValidation();
const validation = validationView.snapshot.validation;
expect(validation.hasErrors && validation.errors.some((error: { path: unknown[]; code?: string }) => error.path.length === 0 && error.code === "entity"),
  "Structured entity validation was not decoded.");
expect(validation.errors.some((error: { path: unknown[] }) => JSON.stringify(error.path) === '["model-profile","postal-code"]')
  && validation.errors.some((error: { path: unknown[] }) => JSON.stringify(error.path) === '["items",0,"label"]'),
  "Nested validation paths did not retain aliases and list indexes.");
validationView.dispose();

// Lists of DTOs as a setter value, a checked write and a command input encode as arrays of objects.
const dtoList = await (await load("dtoList")).connectDtoList();
await dtoList.setEntries([{ name: "set", count: 2 }]);
const entries = await dtoList.writeEntries([{ name: "written", count: 3 }, { name: "second", count: 4 }],
  { requestId: "generated-client-entries", baseline: dtoList.fieldBaseline("entries") });
expect(entries.kind === "applied" && entries.snapshot.value.length === 2, "A checked DTO list write was not applied.");
await dtoList.replace([{ name: "replaced", count: 5 }]);
expect(JSON.stringify(dtoList.snapshot.entries) === '[{"name":"replaced","count":5}]', "A DTO list command did not reach .NET.");
dtoList.dispose();
await live.step("dtoList.expectReplaced");

// An interaction handler passed to connect answers .NET with the DTO it accepts.
const dtoInteraction = await (await load("dtoInteraction")).connectDtoInteraction({
  handlers: { chooseEntry: (input: string) => ({ name: input === "Pick an entry" ? "picked" : input, count: 6 }) },
});
await dtoInteraction.pick();
expect(dtoInteraction.snapshot.picked?.name === "picked", "The interaction's DTO output did not reach the ViewModel.");
dtoInteraction.dispose();
await live.step("dtoInteraction.expectPicked");

await live.close();
console.log("GENERATED_CLIENT_OK");

async function expectThrows(action: () => Promise<unknown>, message: string): Promise<void> {
  try { await action(); }
  catch { return; }
  throw new Error(message);
}

function expect(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}
