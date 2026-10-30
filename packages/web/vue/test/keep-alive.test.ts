// @vitest-environment happy-dom

import { createApp, defineComponent, effectScope, h, nextTick, Suspense } from "vue";
import { describe, expect, test } from "vitest";
import { useSuspenseView, useView } from "../dist/index.js";

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
    increment() { client.push(current.count + 1); return Promise.resolve(current); },
  };
  return client;
}
type FakeClient = ReturnType<typeof fakeClient>;

function fakeReference(start = 1) {
  const clients: FakeClient[] = [];
  let fail = false;
  return {
    clients,
    connects: 0,
    failNext() { fail = true; },
    async connect() {
      this.connects++;
      if (fail) { fail = false; throw new Error("offline"); }
      const client = fakeClient(start + clients.length);
      clients.push(client);
      return client;
    },
  };
}

const settle = async () => { await Promise.resolve(); await Promise.resolve(); await nextTick(); };
const wait = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

describe("keep-alive", () => {
  test("a scope that starts again within the grace period takes over the client with its state", async () => {
    const reference = fakeReference();
    reference.connect = reference.connect.bind(reference);
    const first = effectScope();
    first.run(() => useView(reference, { keepAlive: 30 }));
    await settle();
    reference.clients[0]!.push(4);
    first.stop();
    expect(reference.clients[0]!.disposed).toBe(0);
    const second = effectScope();
    const view = second.run(() => useView(reference, { keepAlive: 30 }))!;
    // setup reads the kept client synchronously: no "Connecting…" render.
    expect(view.pending.value).toBe(false);
    expect(view.state.value).toEqual({ count: 4 });
    expect(reference.connects).toBe(1);
    second.stop();
    await wait(50);
    expect(reference.clients[0]!.disposed).toBe(1);
  });
});

describe("useSuspenseView", () => {
  test("an async setup under <Suspense> renders once the View connected", async () => {
    const reference = fakeReference(7);
    reference.connect = reference.connect.bind(reference);
    const Counter = defineComponent({
      async setup() {
        const { state, client } = await useSuspenseView(reference, { keepAlive: 0 });
        return () => h("button", { onClick: () => void client.increment() }, String(state.value.count));
      },
    });
    const host = document.createElement("div");
    const app = createApp({ render: () => h(Suspense, null, { default: () => h(Counter), fallback: () => h("p", "Connecting…") }) });
    app.mount(host);
    expect(host.textContent).toBe("Connecting…");
    await settle();
    await settle();
    expect(host.textContent).toBe("7");
    reference.clients[0]!.push(8);
    await nextTick();
    expect(host.textContent).toBe("8");
    app.unmount();
    expect(reference.clients[0]!.disposed).toBe(1);
  });

  test("rejects with the connection failure", async () => {
    const reference = fakeReference();
    reference.connect = reference.connect.bind(reference);
    reference.failNext();
    const scope = effectScope();
    const view = scope.run(() => useSuspenseView(reference))!;
    await expect(view).rejects.toThrow("offline");
    scope.stop();
  });
});
