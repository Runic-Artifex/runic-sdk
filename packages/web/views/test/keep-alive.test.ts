import assert from "node:assert/strict";
import { test } from "node:test";
import { createViewController, preloadView } from "../dist/index.js";
import { checkKeepAlive, defaultKeepAlive, viewSourcePreview } from "../dist/bindings.js";

type State = { readonly count: number };

function fakeClient(initial: number) {
  let current: State = { count: initial };
  const listeners = new Set<(state: State) => void>();
  const client = {
    disposed: 0,
    get snapshot() { return current; },
    subscribe(listener: (state: State) => void) {
      listener(current);
      if (client.disposed) return () => {};
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    dispose() { client.disposed++; listeners.clear(); },
    push(count: number) { current = { count }; for (const listener of [...listeners]) listener(current); },
    get listeners() { return listeners.size; },
  };
  return client;
}
type FakeClient = ReturnType<typeof fakeClient>;

function fakeConnector(start = 1) {
  const clients: FakeClient[] = [];
  const pending: (() => void)[] = [];
  let failures = 0;
  return {
    clients,
    get connects() { return clients.length + pending.length; },
    failNext() { failures++; },
    resolveAll() { while (pending.length) pending.shift()!(); },
    connect: () => new Promise<FakeClient>((resolve, reject) => {
      pending.push(() => {
        if (failures > 0) { failures--; reject(new Error("offline")); return; }
        const client = fakeClient(start + clients.length);
        clients.push(client);
        resolve(client);
      });
    }),
  };
}

const settle = () => new Promise<void>(resolve => setTimeout(resolve, 0));
const wait = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms));

test("keepAlive accepts whole milliseconds from 0 and defaults to two seconds", () => {
  assert.equal(defaultKeepAlive, 2_000);
  assert.equal(checkKeepAlive(undefined), 2_000);
  assert.equal(checkKeepAlive(0), 0);
  for (const value of [-1, 1.5, Infinity, Number.NaN, 2 ** 31]) assert.throws(() => checkKeepAlive(value), RangeError);
  assert.throws(() => createViewController({ keepAlive: -1 }), RangeError);
});

test("a released controller keeps its client and state, and takes the client back when it follows the source again", async () => {
  const connector = fakeConnector();
  const controller = createViewController<FakeClient>({ keepAlive: 50 });
  controller.setSource(connector);
  connector.resolveAll();
  await settle();
  const client = connector.clients[0]!;
  const before = controller.current;
  // StrictMode's simulated unmount, a hidden <Activity>, Fast Refresh: the effect is cleaned up and runs again.
  controller.release();
  assert.equal(controller.current, before, "release keeps current and does not notify");
  assert.equal(client.disposed, 0);
  assert.equal(client.listeners, 0, "a released controller stops following the client");
  controller.setSource({ connect: connector.connect });
  assert.equal(controller.current.client, client, "the kept client is taken back at once");
  assert.equal(controller.current.pending, false);
  assert.equal(connector.connects, 1, "the route is not opened again");
  client.push(5);
  assert.deepEqual(controller.current.state, { count: 5 });
  controller.dispose();
  assert.equal(client.disposed, 0, "disposing keeps the client for the grace period");
  await wait(80);
  assert.equal(client.disposed, 1, "and releases it after it");
});

test("a new controller of the same connector adopts a kept client with its state, as after A→B→A navigation", async () => {
  const connector = fakeConnector();
  const first = createViewController<FakeClient>({ keepAlive: 50 });
  first.setSource(connector);
  connector.resolveAll();
  await settle();
  connector.clients[0]!.push(3);
  first.dispose();
  const preview = viewSourcePreview(connector);
  assert.equal(preview?.client, connector.clients[0]);
  assert.deepEqual(preview?.state, { count: 3 });
  const second = createViewController<FakeClient>({ keepAlive: 50 });
  second.setSource(connector);
  assert.equal(second.current.client, connector.clients[0]);
  assert.deepEqual(second.current.state, { count: 3 });
  assert.equal(viewSourcePreview(connector), undefined, "an adopted client is no longer kept");
  // A third controller connects its own client: kept clients are not shared.
  const third = createViewController<FakeClient>({ keepAlive: 0 });
  third.setSource(connector);
  assert.equal(third.current.pending, true);
  connector.resolveAll();
  await settle();
  assert.equal(connector.clients.length, 2);
  second.dispose();
  third.dispose();
  assert.equal(connector.clients[1]!.disposed, 1, "keepAlive: 0 releases at once");
  await wait(80);
  assert.equal(connector.clients[0]!.disposed, 1);
});

test("a connection still on its way is kept and adopted, as by StrictMode's second mount", async () => {
  const connector = fakeConnector();
  const controller = createViewController<FakeClient>({ keepAlive: 50 });
  controller.setSource(connector);
  controller.release();
  controller.setSource(connector);
  assert.equal(controller.current.pending, true);
  connector.resolveAll();
  await settle();
  assert.equal(connector.connects, 1);
  assert.equal(controller.current.client, connector.clients[0]);
  controller.dispose();
  await wait(80);
  assert.equal(connector.clients[0]!.disposed, 1);
});

test("a connection that resolves after its grace period ended is released", async () => {
  const connector = fakeConnector();
  const controller = createViewController<FakeClient>({ keepAlive: 10 });
  controller.setSource(connector);
  controller.dispose();
  await wait(30);
  connector.resolveAll();
  await settle();
  assert.equal(connector.clients[0]!.disposed, 1);
  assert.equal(viewSourcePreview(connector), undefined);
});

