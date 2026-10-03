import { effectScope, nextTick, shallowRef } from "vue";
import { describe, expect, test } from "vitest";
import { useView, type ViewSource } from "../dist/index.js";

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

const settle = async () => { await Promise.resolve(); await nextTick(); };

describe("useView", () => {
  test("connects a reference into refs and disposes it with the scope", async () => {
    const reference = fakeReference();
    const scope = effectScope();
    const view = scope.run(() => useView(reference))!;
    expect(view.state.value).toBeUndefined();
    await settle();
    expect(view.state.value).toEqual({ count: 1 });
    expect(view.client.value).toBe(reference.clients[0]);
    reference.clients[0]!.push(2);
    expect(view.state.value).toEqual({ count: 2 });
    scope.stop();
    expect(reference.clients[0]!.disposed).toBe(1);
    expect(reference.clients[0]!.listeners).toBe(0);
  });

  test("disposes a connection that resolves after the scope stopped", async () => {
    const reference = fakeReference();
    const scope = effectScope();
    scope.run(() => useView(reference));
    scope.stop();
    await settle();
    expect(reference.clients).toHaveLength(1);
    expect(reference.clients[0]!.disposed).toBe(1);
  });

  test("follows a getter and reconnects only when the connect function changes", async () => {
    const first = fakeReference(10);
    const second = fakeReference(20);
    const selected = shallowRef<ViewSource<FakeClient>>({ connect: first.connect });
    const scope = effectScope();
    const view = scope.run(() => useView(() => selected.value))!;
    await settle();
    selected.value = { connect: first.connect };
    await settle();
    expect(first.clients).toHaveLength(1);
    selected.value = second;
    await settle();
    expect(first.clients[0]!.disposed).toBe(1);
    expect(view.state.value).toEqual({ count: 20 });
    selected.value = null;
    await settle();
    expect(second.clients[0]!.disposed).toBe(1);
    expect(view.state.value).toBeUndefined();
    scope.stop();
  });

  test("observes a connected client without disposing it", async () => {
    const client = fakeClient(3);
    const scope = effectScope();
    const view = scope.run(() => useView(client))!;
    expect(view.state.value).toEqual({ count: 3 });
    client.push(4);
    expect(view.state.value).toEqual({ count: 4 });
    scope.stop();
    expect(client.disposed).toBe(0);
    expect(client.listeners).toBe(0);
  });

  test("reports a failed connection and retries it", async () => {
    const reference = fakeReference(7);
    reference.failNext();
    const scope = effectScope();
    const view = scope.run(() => useView(reference))!;
    await settle();
    expect((view.error.value as Error).message).toBe("offline");
    view.retry();
    await settle();
    expect(view.error.value).toBeUndefined();
    expect(view.state.value).toEqual({ count: 7 });
    scope.stop();
  });
});
