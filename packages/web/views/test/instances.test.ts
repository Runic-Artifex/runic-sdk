// Runic instances (specs/application/README.md, "Runic instances"): two instances in one document share no state, no
// connection and no translations, and the page instance keeps the document-global contract of "Shared globals".
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import {
  appearance, commitAllFields, configureTranslatedText, createFieldController, createRunicInstance, createViewController,
  currentInstance, defaultInstance, onBridgeConnection, onBridgeDiagnostic, pageInstance, resolveText, textLocale, windowLocale,
  type BridgeConnectionState, type BridgeDiagnostic, type RunicBridgeClient, type RunicInstance, type TextReference,
  type TextTranslations,
} from "../dist/index.js";
import { connectView } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, type MockBridge, type MockView } from "../dist/mock.js";
// Internal (stripped from the declarations): what a component bundle (component.ts) sets as its copy's default instance.
const { detachedSlots, setDefaultSlots, slotsOf } = await import("../dist/scope.js") as unknown as {
  detachedSlots(): object;
  setDefaultSlots(resolve: (() => object) | undefined): void;
  slotsOf(instance: RunicInstance): object;
};

type Host = typeof globalThis & { __runicBridge?: RunicBridgeClient };
const host = globalThis as Host;
const runtimeKey = Symbol.for("runic.views.generated-client-runtime");

const disposals: (() => void)[] = [];
afterEach(() => {
  for (const step of disposals.splice(0).reverse()) step();
  delete host.__runicBridge;
});

interface Guest { readonly bridge: MockBridge; readonly counter: MockView; readonly instance: RunicInstance }

function guest(count = 0): Guest {
  const bridge = createMockBridge();
  const counter = bridge.view("counter", { state: { count } });
  const instance = createRunicInstance({ bridge });
  disposals.push(() => instance.dispose());
  return { bridge, counter, instance };
}

const counterMembers = { count: (value: unknown) => bridgeWire.integer(value, -2147483648, 2147483647) };
type Counter = { readonly count: number };
const connectCounter = () => connectView<Counter>({ contract: "Tests.Counter:fp-1", route: "counter", members: counterMembers });

// A Translations runtime that formats every reference as `{locale}:{key}`.
function translations(initial: string): TextTranslations {
  let locale = initial;
  return {
    resolve: (reference, language) => `${language}:${reference.key}`,
    getLocale: () => locale,
    setLocale(next) { locale = next; },
  };
}
const saved: TextReference = { version: 2, catalog: "notes", contractFingerprint: "sha256:1", key: "saved", arguments: {}, fallbackText: "Saved." };

test("the page instance is the default one and keeps its state on globalThis", async () => {
  const page = pageInstance();
  assert.equal(page.page, true);
  assert.equal(currentInstance(), page);
  assert.throws(() => page.dispose(), TypeError);
  const bridge = createMockBridge();
  bridge.view("counter", { state: { count: 3 } });
  host.__runicBridge = bridge;
  assert.equal(page.bridge, bridge);
  const client = await connectCounter();
  disposals.push(() => { client.dispose(); (host[runtimeKey as never] as { dispose(): void } | undefined)?.dispose(); });
  assert.equal(client.snapshot.count, 3);
  assert.equal((host as unknown as Record<symbol, { bridge: unknown }>)[runtimeKey]?.bridge, bridge,
    "The page runtime is not on the shared global key.");
});

test("two instances connect the same route to their own Bridges, with their own state", async () => {
  const a = guest(1), b = guest(20);
  assert.equal(a.instance.page, false);
  assert.equal(a.instance.bridge, a.bridge);
  const clientA = await a.instance.run(connectCounter);
  const clientB = await b.instance.run(connectCounter);
  disposals.push(() => { clientA.dispose(); clientB.dispose(); });
  assert.equal(clientA.snapshot.count, 1);
  assert.equal(clientB.snapshot.count, 20);

  a.counter.update({ count: 2 });
  await a.bridge.flush();
  assert.equal(clientA.snapshot.count, 2);
  assert.equal(clientB.snapshot.count, 20, "A push of one instance reached the other.");
  assert.equal(b.bridge.calls.filter(call => call.kind === "open").length, 1);
  assert.equal(a.bridge.calls.filter(call => call.kind === "open").length, 1);
  // Neither touched the page instance.
  assert.equal((host as unknown as Record<symbol, unknown>)[runtimeKey], undefined);
  assert.equal(currentInstance(), pageInstance(), "run did not restore the current instance.");
});

