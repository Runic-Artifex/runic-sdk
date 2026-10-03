import { act, createElement, StrictMode, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, test } from "vitest";
import { useView, type ViewHandle, type ViewSource } from "../dist/index.js";

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

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

function fakeReference(start = 1) {
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

let root: Root | undefined;
let latest: ViewHandle<FakeClient> | undefined;

function Probe({ source }: { readonly source: ViewSource<FakeClient> }): ReactNode {
  latest = useView(source);
  return createElement("output", null, latest.state?.count ?? "connecting");
}

async function render(source: ViewSource<FakeClient>, strict = false) {
  const element = createElement(Probe, { source });
  await act(async () => {
    root ??= createRoot(document.body.appendChild(document.createElement("div")));
    root.render(strict ? createElement(StrictMode, null, element) : element);
  });
}

const text = () => document.querySelector("output")?.textContent;

afterEach(async () => {
  await act(async () => root?.unmount());
  root = undefined;
  latest = undefined;
  document.body.innerHTML = "";
});

describe("useView", () => {
  test("connects a reference, renders pushed state and disposes on unmount", async () => {
    const reference = fakeReference();
    await render(reference);
    expect(text()).toBe("connecting");
    await act(async () => reference.resolveAll());
    expect(text()).toBe("1");
    expect(latest?.client).toBe(reference.clients[0]);
    await act(async () => reference.clients[0]!.push(5));
    expect(text()).toBe("5");
    await act(async () => root?.unmount());
    root = undefined;
    expect(reference.clients[0]!.disposed).toBe(1);
    expect(reference.clients[0]!.listeners).toBe(0);
  });

  test("disposes the StrictMode probe connection and keeps one live client", async () => {
    const reference = fakeReference();
    await render(reference, true);
    await act(async () => reference.resolveAll());
    expect(reference.clients).toHaveLength(2);
    expect(reference.clients.filter(client => client.disposed === 0)).toHaveLength(1);
    expect(text()).toBe(String(reference.clients.find(client => client.disposed === 0)!.snapshot.count));
    await act(async () => root?.unmount());
    root = undefined;
    expect(reference.clients.every(client => client.disposed === 1)).toBe(true);
  });

  test("reconnects only when the reference's connect function changes", async () => {
    const first = fakeReference(10);
    await render({ connect: first.connect });
    await act(async () => first.resolveAll());
    await render({ connect: first.connect });
    expect(first.clients).toHaveLength(1);
    expect(text()).toBe("10");

    const second = fakeReference(20);
    await render(second);
    expect(first.clients[0]!.disposed).toBe(1);
    expect(text()).toBe("connecting");
    await act(async () => second.resolveAll());
    expect(text()).toBe("20");
  });

  test("observes a connected client without disposing it", async () => {
    const client = fakeClient(3);
    await render(client);
    expect(text()).toBe("3");
    await act(async () => client.push(4));
    expect(text()).toBe("4");
    await render(null);
    expect(text()).toBe("connecting");
    expect(client.disposed).toBe(0);
    expect(client.listeners).toBe(0);
  });

  test("reports a failed connection and retries it", async () => {
    const reference = fakeReference(7);
    reference.failNext();
    await render(reference);
    await act(async () => reference.resolveAll());
    expect((latest?.error as Error).message).toBe("offline");
    await act(async () => latest!.retry());
    expect(latest?.error).toBeUndefined();
    await act(async () => reference.resolveAll());
    expect(text()).toBe("7");
  });
});