test("a failed connection is not kept", async () => {
  const connector = fakeConnector();
  connector.failNext();
  const controller = createViewController<FakeClient>({ keepAlive: 50 });
  controller.setSource(connector);
  connector.resolveAll();
  await settle();
  assert.equal((controller.current.error as Error).message, "offline");
  controller.dispose();
  const next = createViewController<FakeClient>({ keepAlive: 0 });
  next.setSource(connector);
  assert.equal(next.current.pending, true, "a new controller connects again");
  connector.resolveAll();
  await settle();
  assert.deepEqual(next.current.state, { count: 1 });
  next.dispose();
});

test("the same source connecting again keeps the last state while it is pending", async () => {
  const connector = fakeConnector();
  const controller = createViewController<FakeClient>({ keepAlive: 10 });
  controller.setSource(connector);
  connector.resolveAll();
  await settle();
  controller.release();
  // The grace period ends while the component is hidden.
  await wait(30);
  assert.equal(connector.clients[0]!.disposed, 1);
  controller.setSource(connector);
  assert.equal(controller.current.pending, true);
  assert.deepEqual(controller.current.state, { count: 1 }, "the last state stays while it reconnects");
  assert.equal(controller.current.client, undefined);
  connector.resolveAll();
  await settle();
  assert.deepEqual(controller.current.state, { count: 2 });
  // A different source does not show the previous one's state.
  controller.setSource(fakeConnector(9));
  assert.equal(controller.current.state, undefined);
  controller.dispose();
});

test("a kept client is released through the release option of the controller that let go of it", async () => {
  const connector = fakeConnector();
  const released: FakeClient[] = [];
  const controller = createViewController<FakeClient>({ keepAlive: 10, release: client => released.push(client) });
  controller.setSource(connector);
  connector.resolveAll();
  await settle();
  controller.dispose();
  assert.deepEqual(released, []);
  await wait(30);
  assert.deepEqual(released, connector.clients);
  assert.equal(connector.clients[0]!.disposed, 0);
});

test("an observed client is never kept or released", () => {
  const client = fakeClient(4);
  const controller = createViewController<FakeClient>();
  controller.setSource(client);
  controller.release();
  assert.equal(client.listeners, 0);
  controller.setSource(client);
  assert.equal(client.listeners, 1, "following the client again subscribes again");
  controller.dispose();
  assert.equal(client.disposed, 0);
  assert.deepEqual(viewSourcePreview(client)?.state, { count: 4 });
});

test("preloadView connects ahead of the component and hands the client to the first controller", async () => {
  const connector = fakeConnector();
  const loading = preloadView(connector, { keepAlive: 50 });
  connector.resolveAll();
  assert.equal(await loading, connector);
  const controller = createViewController<FakeClient>();
  controller.setSource(connector);
  assert.equal(controller.current.client, connector.clients[0]);
  assert.equal(controller.current.pending, false);
  controller.dispose();

  // Nobody takes it: it is released after keepAlive.
  const unused = fakeConnector();
  const preloaded = preloadView(unused, { keepAlive: 10 });
  unused.resolveAll();
  await preloaded;
  await wait(30);
  assert.equal(unused.clients[0]!.disposed, 1);

  const failing = fakeConnector();
  failing.failNext();
  const failed = preloadView(failing);
  failing.resolveAll();
  await assert.rejects(failed, /offline/);
});

test("a kept client reads its route's state again when a controller takes it over", async () => {
  const { createMockBridge, installMockBridge } = await import("../dist/mock.js");
  const { connectView } = await import("../dist/generated.js");
  const wire = await import("../dist/wire.js");
  const host = globalThis as unknown as Record<symbol, { dispose?: () => void } | undefined>;
  const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
  host[runtimeKey]?.dispose?.();
  delete host[runtimeKey];
  const bridge = installMockBridge(createMockBridge());
  const view = bridge.view("content1", { contract: "Tests.Preview:fixture", state: { body: "Line one" } });
  const reference = { connect: () => connectView<{ readonly body: string }>({ contract: "Tests.Preview:fixture", route: "content1", members: { body: wire.string } }) };
  const first = createViewController<Awaited<ReturnType<typeof reference.connect>>>({ keepAlive: 100 });
  first.setSource(reference);
  for (let attempt = 0; attempt < 50 && first.current.pending; attempt++) await settle();
  assert.deepEqual(first.current.state, { body: "Line one" });
  first.dispose();
  // .NET suspends the route while nothing presents it and resumes the kept presentation with its current state,
  // without a push: the page misses the change.
  let dropping = true;
  const onMessage = bridge.onMessage.bind(bridge);
  Object.assign(bridge, { onMessage: (listener: (message: string) => void) => onMessage(message => { if (!dropping) listener(message); }) });
  view.update({ body: "Line two" });
  dropping = false;
  const second = createViewController<Awaited<ReturnType<typeof reference.connect>>>({ keepAlive: 0 });
  second.setSource(reference);
  assert.equal(second.current.pending, false, "the kept client is taken over at once");
  for (let attempt = 0; attempt < 50 && (second.current.state as { body: string } | undefined)?.body !== "Line two"; attempt++) await settle();
  assert.deepEqual(second.current.state, { body: "Line two" });
  assert.ok(bridge.calls.some(call => call.kind === "snapshot" && call.route === "content1"));
  second.dispose();
  delete (globalThis as { __runicBridge?: unknown }).__runicBridge;
});
