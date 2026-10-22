import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import {
  createCommandController,
  createCollectionViewportController,
  createLatestOperationController,
  createOperationController,
  createViewController,
  onBridgeDiagnostic,
  waitForBridge,
  type BridgeDiagnostic,
  type RunicBridgeClient,
  type ViewClient,
} from "../dist/index.js";
import { bridgeOperations, connectView, disposable } from "../dist/generated.js";

// AbortSignal on connect, waitForBridge and operation waits, and Symbol.dispose on
// clients and controllers (I-038).

const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const contract = "Tests.CounterViewModel:fingerprint";
const hydrate = (wire: { readonly count: number }) => ({ count: wire.count });
const snapshot = JSON.stringify({ ok: true, state: { revision: 1, count: 1 }, error: null, protocol: 3 });

// A Bridge whose replies the test releases one at a time.
function heldBridge(connected = true) {
  const calls: string[] = [];
  const held = new Map<string, Array<(reply: string) => void>>();
  const bridge: RunicBridgeClient & { connected: boolean } = {
    connected,
    isConnected() { return this.connected; },
    call(name: string) {
      calls.push(name);
      return new Promise<string>(resolve => { held.set(name, [...held.get(name) ?? [], resolve]); });
    },
  };
  const reply = (name: string, value: string) => {
    const resolve = held.get(name)?.shift();
    assert.ok(resolve, `${name} was not called`);
    resolve(value);
  };
  delete host[runtimeKey];
  host["__runicBridge"] = bridge;
  return { bridge, calls, reply };
}

// A signal that counts its abort listeners, so a test can show none is left behind.
function countedSignal() {
  const controller = new AbortController();
  let listeners = 0;
  const { signal } = controller;
  const add = signal.addEventListener.bind(signal);
  const remove = signal.removeEventListener.bind(signal);
  const tracked = new Set<unknown>();
  signal.addEventListener = ((type: string, listener: EventListenerOrEventListenerObject, options?: AddEventListenerOptions | boolean) => {
    if (type === "abort" && !tracked.has(listener)) { tracked.add(listener); listeners++; }
    add(type, listener, options);
    // A `once` listener is removed by the dispatch itself.
    if (type === "abort" && typeof options === "object" && options.once) add("abort", () => { if (tracked.delete(listener)) listeners--; }, { once: true });
  }) as typeof signal.addEventListener;
  signal.removeEventListener = ((type: string, listener: EventListenerOrEventListenerObject, options?: EventListenerOptions | boolean) => {
    if (type === "abort" && tracked.delete(listener)) listeners--;
    remove(type, listener, options);
  }) as typeof signal.removeEventListener;
  return { controller, signal, listeners: () => listeners };
}

const isAbortError = (error: unknown) => error instanceof DOMException && error.name === "AbortError";
const tick = () => new Promise(resolve => setTimeout(resolve, 0));

function diagnostics() {
  const seen: BridgeDiagnostic[] = [];
  const stop = onBridgeDiagnostic(diagnostic => { seen.push(diagnostic); });
  return { seen, stop };
}

test("waitForBridge rejects with AbortError when aborted before or while waiting, and keeps no listener", async () => {
  const before = countedSignal();
  before.controller.abort();
  await assert.rejects(waitForBridge({ signal: before.signal }), isAbortError);
  assert.equal(before.listeners(), 0);

  heldBridge(false);
  const during = countedSignal();
  const waiting = waitForBridge({ timeout: 10_000, signal: during.signal });
  await new Promise(resolve => setTimeout(resolve, 80));
  during.controller.abort();
  await assert.rejects(waiting, isAbortError);
  assert.equal(during.listeners(), 0);

  // `abort(reason)` rejects with that reason.
  const reason = new Error("Navigated away.");
  const custom = new AbortController();
  const pending = waitForBridge({ timeout: 10_000, signal: custom.signal });
  custom.abort(reason);
  await assert.rejects(pending, error => error === reason);

  // A signal that never aborts keeps no listener after the wait resolves.
  const { bridge } = heldBridge(true);
  const idle = countedSignal();
  assert.equal(await waitForBridge({ signal: idle.signal }), bridge);
  assert.equal(idle.listeners(), 0);
});