test("run nests and restores the current instance, also when the function throws", () => {
  const a = guest(), b = guest();
  a.instance.run(() => {
    assert.equal(currentInstance(), a.instance);
    b.instance.run(() => assert.equal(currentInstance(), b.instance));
    assert.equal(currentInstance(), a.instance);
    assert.throws(() => b.instance.run(() => { throw new Error("boom"); }), /boom/);
    assert.equal(currentInstance(), a.instance);
  });
  assert.equal(currentInstance(), pageInstance());
});

test("each instance has its own translations and window locale", async () => {
  const a = guest(), b = guest();
  disposals.push(a.instance.run(() => configureTranslatedText(translations("en"))));
  const clientA = await a.instance.run(connectCounter);
  const clientB = await b.instance.run(connectCounter);
  disposals.push(() => { clientA.dispose(); clientB.dispose(); });

  b.bridge.setWindowLocale("de");
  await b.bridge.flush();
  assert.equal(b.instance.run(windowLocale), "de");
  assert.equal(a.instance.run(windowLocale), null, "The window locale of one instance reached the other.");
  assert.equal(windowLocale(), null, "The window locale of an instance reached the page.");

  assert.equal(a.instance.run(() => resolveText(saved)), "en:saved");
  // B has no Translations runtime: its references show their fallback text in its window's locale.
  assert.equal(b.instance.run(() => resolveText(saved)), "Saved.");
  assert.equal(b.instance.run(textLocale), "de");
  assert.equal(resolveText(saved), "Saved.", "The page resolved with an instance's Translations runtime.");

  a.bridge.setWindowLocale("fr");
  await a.bridge.flush();
  assert.equal(a.instance.run(() => resolveText(saved)), "fr:saved");
  assert.equal(b.instance.run(textLocale), "de");
});

test("each instance reports its own connection state", async () => {
  const a = guest(), b = guest();
  const states = { a: [] as BridgeConnectionState[], b: [] as BridgeConnectionState[], page: [] as BridgeConnectionState[] };
  disposals.push(a.instance.run(() => onBridgeConnection(state => states.a.push(state))));
  disposals.push(b.instance.run(() => onBridgeConnection(state => states.b.push(state))));
  disposals.push(onBridgeConnection(state => states.page.push(state), { lostAfter: 0 }));
  assert.deepEqual(states.a, ["connected"]);
  assert.deepEqual(states.b, ["connected"]);
  b.bridge.disconnect();
  assert.deepEqual(states.a, ["connected"]);
  assert.deepEqual(states.b, ["connected", "reconnecting"]);
  b.bridge.reconnect();
  assert.deepEqual(states.b, ["connected", "reconnecting", "connected"]);
  // The page has no Bridge: neither instance's connects it.
  assert.deepEqual(states.page, ["lost"]);
});

test("each instance reports its own diagnostics", async () => {
  const a = guest(), b = guest();
  const seen = { a: [] as BridgeDiagnostic[], b: [] as BridgeDiagnostic[], page: [] as BridgeDiagnostic[] };
  disposals.push(a.instance.run(() => onBridgeDiagnostic(diagnostic => seen.a.push(diagnostic))));
  disposals.push(b.instance.run(() => onBridgeDiagnostic(diagnostic => seen.b.push(diagnostic))));
  disposals.push(onBridgeDiagnostic(diagnostic => seen.page.push(diagnostic)));
  // The runtime also hands the error to the page's reportError, which would fail the test runner.
  const page = globalThis as { reportError?: unknown };
  const report = page.reportError;
  page.reportError = () => {};
  disposals.push(() => { page.reportError = report; });
  const client = await a.instance.run(connectCounter);
  disposals.push(() => client.dispose());
  // A push the client cannot read is reported later, from the instance's runtime, outside `run`.
  a.counter.push({ kind: "state", revision: 9, state: { count: "not a number" } });
  await a.bridge.flush();
  assert.ok(seen.a.some(diagnostic => diagnostic.route === "counter"), JSON.stringify(seen.a));
  assert.deepEqual(seen.b, []);
  assert.deepEqual(seen.page, []);
});

test("a view controller connects in its instance, and kept clients stay in it", async () => {
  const a = guest(1), b = guest(2);
  const connector = { connect: connectCounter };
  const controllerA = a.instance.run(() => createViewController<Awaited<ReturnType<typeof connectCounter>>>({ keepAlive: 1_000 }));
  controllerA.setSource(connector);
  await a.bridge.flush();
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(controllerA.current.state?.count, 1);
  // Released into A's keep-alive pool; B's controller connects its own client for the same connector.
  controllerA.release();
  const controllerB = b.instance.run(() => createViewController<Awaited<ReturnType<typeof connectCounter>>>());
  controllerB.setSource(connector);
  await b.bridge.flush();
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(controllerB.current.state?.count, 2, "An instance adopted a client another instance kept.");
  // A's next controller adopts A's kept client without opening the route again.
  const opened = a.bridge.calls.filter(call => call.kind === "open").length;
  const again = a.instance.run(() => createViewController<Awaited<ReturnType<typeof connectCounter>>>());
  again.setSource(connector);
  assert.equal(again.current.state?.count, 1);
  assert.equal(a.bridge.calls.filter(call => call.kind === "open").length, opened);
  disposals.push(() => { controllerA.dispose(); controllerB.dispose(); again.dispose(); });
});

