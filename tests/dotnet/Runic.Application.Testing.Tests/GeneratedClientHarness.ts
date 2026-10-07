import { BridgeError } from "@runic-artifex/views";
import { runToolkitGeneratedClient, type ToolkitGeneratedClientTranscript } from "./ToolkitGeneratedClientHarness.ts";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

type Transcript = {
  readonly dtoInteraction: { readonly snapshot: string; readonly request: string; readonly output: string };
  readonly dtoList: {
    readonly snapshot: string;
    readonly setRequest: string; readonly setReply: string;
    readonly writeRequest: string; readonly writeReply: string;
    readonly replaceRequest: string; readonly replaceReply: string;
  };
  readonly toolkitTyped: ToolkitGeneratedClientTranscript;
  readonly validationSnapshot: string;
  readonly dataShape: {
    readonly snapshot: string;
    readonly duration: string;
    readonly durationRequest: string;
    readonly durationReply: string;
    readonly amount: string;
    readonly amountRequest: string;
    readonly amountReply: string;
    readonly wholeRequest: string;
    readonly wholeReply: string;
    readonly exactIdWriteRequest: string;
    readonly exactIdWriteReply: string;
    readonly conflictRequest: string; readonly conflictReply: string;
    readonly failedRequest: string; readonly failedReply: string;
    readonly unionRequest: string; readonly unionReply: string;
  };
  readonly typedReactive: {
    readonly snapshot: string;
    readonly requestId: string;
    readonly startRequest: string;
    readonly admission: string;
    readonly waitRequest: string;
    readonly completion: string;
    readonly wrongMemberStatusRequest: string;
    readonly wrongMemberStatus: string;
  };
};

type Call = { readonly route: string; readonly args: readonly unknown[] };
type Bridge = { isConnected(): boolean; call(route: string, ...args: unknown[]): Promise<string> };

const [fixturePath, generatedDirectory] = Bun.argv.slice(2);
if (!fixturePath || !generatedDirectory) throw new Error("Usage: GeneratedClientHarness.ts <fixture.json> <generated-dir>");
const fixture = await Bun.file(fixturePath).json() as Transcript;
const calls: Call[] = [];
let failNextWait = false;

const host = globalThis as typeof globalThis & { window?: Record<string, unknown> };
host.window = host as unknown as Record<string, unknown>;
const bridge: Bridge = {
  isConnected: () => true,
  async call(route, ...args) {
    calls.push({ route, args });
    switch (route) {
      case "dataShapeSnapshot": return fixture.dataShape.snapshot;
      case "dataShapeSetDuration": expectArgs(route, args, fixture.dataShape.durationRequest); return fixture.dataShape.durationReply;
      case "dataShapeSetAmount": expectArgs(route, args, fixture.dataShape.amountRequest); return fixture.dataShape.amountReply;
      case "dataShapeSetWhole": expectArgs(route, args, fixture.dataShape.wholeRequest); return fixture.dataShape.wholeReply;
      case "dataShapeWritePayload": expectArgs(route, args, fixture.dataShape.unionRequest); return fixture.dataShape.unionReply;
      case "dataShapeWriteExactId": {
        const request = JSON.parse(String(args[0]));
        const pair = request.requestId === "generated-client-conflict" ? [fixture.dataShape.conflictRequest, fixture.dataShape.conflictReply]
          : request.requestId === "generated-client-post-apply" ? [fixture.dataShape.failedRequest, fixture.dataShape.failedReply]
          : [fixture.dataShape.exactIdWriteRequest, fixture.dataShape.exactIdWriteReply];
        expectArgs(route, args, pair[0]!); return pair[1]!;
      }
      case "typedReactiveSnapshot": return fixture.typedReactive.snapshot;
      case "typedReactiveStartSave": expectArgs(route, args, fixture.typedReactive.startRequest); return fixture.typedReactive.admission;
      case "__runicOperationWait":
        if (failNextWait) { failNextWait = false; throw new Error("The transport dropped the wait."); }
        expectArgs(route, args, fixture.typedReactive.waitRequest); return fixture.typedReactive.completion;
      case "__runicOperationStatus": expectArgs(route, args, fixture.typedReactive.wrongMemberStatusRequest); return fixture.typedReactive.wrongMemberStatus;
      default: throw new Error(`Unexpected generated-client route ${route}.`);
    }
  },
};
(host.window as Record<string, unknown>).__runicBridge = bridge;