test("connect rejects with AbortError before it starts, calls no route and reports no diagnostic", async () => {
  const { calls } = heldBridge();
  const { seen, stop } = diagnostics();
  try {
    const signal = countedSignal();
    signal.controller.abort();
    await assert.rejects(connectView({ contract, route: "counter", mount: false, hydrate, signal: signal.signal }), isAbortError);
    assert.deepEqual(calls, []);
    assert.equal(signal.listeners(), 0);
    assert.deepEqual(seen.filter(diagnostic => diagnostic.kind === "error"), []);
  } finally { stop(); }
});

test("connect aborted while the snapshot loads rejects, releases its route and keeps no listener", async () => {
  const { calls, reply } = heldBridge();
  const { seen, stop } = diagnostics();
  try {
    const signal = countedSignal();
    const connecting = connectView({ contract, route: "counter", mount: true, hydrate, signal: signal.signal });
    await tick();
    assert.deepEqual(calls, ["counterSnapshot"]);
    signal.controller.abort();
    await assert.rejects(connecting, isAbortError);
    assert.equal(signal.listeners(), 0);
    assert.equal(host["__counterChanged"], undefined, "the push callback was released");
    // The snapshot answering later changes nothing.
    reply("counterSnapshot", snapshot);
    await tick();
    assert.deepEqual(calls, ["counterSnapshot"]);
    assert.deepEqual(seen.filter(diagnostic => diagnostic.kind === "error"), []);
  } finally { stop(); }
});

test("connect aborted during the mount unmounts the View", async () => {
  const { calls, reply } = heldBridge();
  const signal = countedSignal();
  const connecting = connectView({ contract, route: "counter", mount: true, hydrate, signal: signal.signal });
  await tick();
  reply("counterSnapshot", snapshot);
  await tick();
  assert.deepEqual(calls, ["counterSnapshot", "counterMount"]);
  signal.controller.abort();
  await assert.rejects(connecting, isAbortError);
  assert.equal(signal.listeners(), 0);
  assert.deepEqual(calls, ["counterSnapshot", "counterMount", "counterUnmount"]);
});

test("aborting after connect resolved has no effect and leaves no listener", async () => {
  const { calls, reply } = heldBridge();
  const signal = countedSignal();
  const connecting = connectView({ contract, route: "counter", mount: false, hydrate, signal: signal.signal });
  await tick();
  reply("counterSnapshot", snapshot);
  const client = await connecting;
  assert.equal(signal.listeners(), 0);
  signal.controller.abort();
  assert.deepEqual(client.snapshot, { count: 1 });
  const invoked = client.invoke("counterIncrement");
  await tick();
  reply("counterIncrement", JSON.stringify({ ok: true, state: { revision: 2, count: 2 }, error: null }));
  assert.deepEqual(await invoked, { count: 2 });
  assert.deepEqual(calls, ["counterSnapshot", "counterIncrement"]);
  client.dispose();
});

test("an aborted operation wait rejects without cancelling the operation; other waits continue", async () => {
  const { calls, reply } = heldBridge();
  const connecting = connectView({ contract, route: "counter", mount: false, hydrate, operations: bridgeOperations });
  await tick();
  reply("counterSnapshot", snapshot);
  const client = await connecting;
  const starting = client.startOperation("Save", "request-1", () => "request-1", value => value as string);
  await tick();
  reply("counterStartSave", JSON.stringify({ kind: "accepted", terminal: null }));
  const operation = await starting;

  const before = countedSignal();
  before.controller.abort();
  await assert.rejects(operation.wait({ signal: before.signal }), isAbortError);
  await assert.rejects(operation.outcome({ signal: before.signal }), isAbortError);
  assert.equal(before.listeners(), 0);

  const during = countedSignal();
  const waiting = operation.wait({ signal: during.signal });
  const timed = countedSignal();
  const timedWait = operation.wait({ timeout: 60_000, signal: timed.signal });
  const plain = operation.wait();
  await tick();
  during.controller.abort();
  timed.controller.abort();
  await assert.rejects(waiting, isAbortError);
  await assert.rejects(timedWait, isAbortError);
  assert.equal(during.listeners(), 0);
  assert.equal(timed.listeners(), 0);
  assert.equal(calls.includes("__runicOperationCancel"), false, "an abort sends no cancellation request");

  const settled = countedSignal();
  const outcome = operation.outcome({ signal: settled.signal });
  reply("__runicOperationWait", JSON.stringify({ contract: `${contract}:counter`, requestId: "request-1", kind: "succeeded", result: "done" }));
  assert.equal((await plain).kind, "succeeded");
  assert.deepEqual(await outcome, { ok: true, value: "done" });
  assert.equal(settled.listeners(), 0);
  client.dispose();
});

