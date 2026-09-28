import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

type Transcript = {
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
      case "dataShapeWriteExactId": expectArgs(route, args, fixture.dataShape.exactIdWriteRequest); return fixture.dataShape.exactIdWriteReply;
      case "typedReactiveSnapshot": return fixture.typedReactive.snapshot;
      case "typedReactiveStartSave": expectArgs(route, args, fixture.typedReactive.startRequest); return fixture.typedReactive.admission;
      case "__runicOperationWait": expectArgs(route, args, fixture.typedReactive.waitRequest); return fixture.typedReactive.completion;
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

await expectThrows(() => data.setDuration("24:00:00"), "Generated TimeSpan encode accepted an invalid hour.");
await expectThrows(() => data.setDuration("10675199.02:48:05.4775808"), "Generated TimeSpan encode accepted overflow.");
await data.setDuration(fixture.dataShape.duration);
await data.setAmount(fixture.dataShape.amount);
await data.setWhole({ source: "base", ["__proto__"]: "whole-from-client", "retry-after": 11 });
await data.writeExactId(9007199254740992n, {
  requestId: "generated-client-exact-id",
  baseline: data.fieldBaseline("exact-id"),
});
expect(data.snapshot["exact-id"] === 9007199254740992n, "Checked bigint write did not accept the C# receipt state.");

const typed = await typedReactive.connectTypedReactive();
const operation = await typed.startSaveWithRequestId(fixture.typedReactive.requestId,
  { documentId: "document-1", content: "from-client", expectedVersion: 7 });
expect(!calls.some(call => call.route === "__runicOperationWait"),
  "Operation admission eagerly started a terminal wait.");
const completion = await operation.completion;
expect(completion.kind === "succeeded" && completion.result?.documentId === "document-1"
  && completion.result.savedVersion === 8 && completion.result.contentLength === "from-client".length,
  "Typed operation result did not decode from the C# terminal payload.");
expect(calls.some(call => call.route === "typedReactiveStartSave")
  && calls.some(call => call.route === "__runicOperationWait"),
  "Generated typed operation client did not issue start and terminal-result calls.");
await expectThrows(() => typed.recoverLastResultWithRequestId(fixture.typedReactive.requestId),
  "A recovery request for a different operation member was accepted.");


data.dispose();
typed.dispose();

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
