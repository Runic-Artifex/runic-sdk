import assert from "node:assert/strict";
import { test } from "node:test";
import {
  createCollectionViewportController,
  createCommandController,
  createViewController,
  isViewClient,
  viewSourceIdentity,
  type ViewControllerState,
} from "../dist/index.js";

type State = { readonly count: number };

// Mirrors a generated client: subscribe delivers the current state first, and
// a disposed client keeps its last snapshot.
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

// A connector whose connections resolve or fail when the test says so.
function fakeConnector(start = 1) {
  const clients: FakeClient[] = [];
  const pending: (() => void)[] = [];
  let failures = 0;
  return {
    clients,
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

function record<TClient extends FakeClient>(controller: { readonly current: ViewControllerState<TClient>; subscribe(listener: () => void): () => void }) {
  const states: ViewControllerState<TClient>[] = [];
  controller.subscribe(() => states.push(controller.current));
  return states;
}

test("a view controller connects a connector, follows its state and releases it on dispose", async () => {
  const connector = fakeConnector();
  const controller = createViewController<FakeClient>();
  const states = record(controller);
  assert.equal(controller.setSource(connector), true);
  assert.equal(states.length, 0, "setSource does not notify");
  assert.equal(controller.current.pending, true);
  assert.equal(controller.current.client, undefined);
  connector.resolveAll();
  await settle();
  assert.equal(controller.current.pending, false);
  assert.equal(controller.current.client, connector.clients[0]);
  assert.deepEqual(controller.current.state, { count: 1 });
  connector.clients[0]!.push(2);
  assert.deepEqual(controller.current.state, { count: 2 });
  // Each change publishes a new object, so external stores can compare by identity.
  assert.equal(new Set(states).size, states.length);
  controller.dispose();
  assert.equal(connector.clients[0]!.disposed, 1);
  assert.equal(connector.clients[0]!.listeners, 0);
  assert.equal(controller.setSource(fakeConnector()), false);
});

test("a view controller ignores a source with the same identity and releases a replaced one", async () => {
  const first = fakeConnector(10);
  const controller = createViewController<FakeClient>();
  controller.setSource({ connect: first.connect });
  first.resolveAll();
  await settle();
  assert.equal(controller.setSource({ connect: first.connect }), false);
  assert.equal(first.clients.length, 1);

  const second = fakeConnector(20);
  assert.equal(controller.setSource(second), true);
  assert.equal(first.clients[0]!.disposed, 1);
  assert.equal(controller.current.state, undefined);
  second.resolveAll();
  await settle();
  assert.deepEqual(controller.current.state, { count: 20 });
  controller.setSource(null);
  assert.equal(second.clients[0]!.disposed, 1);
  assert.equal(controller.current.client, undefined);
});

test("a view controller releases a connection that resolves after its source changed", async () => {
  const connector = fakeConnector();
  const controller = createViewController<FakeClient>();
  controller.setSource(connector);
  controller.setSource(undefined);
  connector.resolveAll();
  await settle();
  assert.equal(connector.clients[0]!.disposed, 1);
  assert.equal(controller.current.client, undefined);
});

test("a view controller observes a connected client without releasing it", () => {
  const client = fakeClient(3);
  const controller = createViewController<FakeClient>();
  controller.setSource(client);
  assert.deepEqual(controller.current.state, { count: 3 });
  assert.equal(controller.current.pending, false);
  client.push(4);
  assert.deepEqual(controller.current.state, { count: 4 });
  controller.dispose();
  assert.equal(client.disposed, 0);
  assert.equal(client.listeners, 0);
});

test("a view controller reports a failed connection and retries it", async () => {
  const connector = fakeConnector(7);
  connector.failNext();
  const controller = createViewController<FakeClient>();
  controller.setSource(connector);
  connector.resolveAll();
  await settle();
  assert.equal((controller.current.error as Error).message, "offline");
  assert.equal(controller.current.pending, false);
  controller.retry();
  assert.equal(controller.current.error, undefined);
  assert.equal(controller.current.pending, true);
  connector.resolveAll();
  await settle();
  assert.deepEqual(controller.current.state, { count: 7 });
  controller.dispose();
});

test("a view controller reports a connector that throws synchronously", async () => {
  const controller = createViewController<FakeClient>();
  controller.setSource({ connect: () => { throw new Error("no host"); } });
  await settle();
  assert.equal((controller.current.error as Error).message, "no host");
});

test("a view controller releases through the release option", async () => {
  const connector = fakeConnector();
  const released: FakeClient[] = [];
  const controller = createViewController<FakeClient>({ release: client => released.push(client) });
  controller.setSource(connector);
  connector.resolveAll();
  await settle();
  controller.dispose();
  assert.deepEqual(released, connector.clients);
  assert.equal(connector.clients[0]!.disposed, 0);
});

test("source helpers tell clients from connectors", () => {
  const client = fakeClient(1);
  const connect = async () => client;
  assert.equal(isViewClient(client), true);
  assert.equal(isViewClient({ connect }), false);
  assert.equal(viewSourceIdentity({ connect }), connect);
  assert.equal(viewSourceIdentity(client), client);
  assert.equal(viewSourceIdentity(null), undefined);
});

test("a command controller tracks pending and error and never rejects", async () => {
  let fail = true;
  const resolvers: (() => void)[] = [];
  const command = createCommandController((value: number) => new Promise<number>((resolve, reject) => {
    resolvers.push(() => fail ? reject(new Error("rejected")) : resolve(value * 2));
  }));
  const changes: boolean[] = [];
  command.subscribe(() => changes.push(command.current.pending));
  const failed = command.run(1);
  assert.equal(command.current.pending, true);
  resolvers.shift()!();
  assert.equal(await failed, undefined);
  assert.equal(command.current.pending, false);
  assert.equal((command.current.error as Error).message, "rejected");

  fail = false;
  const first = command.run(2);
  assert.equal(command.current.error, undefined);
  const second = command.run(3);
  resolvers.shift()!();
  assert.equal(await first, 4);
  assert.equal(command.current.pending, true);
  resolvers.shift()!();
  assert.equal(await second, 6);
  assert.equal(command.current.pending, false);
  assert.deepEqual(changes, [true, false, true, true, false]);
});

test("a command controller accepts synchronous and missing results and can be reset", async () => {
  const absent = createCommandController((client?: { increment(): Promise<number> }) => client?.increment());
  assert.equal(await absent.run(undefined), undefined);
  assert.equal(absent.current.error, undefined);
  const throwing = createCommandController(() => { throw new Error("sync"); });
  assert.equal(await throwing.run(), undefined);
  assert.equal((throwing.current.error as Error).message, "sync");
  throwing.reset();
  assert.equal(throwing.current.error, undefined);
  throwing.dispose();
  await throwing.run();
  assert.equal(throwing.current.error, undefined);
});

function fakeScroller(height: number) {
  const listeners = new Set<() => void>();
  return {
    scrollTop: 0,
    clientHeight: height,
    addEventListener(_type: string, listener: () => void) { listeners.add(listener); },
    removeEventListener(_type: string, listener: () => void) { listeners.delete(listener); },
    scrollTo(top: number) { this.scrollTop = top; for (const listener of [...listeners]) listener(); },
    get listeners() { return listeners.size; },
  };
}

test("a viewport controller follows its container and publishes only changed ranges", () => {
  const viewport = createCollectionViewportController({ totalCount: 1000, rowHeight: 32 });
  assert.deepEqual(viewport.current, { start: 0, size: 5, offset: 0, totalSize: 32000 });
  let changes = 0;
  viewport.subscribe(() => changes++);
  const scroller = fakeScroller(640);
  viewport.attach(scroller as unknown as HTMLElement);
  assert.deepEqual(viewport.current, { start: 0, size: 25, offset: 0, totalSize: 32000 });
  scroller.scrollTo(3200);
  assert.deepEqual(viewport.current, { start: 95, size: 30, offset: 3040, totalSize: 32000 });
  scroller.scrollTo(3210);
  assert.equal(changes, 2);
  viewport.update({ totalCount: 10, rowHeight: 32, overscan: 0 });
  assert.deepEqual(viewport.current, { start: 10, size: 0, offset: 320, totalSize: 320 });
  viewport.attach(null);
  assert.equal(scroller.listeners, 0);
  assert.deepEqual(viewport.current, { start: 0, size: 0, offset: 0, totalSize: 320 });
  viewport.dispose();
  viewport.update({ totalCount: 20, rowHeight: 32 });
  assert.equal(viewport.current.totalSize, 320);
});

test("a command controller ignores the failure of a run superseded by a later one", async () => {
  const outcomes: ((fail: boolean) => void)[] = [];
  const command = createCommandController(() => new Promise<number>((resolve, reject) => {
    outcomes.push(fail => fail ? reject(new Error("stale")) : resolve(1));
  }));
  const older = command.run();
  const newer = command.run();
  outcomes[1]!(false);
  assert.equal(await newer, 1);
  assert.equal(command.current.pending, true);
  outcomes[0]!(true);
  assert.equal(await older, undefined);
  assert.equal(command.current.pending, false);
  assert.equal(command.current.error, undefined);

  // The latest run's own failure is still reported.
  const failing = command.run();
  outcomes[2]!(true);
  await failing;
  assert.equal((command.current.error as unknown as Error).message, "stale");
});