const dataShape = await import(pathToFileURL(resolve(generatedDirectory, "dataShape.ts")).href);
const typedReactive = await import(pathToFileURL(resolve(generatedDirectory, "typedReactive.ts")).href);
const data = await dataShape.connectDataShape();

expect(data.snapshot["exact-id"] === 9007199254740993n, "Int64 state did not decode to bigint exactly.");
expect(data.snapshot.amount === "1234567890.123456789", "Decimal state changed representation.");
expect(data.snapshot.day === "2026-09-28" && data.snapshot.when.startsWith("2026-09-28T10:11:12"),
  "Date state changed representation.");
expect(data.snapshot.duration === "00:00:12.3456789", "TimeSpan state did not decode in c format.");
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

await expectThrows(() => data.setWhen("yesterday"), "Generated DateTime encode accepted a non-ISO value.");
expect(!calls.some(call => call.route === "dataShapeSetWhen"), "Generated DateTime encode sent a non-ISO value to .NET.");
await expectThrows(() => data.setDuration("24:00:00"), "Generated TimeSpan encode accepted an invalid hour.");
await expectThrows(() => data.setDuration("10675199.02:48:05.4775808"), "Generated TimeSpan encode accepted overflow.");
await data.setDuration(fixture.dataShape.duration);
await data.setAmount(fixture.dataShape.amount);
await data.setWhole({ source: "base", ["__proto__"]: "whole-from-client", "retry-after": 11 });
const appliedReceipt = await data.writeExactId(9007199254740992n, {
  requestId: "generated-client-exact-id",
  baseline: data.fieldBaseline("exact-id"),
});
expect(data.snapshot["exact-id"] === 9007199254740992n, "Checked bigint write did not accept the C# receipt state.");

expect(appliedReceipt.kind === "applied" && appliedReceipt.snapshot.value === 9007199254740992n,
  "Applied checked-write receipt did not decode its Int64 value.");
const conflictReceipt = await data.writeExactId(2n, { requestId: "generated-client-conflict",
  baseline: { version: JSON.parse(fixture.dataShape.conflictRequest).expectedVersion, value: 9007199254740993n } });
expect(conflictReceipt.kind === "conflict" && conflictReceipt.incoming.value === 9007199254740992n,
  "Conflict checked-write receipt did not decode its incoming Int64 value.");
const failedReceipt = await data.writeExactId(-1n, { requestId: "generated-client-post-apply", baseline: data.fieldBaseline("exact-id") });
expect(failedReceipt.kind === "committed-with-error" && failedReceipt.snapshot.value === -1n,
  "Committed-with-error receipt did not decode its post-setter Int64 value.");
const unionReceipt = await data.writePayload({ $case: "count", count: 42 }, {
  requestId: "generated-client-union", baseline: data.fieldBaseline("payload"),
});
expect(unionReceipt.kind === "applied" && unionReceipt.snapshot.value.$case === "count"
  && unionReceipt.snapshot.value.count === 42, "Checked-write union receipt did not decode.");

const typed = await typedReactive.connectTypedReactive();
const operation = await typed.startSaveWithRequestId(fixture.typedReactive.requestId,
  { documentId: "document-1", content: "from-client", expectedVersion: 7 });
expect(!calls.some(call => call.route === "__runicOperationWait"),
  "Operation admission eagerly started a terminal wait.");
failNextWait = true;
await expectThrows(() => operation.wait(), "A dropped operation wait did not reject.");
const completion = await operation.completion;
expect(calls.filter(call => call.route === "__runicOperationWait").length === 2,
  "A failed operation wait was cached instead of retried.");
expect(completion.kind === "succeeded" && completion.result?.documentId === "document-1"
  && completion.result.savedVersion === 8 && completion.result.contentLength === "from-client".length,
  "Typed operation result did not decode from the C# terminal payload.");
expect(calls.some(call => call.route === "typedReactiveStartSave")
  && calls.some(call => call.route === "__runicOperationWait"),
  "Generated typed operation client did not issue start and terminal-result calls.");