test("commitAllFields commits the drafts of the current instance only", async () => {
  const a = guest(), b = guest();
  const written: string[] = [];
  const field = (name: string) => ({
    kind: "number" as const, read: (state: Counter) => state.count,
    write: async (_client: unknown, value: number) => { written.push(`${name}=${value}`); },
  });
  const fieldA = a.instance.run(() => createFieldController(field("a"), { commit: "pause", delay: 60_000 }));
  const fieldB = b.instance.run(() => createFieldController(field("b"), { commit: "pause", delay: 60_000 }));
  disposals.push(() => { fieldA.dispose(); fieldB.dispose(); });
  fieldA.setSource({ state: { count: 0 }, client: {} });
  fieldB.setSource({ state: { count: 0 }, client: {} });
  fieldA.input(1);
  fieldB.input(2);
  await commitAllFields();
  assert.deepEqual(written, [], "The page committed the drafts of an instance.");
  await a.instance.run(commitAllFields);
  assert.deepEqual(written, ["a=1"]);
});

test("each instance has its own appearance", async () => {
  const a = guest(), b = guest();
  const clientA = await a.instance.run(connectCounter);
  const clientB = await b.instance.run(connectCounter);
  disposals.push(() => { clientA.dispose(); clientB.dispose(); });
  a.bridge.setAppearance({ accentColor: "#112233", textScale: 1.5 });
  await a.bridge.flush();
  assert.equal(a.instance.run(appearance).accentColor, "#112233");
  assert.equal(b.instance.run(appearance).accentColor, null);
  assert.equal(appearance().accentColor, null);
});

test("a disposed instance releases its Views and refuses new ones; the other keeps working", async () => {
  const a = guest(1), b = guest(2);
  const clientA = await a.instance.run(connectCounter);
  const clientB = await b.instance.run(connectCounter);
  disposals.push(() => clientB.dispose());
  a.instance.dispose();
  a.instance.dispose();
  await assert.rejects(a.instance.run(connectCounter), { name: "BridgeError", kind: "unavailable" });
  await assert.rejects(clientA.command("increment"), { name: "BridgeError", kind: "disconnected" });
  const opened = a.bridge.calls.length;
  a.counter.update({ count: 5 });
  await a.bridge.flush();
  assert.equal(clientA.snapshot.count, 1, "A disposed instance still received pushes.");
  assert.equal(a.bridge.calls.length, opened);
  b.counter.update({ count: 3 });
  await b.bridge.flush();
  assert.equal(clientB.snapshot.count, 3);
});

test("createRunicInstance needs a Bridge", () => {
  assert.throws(() => createRunicInstance({} as never), TypeError);
  assert.throws(() => createRunicInstance({ bridge: { request() {} } } as never), TypeError);
});

test("a component bundle's default instance replaces the page instance outside run, and the page is still reachable", async () => {
  const a = guest(4);
  host.__runicBridge = createMockBridge();
  setDefaultSlots(() => slotsOf(a.instance));
  disposals.push(() => setDefaultSlots(undefined));
  assert.equal(defaultInstance(), a.instance);
  assert.equal(currentInstance(), a.instance);
  assert.equal(pageInstance().run(() => currentInstance()), pageInstance());
  assert.equal(pageInstance().run(() => a.instance.run(() => currentInstance())), a.instance);
  // A connect call outside run goes to the default instance, not to the page's __runicBridge.
  const client = await connectCounter();
  disposals.push(() => client.dispose());
  assert.equal(client.snapshot.count, 4);
  assert.equal((host.__runicBridge as MockBridge).calls.length, 0);

  // Without exactly one element, a component bundle's default instance has no Bridge and is disposed.
  setDefaultSlots(() => detachedSlots());
  assert.equal(defaultInstance().page, false);
  assert.equal(defaultInstance().bridge, undefined);
  await assert.rejects(connectCounter(), { name: "BridgeError", kind: "unavailable" });
  setDefaultSlots(undefined);
  assert.equal(defaultInstance(), pageInstance());
});
