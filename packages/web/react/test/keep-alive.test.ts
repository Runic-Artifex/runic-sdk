import * as React from "react";
import { createElement, memo, StrictMode, Suspense, useState, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, test } from "vitest";
import { useSuspenseView, useView, useViewState, type ViewHandle } from "../dist/index.js";

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
// <Activity> is stable in React 19.2; the lowest supported React (18.3) has none.
const Activity = (React as unknown as { Activity?: React.ComponentType<{ mode: "visible" | "hidden"; children: ReactNode }> }).Activity;

type State = { readonly count: number; readonly title: string };

function fakeClient(initial: number) {
  let current: State = { count: initial, title: "Draft" };
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
    push(change: Partial<State>) { current = { ...current, ...change }; for (const listener of [...listeners]) listener(current); },
    get listeners() { return listeners.size; },
  };
  return client;
}
type FakeClient = ReturnType<typeof fakeClient>;

function fakeReference(start = 1) {
  const clients: FakeClient[] = [];
  const pending: (() => void)[] = [];
  let connects = 0;
  return {
    clients,
    get connects() { return connects; },
    resolveAll() { while (pending.length) pending.shift()!(); },
    connect: () => {
      connects++;
      return new Promise<FakeClient>(resolve => {
        pending.push(() => {
          const client = fakeClient(start + clients.length);
          clients.push(client);
          resolve(client);
        });
      });
    },
  };
}
type FakeReference = ReturnType<typeof fakeReference>;

let root: Root | undefined;
const renders: string[] = [];

function Probe({ source, keepAlive }: { readonly source: FakeReference; readonly keepAlive?: number }): ReactNode {
  const view = useView(source, keepAlive === undefined ? {} : { keepAlive });
  const shown = view.state === undefined ? "connecting" : String(view.state.count);
  renders.push(shown);
  return createElement("output", null, shown);
}

async function render(element: ReactNode) {
  await act(async () => {
    root ??= createRoot(document.body.appendChild(document.createElement("div")));
    root.render(element);
  });
}

const text = () => document.querySelector("output")?.textContent;
const wait = (ms: number) => act(() => new Promise<void>(resolve => setTimeout(resolve, ms)));

afterEach(async () => {
  await act(async () => root?.unmount());
  root = undefined;
  renders.length = 0;
  document.body.innerHTML = "";
});

describe("keep-alive", () => {
  test("StrictMode's second mount takes over the first connection", async () => {
    const reference = fakeReference();
    await render(createElement(StrictMode, null, createElement(Probe, { source: reference, keepAlive: 20 })));
    await act(async () => reference.resolveAll());
    expect(reference.connects).toBe(1);
    expect(text()).toBe("1");
    await act(async () => root?.unmount());
    root = undefined;
    expect(reference.clients[0]!.disposed).toBe(0);
    await wait(40);
    expect(reference.clients[0]!.disposed).toBe(1);
  });

  test("a component that mounts again within the grace period renders the kept state at once (A→B→A)", async () => {
    const reference = fakeReference();
    const app = (show: boolean) => show ? createElement(Probe, { source: reference, keepAlive: 50 }) : createElement("p", null, "other");
    await render(app(true));
    await act(async () => reference.resolveAll());
    await act(async () => reference.clients[0]!.push({ count: 4 }));
    await render(app(false));
    expect(reference.clients[0]!.disposed).toBe(0);
    renders.length = 0;
    await render(app(true));
    expect(renders).not.toContain("connecting");
    expect(text()).toBe("4");
    expect(reference.connects).toBe(1);
    await act(async () => reference.clients[0]!.push({ count: 5 }));
    expect(text()).toBe("5");
  });

  test("a changed reference keeps the previous client for the grace period", async () => {
    const first = fakeReference(10);
    const second = fakeReference(20);
    await render(createElement(Probe, { source: first, keepAlive: 20 }));
    await act(async () => first.resolveAll());
    await render(createElement(Probe, { source: second, keepAlive: 20 }));
    expect(text()).toBe("connecting");
    expect(first.clients[0]!.disposed).toBe(0);
    await render(createElement(Probe, { source: first, keepAlive: 20 }));
    expect(text()).toBe("10");
    expect(first.connects).toBe(1);
    await wait(40);
    await act(async () => second.resolveAll());
    // The second connection resolved after its grace period ended.
    expect(second.clients[0]!.disposed).toBe(1);
  });

  test.skipIf(Activity === undefined)("a hidden <Activity> keeps its View and shows it again without reconnecting", async () => {
    const reference = fakeReference();
    const app = (mode: "visible" | "hidden") => createElement(Activity!, { mode, children: createElement(Probe, { source: reference, keepAlive: 50 }) });
    await render(app("visible"));
    await act(async () => reference.resolveAll());
    await render(app("hidden"));
    renders.length = 0;
    await render(app("visible"));
    expect(renders).not.toContain("connecting");
    expect(text()).toBe("1");
    expect(reference.connects).toBe(1);
  });

  test("a hidden <Activity> past the grace period reconnects and keeps showing the last state", async () => {
    if (Activity === undefined) return;
    const reference = fakeReference();
    const app = (mode: "visible" | "hidden") => createElement(Activity, { mode, children: createElement(Probe, { source: reference, keepAlive: 10 }) });
    await render(app("visible"));
    await act(async () => reference.resolveAll());
    await render(app("hidden"));
    await wait(30);
    expect(reference.clients[0]!.disposed).toBe(1);
    renders.length = 0;
    await render(app("visible"));
    expect(renders).not.toContain("connecting");
    expect(reference.connects).toBe(2);
    await act(async () => reference.resolveAll());
    expect(text()).toBe("2");
  });

  test("useSuspenseView keeps its client for a component that mounts again", async () => {
    const reference = fakeReference();
    function Page(): ReactNode {
      const { state } = useSuspenseView(reference, { keepAlive: 50 });
      return createElement("output", null, String(state.count));
    }
    const app = (show: boolean) => createElement(Suspense, { fallback: createElement("output", null, "suspended") }, show ? createElement(Page) : null);
    await render(app(true));
    await act(async () => reference.resolveAll());
    expect(text()).toBe("1");
    await render(app(false));
    await render(app(true));
    expect(text()).toBe("1");
    expect(reference.connects).toBe(1);
    await act(async () => root?.unmount());
    root = undefined;
    await wait(80);
    expect(reference.clients[0]!.disposed).toBe(1);
    expect(() => useSuspenseView(reference, { keepAlive: -1 })).toThrow(RangeError);
  });
});

