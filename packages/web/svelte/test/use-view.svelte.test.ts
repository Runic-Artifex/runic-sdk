// @vitest-environment happy-dom

import { flushSync } from "svelte";
import { describe, expect, test } from "vitest";
import { useView, type ViewHandle, type ViewSource } from "../src/views/index.js";

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

function fakeReference(start = 1) {
  const clients: FakeClient[] = [];
  let fail = false;
  return {
    clients,
    failNext() { fail = true; },
    async connect() {
      if (fail) { fail = false; throw new Error("offline"); }
      const client = fakeClient(start + clients.length);
      clients.push(client);
      return client;
    },
  };
}

const settle = async () => { await Promise.resolve(); await Promise.resolve(); flushSync(); };

function mount(source: () => ViewSource<FakeClient>) {
  let view!: ViewHandle<FakeClient>;
  const destroy = $effect.root(() => { view = useView(source); });
  flushSync();
  return { view, destroy };
}

describe("useView", () => {
  test("connects a reference into reactive properties and disposes it with its owner", async () => {
    const reference = fakeReference();
    const { view, destroy } = mount(() => reference);
    expect(view.state).toBeUndefined();
    await settle();
    expect(view.state).toEqual({ count: 1 });
    expect(view.client).toBe(reference.clients[0]);
    reference.clients[0]!.push(2);
    expect(view.state).toEqual({ count: 2 });
    destroy();
    expect(reference.clients[0]!.disposed).toBe(1);
    expect(reference.clients[0]!.listeners).toBe(0);
  });

  test("disposes a connection that resolves after its owner was destroyed", async () => {
    const reference = fakeReference();
    const { destroy } = mount(() => reference);
    destroy();
    await settle();
    expect(reference.clients).toHaveLength(1);
    expect(reference.clients[0]!.disposed).toBe(1);
  });

  test("follows the getter and reconnects only when the connect function changes", async () => {
    const first = fakeReference(10);
    const second = fakeReference(20);
    let selected = $state.raw<ViewSource<FakeClient>>({ connect: first.connect });
    const { view, destroy } = mount(() => selected);
    await settle();
    selected = { connect: first.connect };
    await settle();
    expect(first.clients).toHaveLength(1);
    selected = second;
    await settle();
    expect(first.clients[0]!.disposed).toBe(1);
    expect(view.state).toEqual({ count: 20 });
    destroy();
    expect(second.clients[0]!.disposed).toBe(1);
  });

  test("reads and observes a connected client without disposing it", () => {
    const client = fakeClient(3);
    let view!: ViewHandle<FakeClient>;
    const destroy = $effect.root(() => { view = useView(() => client); });
    expect(view.state).toEqual({ count: 3 });
    expect(view.client).toBe(client);
    flushSync();
    client.push(4);
    expect(view.state).toEqual({ count: 4 });
    destroy();
    expect(client.disposed).toBe(0);
    expect(client.listeners).toBe(0);
  });

  test("reports a failed connection and retries it", async () => {
    const reference = fakeReference(7);
    reference.failNext();
    const { view, destroy } = mount(() => reference);
    await settle();
    expect((view.error as Error).message).toBe("offline");
    view.retry();
    flushSync();
    expect(view.error).toBeUndefined();
    await settle();
    expect(view.state).toEqual({ count: 7 });
    destroy();
  });
});