await expectThrows(() => typed.recoverLastResultWithRequestId(fixture.typedReactive.requestId),
  "A recovery request for a different operation member was accepted.");

// A throwing subscriber must not stop delivery to other subscribers, and an
// undecodable pushed state must neither escape into the .NET callback nor
// advance the accepted revision.
const reported: unknown[] = [];
const hostWithReporter = globalThis as { reportError?: (error: unknown) => void };
const previousReporter = hostWithReporter.reportError;
hostWithReporter.reportError = error => { reported.push(error); };
const pushState = (host.window as Record<string, unknown>)["__dataShapeChanged"] as (state: unknown) => unknown;
const wireState = JSON.parse(fixture.dataShape.snapshot).state as Record<string, unknown>;
// The revision is internal to the client: start above every reply it accepted.
expect(!("revision" in data.snapshot), "The public state exposed the internal revision.");
const baseRevision = Math.max(...[fixture.dataShape.snapshot, fixture.dataShape.durationReply, fixture.dataShape.amountReply,
  fixture.dataShape.wholeReply, fixture.dataShape.exactIdWriteReply, fixture.dataShape.conflictReply,
  fixture.dataShape.failedReply, fixture.dataShape.unionReply].map(json => Number(JSON.parse(json).state?.revision ?? 0)));
let initialDelivered = false;
const stopThrowing = data.subscribe(() => { if (initialDelivered) throw new Error("listener failure"); initialDelivered = true; });
const delivered: bigint[] = [];
const stopRecording = data.subscribe((state: { readonly "exact-id": bigint }) => { delivered.push(state["exact-id"]); });
pushState({ ...wireState, revision: baseRevision + 1, "exact-id": "11" });
expect(delivered.at(-1) === 11n && reported.length === 1,
  "A throwing subscriber stopped delivery to later subscribers or was not reported.");
pushState({ ...wireState, revision: baseRevision + 2, "exact-id": 12 });
expect(reported.length === 2 && data.snapshot["exact-id"] === 11n && delivered.at(-1) === 11n,
  "An undecodable pushed state replaced the current state or escaped the push callback.");
pushState({ ...wireState, revision: baseRevision + 1, "exact-id": "13" });
expect(data.snapshot["exact-id"] === 13n && delivered.at(-1) === 13n,
  "An undecodable pushed state advanced the accepted revision.");
stopThrowing();
stopRecording();
hostWithReporter.reportError = previousReporter;

data.dispose();
// A disposed client keeps its last state and accepts late subscribers, as
// framework stores (for example React's useSyncExternalStore) expect.
const lastState = data.snapshot;
expect(lastState["exact-id"] === 13n, "A disposed client lost its last snapshot.");
let lateDelivery: unknown;
const stopLate = data.subscribe((state: unknown) => { lateDelivery = state; });
stopLate();
expect(lateDelivery === lastState, "A subscriber after dispose did not receive the last snapshot.");
try {
  await data.setAmount("1");
  throw new Error("A disposed client accepted a call.");
} catch (error) {
  expect(error instanceof BridgeError && error.kind === "disconnected",
    "A disposed client did not reject with the shared BridgeError.");
}
typed.dispose();
await runToolkitGeneratedClient(fixture.toolkitTyped, generatedDirectory);
host.window!.__runicBridge = { isConnected: () => true, async call(route: string) {
  if (route !== "validationSnapshot") throw new Error(`Unexpected validation route ${route}`);
  return fixture.validationSnapshot;
} };
const validationModule = await import(pathToFileURL(resolve(generatedDirectory, "validation.ts")).href);
const validationView = await validationModule.connectValidation();
const validation = validationView.snapshot.validation;
expect(validation.hasErrors && validation.errors.some((error: {path: unknown[]; code?: string}) => error.path.length === 0 && error.code === "entity"),
  "Structured entity validation was not decoded.");
expect(validation.errors.some((error: {path: unknown[]}) => JSON.stringify(error.path) === '["model-profile","postal-code"]')
  && validation.errors.some((error: {path: unknown[]}) => JSON.stringify(error.path) === '["items",0,"label"]'),
  "Nested validation paths did not retain aliases and list indexes.");
