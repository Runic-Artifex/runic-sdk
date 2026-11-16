// #798: a client that a binding keeps after its component went away belongs to the Bridge it connected over. A test
// that installs a new mock Bridge, or a page whose Bridge is replaced, never takes over a client of the previous one.
import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { createViewController, type ViewController } from "../dist/index.js";
import { viewSourcePreview } from "../dist/bindings.js";
import { connectView, type ViewConnection } from "../dist/generated.js";
import { createMockBridge, installMockBridge, releaseKeptViews, type MockBridge } from "../dist/mock.js";

type State = { readonly count: number };
type Counter = ViewConnection<State>;

// What a generated page reference is: one `connect` function, shared by every binding of the View.
const counter = {
  connect: () => connectView<State>({ contract: "mock.counter:mock", route: "counter", members: { count: wire => wire as number } }),
};

function serve(start: number): MockBridge {
  const bridge = installMockBridge(createMockBridge());
  bridge.view("counter", { state: { count: start }, commands: { bump: state => ({ count: (state["count"] as number) + 1 }) } });
  return bridge;
}

async function until(condition: () => boolean, what: string): Promise<void> {
  for (let attempt = 0; attempt < 200; attempt++) {
    if (condition()) return;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  assert.fail(`timed out waiting for ${what}`);
}

async function mount(): Promise<ViewController<Counter>> {
  const controller = createViewController<Counter>();
  controller.setSource(counter);
  await until(() => controller.current.state !== undefined, "the View's state");
  return controller;
}

const opens = (bridge: MockBridge) => bridge.calls.filter(call => call.kind === "open").length;
const closes = (bridge: MockBridge) => bridge.calls.filter(call => call.kind === "close").length;

const controllers: ViewController<Counter>[] = [];
afterEach(() => {
  for (const controller of controllers.splice(0)) controller.dispose();
  releaseKeptViews();
  delete (globalThis as { __runicBridge?: unknown }).__runicBridge;
});

test("two consecutive tests with new mock Bridges do not share a kept client", async () => {
  // The first test: its component mounts, runs a command and is destroyed. The binding keeps its client.
  const first = serve(1);
  const before = await mount();
  const kept = before.current.client!;
  await kept.command("bump");
  await until(() => before.current.state?.count === 2, "the first test's command");
  before.dispose();
  assert.equal(closes(first), 0, "the binding keeps the client for its keepAlive period");

  // The next test installs a new Bridge: the client kept for the previous one is released at once.
  const second = serve(10);
  await until(() => closes(first) === 1, "the kept client's close on the first Bridge");
  assert.equal(viewSourcePreview(counter), undefined, "a binding shows nothing of the previous test's client");
  const after = await mount();
  controllers.push(after);
  assert.notEqual(after.current.client, kept);
  assert.deepEqual(after.current.state, { count: 10 }, "the state comes from the new mock");
  await after.current.client!.command("bump");
  await until(() => after.current.state?.count === 11, "the second test's command");
  assert.equal(second.calls.filter(call => call.kind === "command").length, 1, "the command reaches the new mock");
  assert.equal(first.calls.filter(call => call.kind === "command").length, 1, "the previous mock receives nothing new");
});

test("a page whose Bridge is replaced does not hand a client of the previous Bridge over", async () => {
  // Not through installMockBridge: the host replaces `__runicBridge`, so only taking over checks the Bridge.
  const first = serve(1);
  const before = await mount();
  const kept = before.current.client!;
  before.dispose();
  const second = createMockBridge();
  second.view("counter", { state: { count: 20 } });
  (globalThis as { __runicBridge?: unknown }).__runicBridge = second;
  assert.equal(viewSourcePreview(counter), undefined);
  const after = createViewController<Counter>();
  controllers.push(after);
  after.setSource(counter);
  assert.equal(after.current.pending, true, "the binding connects instead of taking the kept client over");
  await until(() => after.current.state !== undefined, "the new Bridge's state");
  assert.notEqual(after.current.client, kept);
  assert.deepEqual(after.current.state, { count: 20 });
  assert.equal(closes(first), 1, "the stale client is released when a binding passes it over");
  assert.equal(opens(second), 1);
});

test("a binding of the same Bridge still takes the kept client over, and releaseKeptViews releases it", async () => {
  const bridge = serve(1);
  const first = await mount();
  const client = first.current.client!;
  first.dispose();
  // StrictMode's second mount, A→B→A navigation: the same Bridge hands the client over.
  const again = createViewController<Counter>();
  again.setSource(counter);
  assert.equal(again.current.client, client);
  assert.equal(again.current.pending, false);
  assert.equal(opens(bridge), 1);
  again.dispose();

  // A suite that keeps one Bridge across tests releases the kept clients after each test.
  assert.equal(releaseKeptViews(), 1);
  await until(() => closes(bridge) === 1, "the released client's close");
  assert.equal(releaseKeptViews(), 0);
  const next = await mount();
  controllers.push(next);
  assert.notEqual(next.current.client, client);
  assert.equal(opens(bridge), 2, "the next binding opens the route again");
});