test("controllers and generated clients release themselves at the end of a using block", async () => {
  const fake: ViewClient<number> = { snapshot: 1, subscribe: listener => { listener(1); return () => {}; }, dispose() {} };
  let released = 0;
  {
    using view = createViewController<ViewClient<number>>({ release: () => { released++; } });
    using command = createCommandController(() => 1);
    using operation = createOperationController(async () => undefined);
    using latest = createLatestOperationController();
    using viewport = createCollectionViewportController({ totalCount: 10, rowHeight: 20 });
    view.setSource({ connect: async () => fake });
    await tick();
    assert.equal(view.current.client, fake);
    void [command, operation, latest, viewport];
  }
  assert.equal(released, 1, "the view controller released its client");

  let disposed = 0;
  {
    using client = disposable({ ...fake, dispose() { disposed++; } });
    assert.equal(typeof client[Symbol.dispose], "function");
  }
  assert.equal(disposed, 1);
  // The symbol is not enumerable, so spreading or serializing a client does not copy it.
  const client = disposable({ dispose() {} });
  assert.deepEqual(Object.keys(client), ["dispose"]);
  assert.equal(Object.getOwnPropertySymbols({ ...client }).length, 0);
});

const root = fileURLToPath(new URL("..", import.meta.url));
const run = promisify(execFile);

test("the runtime loads and disposes where Symbol.dispose is undefined", async () => {
  // The script replaces the global Symbol before it imports the package, as an engine without
  // explicit resource management (older WebKitGTK and WKWebView) would have it.
  const { stdout } = await run(process.execPath, [join(root, "test", "no-symbol-dispose.mjs")], { cwd: root });
  assert.equal(stdout.trim(), "ok");
});

test("a consumer on the ES2022 library without skipLibCheck type-checks `using` with the package types", async () => {
  const directory = await mkdtemp(join(tmpdir(), "runic-views-using-"));
  const dist = join(root, "dist");
  try {
    await writeFile(join(directory, "consumer.ts"), [
      `import { createViewController, createCommandController, type ViewClient } from ${JSON.stringify(join(dist, "index.js"))};`,
      `import { disposable } from ${JSON.stringify(join(dist, "generated.js"))};`,
      `export function run(client: ViewClient<number>): number {`,
      `  using controller = createViewController<ViewClient<number>>();`,
      `  using command = createCommandController(() => 1);`,
      `  using connected = disposable(client);`,
      `  // @ts-expect-error A plain client interface has no Symbol.dispose member.`,
      `  using plain = client;`,
      `  return connected.snapshot + (controller.current.state ?? 0) + (command.current.pending ? 1 : 0) + plain.snapshot;`,
      `}`,
    ].join("\n"));
    await writeFile(join(directory, "tsconfig.json"), JSON.stringify({
      compilerOptions: {
        target: "ES2022", module: "NodeNext", moduleResolution: "NodeNext", lib: ["ES2022", "DOM"],
        strict: true, skipLibCheck: false, noEmit: true, types: [],
      },
      files: ["consumer.ts"],
    }));
    const tsc = fileURLToPath(import.meta.resolve("typescript/bin/tsc"));
    await run(process.execPath, [tsc, "-p", join(directory, "tsconfig.json")], { cwd: directory }).catch((error: { stdout?: string }) => {
      assert.fail(`tsc failed:\n${error.stdout ?? String(error)}`);
    });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
