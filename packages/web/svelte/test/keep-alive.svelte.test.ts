// @vitest-environment happy-dom

import { flushSync } from "svelte";
import { describe, expect, test } from "vitest";
import { useSuspenseView, useView } from "../src/views/index.js";

type State = { readonly count: number };

function fakeClient(initial: number) {
  let current: State = { count: initial };
  const listeners = new Set<(state: State) => void>();
  const client = {
    disposed: 0,
    get snapshot() { return current; },
    subscribe(listener: (state: State) => void) { listener(current); listeners.add(listener); return () => { listeners.delete(listener); }; },
    dispose() { client.disposed++; listeners.clear(); },
    push(count: number) { current = { count }; for (const listener of [...listeners]) listener(current); },
  };
  return client;
}
type FakeClient = ReturnType<typeof fakeClient>;

function fakeReference(start = 1) {
  const clients: FakeClient[] = [];
  let fail = false;
  const reference = {
    clients,
    connects: 0,
    failNext() { fail = true; },
    connect: async () => {
      reference.connects++;
      if (fail) { fail = false; throw new Error("offline"); }
      const client = fakeClient(start + clients.length);
      clients.push(client);
      return client;
    },
  };
  return reference;
}

const settle = async () => { await Promise.resolve(); await Promise.resolve(); flushSync(); };
const wait = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

describe("keep-alive", () => {
  test("an owner created within the grace period shows the kept client before its effect runs", async () => {
    const reference = fakeReference();
    const destroyFirst = $effect.root(() => { useView(() => reference, { keepAlive: 30 }); });
    flushSync();
    await settle();
    reference.clients[0]!.push(3);
    destroyFirst();
    let view!: ReturnType<typeof useView<FakeClient>>;
    const destroySecond = $effect.root(() => { view = useView(() => reference, { keepAlive: 30 }); });
    // Before the first flush: the effect that follows the source has not run.
    expect(view.pending).toBe(false);
    expect(view.state).toEqual({ count: 3 });
    flushSync();
    expect(view.client).toBe(reference.clients[0]);
    expect(reference.connects).toBe(1);
    destroySecond();
    await wait(50);
    expect(reference.clients[0]!.disposed).toBe(1);
  });
});

describe("useSuspenseView", () => {
  test("resolves with reactive state once connected, and rejects with a failure", async () => {
    const reference = fakeReference(5);
    let promise!: ReturnType<typeof useSuspenseView<FakeClient>>;
    const destroy = $effect.root(() => { promise = useSuspenseView(reference, { keepAlive: 0 }); });
    flushSync();
    await settle();
    const view = await promise;
    expect(view.state).toEqual({ count: 5 });
    expect(view.client).toBe(reference.clients[0]);
    reference.clients[0]!.push(6);
    flushSync();
    expect(view.state).toEqual({ count: 6 });
    destroy();
    expect(reference.clients[0]!.disposed).toBe(1);

    const failing = fakeReference();
    failing.failNext();
    let failed!: Promise<unknown>;
    const destroyFailed = $effect.root(() => { failed = useSuspenseView(failing); });
    flushSync();
    await settle();
    await expect(failed).rejects.toThrow("offline");
    destroyFailed();
  });
});