validationView.dispose();

// Lists of DTOs as a setter, a checked write and a command argument encode as
// arrays of objects, exactly as the C# routes accepted them.
host.window!.__runicBridge = { isConnected: () => true, async call(route: string, ...args: unknown[]) {
  switch (route) {
    case "dtoListSnapshot": return fixture.dtoList.snapshot;
    case "dtoListSetEntries": expectArgs(route, args, fixture.dtoList.setRequest); return fixture.dtoList.setReply;
    case "dtoListWriteEntries": expectArgs(route, args, fixture.dtoList.writeRequest); return fixture.dtoList.writeReply;
    case "dtoListReplace": expectArgs(route, args, fixture.dtoList.replaceRequest); return fixture.dtoList.replaceReply;
    default: throw new Error(`Unexpected DTO list route ${route}`);
  }
} };
const dtoListModule = await import(pathToFileURL(resolve(generatedDirectory, "dtoList.ts")).href);
const dtoList = await dtoListModule.connectDtoList();
await dtoList.setEntries([{ name: "set", count: 2 }]);
const entriesReceipt = await dtoList.writeEntries([{ name: "written", count: 3 }, { name: "second", count: 4 }],
  { requestId: "generated-client-entries", baseline: dtoList.fieldBaseline("entries") });
expect(entriesReceipt.kind === "applied" && entriesReceipt.snapshot.value.length === 2, "A checked DTO list write was not applied.");
await dtoList.replace([{ name: "replaced", count: 5 }]);
expect(JSON.stringify(dtoList.snapshot.entries) === '[{"name":"replaced","count":5}]', "A DTO list command reply did not decode.");
dtoList.dispose();

// An interaction with a DTO output answers the .NET request with the object
// .NET accepted for it.
let interactionReply: { readonly kind: string; readonly output?: unknown } | undefined;
let releaseControlWait: ((reply: string) => void) | undefined;
let interactionDelivered = false;
let interactionMount = "";
host.window!.__runicBridge = { isConnected: () => true, async call(route: string, ...args: unknown[]) {
  switch (route) {
    case "dtoInteractionSnapshot": return fixture.dtoInteraction.snapshot;
    case "dtoInteractionMount": interactionMount = String(args[0]); return "ok";
    case "dtoInteractionUnmount": return "ok";
    case "__runicInteractionControl": return JSON.stringify({ kind: "ok" });
    case "__runicInteractionControlWait": return await new Promise<string>(resolve => { releaseControlWait = resolve; });
    case "__runicInteractionWait":
      if (interactionDelivered) return await new Promise<string>(() => {});
      interactionDelivered = true;
      return JSON.stringify({ ...JSON.parse(fixture.dtoInteraction.request), presentationId: interactionMount });
    case "__runicInteractionReply": interactionReply = JSON.parse(String(args[0])); return JSON.stringify({ kind: "ok" });
    default: throw new Error(`Unexpected DTO interaction route ${route}`);
  }
} };
const dtoInteractionModule = await import(pathToFileURL(resolve(generatedDirectory, "dtoInteraction.ts")).href);
const dtoInteraction = await dtoInteractionModule.connectDtoInteraction();
dtoInteraction.interactions.chooseEntry.handle((input: string) => ({ name: input === "Pick an entry" ? "picked" : input, count: 6 }));
for (let attempt = 0; interactionReply === undefined && attempt < 500; attempt++) await Bun.sleep(2);
expect(interactionReply?.kind === "answered" && JSON.stringify(interactionReply.output) === fixture.dtoInteraction.output,
  `A DTO interaction output was not encoded as .NET accepts it: ${JSON.stringify(interactionReply)}`);
dtoInteraction.dispose();
releaseControlWait?.(JSON.stringify({ kind: "disconnected" }));

function expectArgs(route: string, actual: readonly unknown[], expected: string): void {
  expect(actual.length === 1 && actual[0] === expected,
    `${route} did not preserve the generated C# request payload.`);
}

async function expectThrows(action: () => Promise<unknown>, message: string): Promise<void> {
  try { await action(); }
  catch { return; }
  throw new Error(message);
}

function expect(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}