describe("useViewState", () => {
  test("re-renders a component only when its selected member changes", async () => {
    const reference = fakeReference();
    let titleRenders = 0;
    let countRenders = 0;
    const Title = memo(function Title({ client }: { readonly client: FakeClient | undefined }) {
      titleRenders++;
      return createElement("h1", null, useViewState(client, state => state.title) ?? "…");
    });
    const Count = memo(function Count({ client }: { readonly client: FakeClient | undefined }) {
      countRenders++;
      return createElement("output", null, String(useViewState(client, state => state.count) ?? "…"));
    });
    function Page(): ReactNode {
      const { client } = useView(reference, { keepAlive: 0 });
      return createElement("section", null, createElement(Title, { client }), createElement(Count, { client }));
    }
    await render(createElement(Page));
    await act(async () => reference.resolveAll());
    expect(document.querySelector("h1")?.textContent).toBe("Draft");
    const titles = titleRenders;
    const counts = countRenders;
    await act(async () => reference.clients[0]!.push({ count: 2 }));
    await act(async () => reference.clients[0]!.push({ count: 3 }));
    expect(text()).toBe("3");
    expect(titleRenders).toBe(titles);
    expect(countRenders).toBe(counts + 2);
    await act(async () => reference.clients[0]!.push({ title: "Final" }));
    expect(document.querySelector("h1")?.textContent).toBe("Final");
    expect(titleRenders).toBe(titles + 1);
  });

  test("accepts a useView handle and keeps an equal derived selection by isEqual", async () => {
    const reference = fakeReference();
    const seen: (readonly number[] | undefined)[] = [];
    let handle: ViewHandle<FakeClient> | undefined;
    function Page(): ReactNode {
      handle = useView(reference, { keepAlive: 0 });
      const [, force] = useState(0);
      void force;
      const digits = useViewState(handle, state => [state.count % 2], (left, right) => left[0] === right[0]);
      seen.push(digits);
      return createElement("output", null, String(digits?.[0] ?? "…"));
    }
    await render(createElement(Page));
    expect(text()).toBe("…");
    await act(async () => reference.resolveAll());
    expect(text()).toBe("1");
    const first = seen.at(-1);
    await act(async () => reference.clients[0]!.push({ count: 3 }));
    expect(seen.at(-1)).toBe(first);
    await act(async () => reference.clients[0]!.push({ count: 4 }));
    expect(text()).toBe("0");
  });
});
