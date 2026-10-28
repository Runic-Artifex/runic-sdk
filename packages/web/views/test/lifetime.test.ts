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
const members = { count: (value: unknown) => value as number };
const opened = { ok: true, revision: 1, state: { count: 1 }, presentation: "ok" };

interface Held { readonly kind: string; readonly message: Record<string, unknown>; resolve(reply: object): void }

// A protocol-4 host bridge whose replies the test releases one at a time. It answers `hello` itself.
function heldBridge(connected = true) {
  const requests: Held[] = [];
  const messageListeners = new Set<(message: string) => void>();
  const bridge: RunicBridgeClient & { connected: boolean } = {
    connected,
    isConnected() { return this.connected; },
    request(text) {
      const message = JSON.parse(text) as Record<string, unknown>;
      if (message["kind"] === "hello")
        return Promise.resolve(JSON.stringify({ ok: true, protocol: 4, implementation: { name: "Tests.Host", version: "1.0.0" } }));
      return new Promise<string>(resolve => {
        requests.push({ kind: message["kind"] as string, message, resolve: reply => resolve(JSON.stringify(reply)) });
      });
    },
    onMessage(listener) { messageListeners.add(listener); return () => { messageListeners.delete(listener); }; },
    onConnectionChange(listener) { listener(bridge.connected); return () => {}; },
  };
  // Requests answered so far: `kinds()` lists the open ones and `all()` every request after hello.
  const sent: Held[] = [];
  const kinds = () => requests.map(request => request.kind);
  const all = () => [...sent, ...requests].map(request => request.kind);
  const reply = (kind: string, value: object) => {
    const index = requests.findIndex(request => request.kind === kind);
    assert.ok(index >= 0, `${kind} was not requested`);
    const [request] = requests.splice(index, 1);
    sent.push(request!);
    request!.resolve(value);
  };
  const push = (message: object) => { for (const listener of [...messageListeners]) listener(JSON.stringify(message)); };
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  host["__runicBridge"] = bridge;
  return { bridge, requests, kinds, all, reply, push };
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
const routes = () => (host[runtimeKey] as { routes: Map<string, unknown> }).routes;

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

test("connect rejects with AbortError before it starts, sends no request and reports no diagnostic", async () => {
  const { all } = heldBridge();
  const { seen, stop } = diagnostics();
  try {
    const signal = countedSignal();
    signal.controller.abort();
    await assert.rejects(connectView({ contract, route: "counter", members, signal: signal.signal }), isAbortError);
    await tick();
    assert.deepEqual(all(), []);
    assert.equal(signal.listeners(), 0);
    assert.deepEqual(seen.filter(diagnostic => diagnostic.kind === "error"), []);
  } finally { stop(); }
});

// The open registered a presentation the page no longer wants, so the client closes it when it learns of it.
test("connect aborted while the open is in flight rejects, releases its route, closes the presentation and keeps no listener", async () => {
  const { kinds, all, reply, requests } = heldBridge();
  const { seen, stop } = diagnostics();
  try {
    const signal = countedSignal();
    const connecting = connectView({ contract, route: "counter", members, signal: signal.signal });
    await tick();
    assert.deepEqual(kinds(), ["open"]);
    const presentation = requests[0]!.message["presentation"];
    signal.controller.abort();
    await assert.rejects(connecting, isAbortError);
    assert.equal(signal.listeners(), 0);
    assert.equal(routes().has("counter"), false, "the route was released");
    // The open answering later registers nothing on the page, and its presentation is closed.
    reply("open", opened);
    await tick();
    assert.equal(routes().has("counter"), false);
    assert.deepEqual(all(), ["open", "close"]);
    assert.equal(requests.find(request => request.kind === "close")?.message["presentation"], presentation);
    assert.deepEqual(seen.filter(diagnostic => diagnostic.kind === "error"), []);
  } finally { stop(); }
});

test("aborting after connect resolved has no effect and leaves no listener", async () => {
  const { kinds, reply } = heldBridge();
  const signal = countedSignal();
  const connecting = connectView<{ count: number }>({ contract, route: "counter", members, signal: signal.signal });
  await tick();
  reply("open", opened);
  const client = await connecting;
  assert.equal(signal.listeners(), 0);
  signal.controller.abort();
  assert.deepEqual(client.snapshot, { count: 1 });
  const invoked = client.command("increment");
  await tick();
  assert.deepEqual(kinds(), ["command"]);
  reply("command", { ok: true, revision: 1 });
  assert.deepEqual(await invoked, { count: 1 });
  client.dispose();
});

test("an aborted operation wait rejects without cancelling the operation; other waits continue", async () => {
  const { all, reply, push } = heldBridge();
  const connecting = connectView<{ count: number }>({ contract, route: "counter", members, operations: bridgeOperations });
  await tick();
  reply("open", opened);
  const client = await connecting;
  const starting = client.start("save", "request-1", undefined, { decode: value => value as string });
  await tick();
  reply("start", { ok: true, revision: 1, admission: "accepted", status: { kind: "running" } });
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
  reply("watch", { ok: true, status: { kind: "running" } });
  during.controller.abort();
  timed.controller.abort();
  await assert.rejects(waiting, isAbortError);
  await assert.rejects(timedWait, isAbortError);
  assert.equal(during.listeners(), 0);
  assert.equal(timed.listeners(), 0);
  assert.equal(all().includes("cancel"), false, "an abort sends no cancellation request");
  assert.equal(all().includes("unwatch"), false, "the remaining wait keeps the watch");

  const settled = countedSignal();
  const outcome = operation.outcome({ signal: settled.signal });
  push({ kind: "operation", route: "counter", member: "save", operation: "request-1", contract,
    status: { kind: "succeeded", revision: 1, result: "done" } });
  assert.equal((await plain).kind, "succeeded");
  assert.deepEqual(await outcome, { ok: true, value: "done" });
  assert.equal(settled.listeners(), 0);
  assert.deepEqual(all().filter(kind => kind !== "close"), ["open", "start", "watch"]);
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
