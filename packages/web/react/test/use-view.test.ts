import { act, Component, createElement, StrictMode, Suspense, useState, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, expectTypeOf, test, vi } from "vitest";
import { bridgeFailure, bridgeSuccess, type BridgeOutcome } from "@runic-artifex/views";
import {
  retrySuspenseView, useCollectionViewport, useCommand, useSuspenseView, useView, ViewOutlet,
  type CollectionViewportHandle, type CommandHandle, type ViewHandle, type ViewRegistry, type ViewSource,
} from "../dist/index.js";

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

describe("useView glue", () => {
  test("reports pending while a reference connects", async () => {
    const reference = fakeReference();
    await render(reference);
    expect(latest?.pending).toBe(true);
    await act(async () => reference.resolveAll());
    expect(latest?.pending).toBe(false);
  });

  test("warns once in development when an inline connect function reconnects on every render", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    try {
      const reference = fakeReference();
      function Inline(): ReactNode {
        // A connect function recreated on every render.
        const view = useView({ connect: () => reference.connect() });
        return createElement("output", null, view.state?.count ?? "connecting");
      }
      await act(async () => {
        root ??= createRoot(document.body.appendChild(document.createElement("div")));
        root.render(createElement(Inline));
      });
      for (let round = 0; round < 6; round++) await act(async () => reference.resolveAll());
      expect(warn).toHaveBeenCalledTimes(1);
      expect(String(warn.mock.calls[0]![0])).toMatch(/connect function is recreated on every render/);
    } finally {
      warn.mockRestore();
      await act(async () => root?.unmount());
      root = undefined;
    }
  });

  test("does not warn for a stable reference", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    try {
      const reference = fakeReference();
      await render({ connect: reference.connect }, true);
      await act(async () => reference.resolveAll());
      for (let round = 0; round < 4; round++) await render({ connect: reference.connect }, true);
      expect(warn).not.toHaveBeenCalled();
    } finally { warn.mockRestore(); }
  });
});

type Page =
  | { readonly kind: "counter"; connect(): Promise<FakeClient> }
  | { readonly kind: "welcome"; connect(): Promise<FakeClient> };

describe("ViewOutlet", () => {
  const Counter = ({ page }: { readonly page: Extract<Page, { kind: "counter" }> }) => createElement("output", null, `counter:${page.kind}`);
  const Welcome = ({ page }: { readonly page: Extract<Page, { kind: "welcome" }> }) => createElement("output", null, `welcome:${page.kind}`);
  const registry = { counter: Counter, welcome: Welcome } satisfies ViewRegistry<Page>;

  async function show(content: Page | undefined, pages: ViewRegistry<Page> = registry) {
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(ViewOutlet<Page>, { content, registry: pages, fallback: createElement("output", null, "empty") }));
    });
  }

  test("renders the registered component for each kind and the fallback without content", async () => {
    const counter: Page = { kind: "counter", connect: fakeReference().connect };
    await show(undefined);
    expect(text()).toBe("empty");
    await show(counter);
    expect(text()).toBe("counter:counter");
    await show({ kind: "welcome", connect: fakeReference().connect });
    expect(text()).toBe("welcome:welcome");
  });

  test("remounts a component when the reference changes and alerts for a missing kind", async () => {
    let mounts = 0;
    const Counting = ({ page }: { readonly page: Extract<Page, { kind: "counter" }> }) => {
      useState(() => ++mounts);
      return createElement("output", null, page.kind);
    };
    const pages = { ...registry, counter: Counting };
    const first: Page = { kind: "counter", connect: fakeReference().connect };
    await show(first, pages);
    await show(first, pages);
    expect(mounts).toBe(1);
    await show({ kind: "counter", connect: fakeReference().connect }, pages);
    expect(mounts).toBe(2);
    await show({ kind: "counter", connect: fakeReference().connect }, { welcome: Welcome } as unknown as ViewRegistry<Page>);
    expect(document.querySelector("[role=alert]")?.textContent).toBe("No web component is registered for counter.");
  });
});

let boundary: Boundary | undefined;
class Boundary extends Component<{ readonly children: ReactNode }, { readonly error?: unknown }> {
  override state: { readonly error?: unknown } = {};
  static getDerivedStateFromError(error: unknown) { return { error }; }
  override componentDidMount() { boundary = this; }
  reset() { this.setState({ error: undefined }); }
  override render(): ReactNode {
    return this.state.error ? createElement("output", null, `failed:${(this.state.error as Error).message}`) : this.props.children;
  }
}

describe("useSuspenseView", () => {
  function SuspenseProbe({ source }: { readonly source: { connect(): Promise<FakeClient> } | FakeClient }): ReactNode {
    const { state } = useSuspenseView(source);
    return createElement("output", null, state.count);
  }
  async function renderSuspense(source: { connect(): Promise<FakeClient> } | FakeClient, strict = false) {
    const tree = createElement(Boundary, null,
      createElement(Suspense, { fallback: createElement("output", null, "suspended") }, createElement(SuspenseProbe, { source })));
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(strict ? createElement(StrictMode, null, tree) : tree);
    });
  }
  const tick = () => new Promise(resolve => setTimeout(resolve, 0));

  test("suspends until the reference connects in StrictMode, renders pushes and disposes after unmount", async () => {
    const reference = fakeReference(4);
    await renderSuspense(reference, true);
    expect(text()).toBe("suspended");
    await act(async () => { reference.resolveAll(); await tick(); });
    expect(text()).toBe("4");
    expect(reference.clients).toHaveLength(1);
    await act(async () => reference.clients[0]!.push(9));
    expect(text()).toBe("9");
    await act(async () => { root?.unmount(); await tick(); });
    root = undefined;
    expect(reference.clients[0]!.disposed).toBe(1);
  });

  test("throws a failed connection to the error boundary and reconnects after a reset", async () => {
    const reference = fakeReference(6);
    reference.failNext();
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      await renderSuspense(reference);
      await act(async () => { reference.resolveAll(); await tick(); });
      expect(text()).toBe("failed:offline");
      // Without a retry the boundary renders the same failure again.
      await act(async () => boundary!.reset());
      expect(text()).toBe("failed:offline");
      retrySuspenseView(reference);
      await act(async () => boundary!.reset());
      expect(text()).toBe("suspended");
      await act(async () => { reference.resolveAll(); await tick(); });
      expect(text()).toBe("6");
    } finally { error.mockRestore(); }
  });

  test("releases a connection whose render never mounted after a delay", async () => {
    const reference = fakeReference();
    await renderSuspense(reference);
    expect(text()).toBe("suspended");
    await act(async () => root?.unmount());
    root = undefined;
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      reference.resolveAll();
      for (let turn = 0; turn < 5; turn++) await Promise.resolve();
      expect(reference.clients).toHaveLength(1);
      vi.advanceTimersByTime(9_999);
      expect(reference.clients[0]!.disposed).toBe(0);
      vi.advanceTimersByTime(1);
      expect(reference.clients[0]!.disposed).toBe(1);
    } finally { vi.useRealTimers(); }
  });

  test("renders a connected client without suspending", async () => {
    const client = fakeClient(2);
    await renderSuspense(client);
    expect(text()).toBe("2");
    await act(async () => root?.unmount());
    root = undefined;
    expect(client.disposed).toBe(0);
  });
});

describe("useCommand", () => {
  test("tracks pending and error and calls the latest command", async () => {
    let handle: CommandHandle<[], number> | undefined;
    const outcomes: (() => void)[] = [];
    function Runner({ value }: { readonly value: number }): ReactNode {
      handle = useCommand(() => new Promise<number>((resolve, reject) => {
        outcomes.push(() => value < 0 ? reject(new Error("negative")) : resolve(value));
      }));
      return createElement("output", null, `${handle.pending}:${handle.error ? (handle.error as Error).message : ""}`);
    }
    const show = (value: number) => act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(Runner, { value }));
    });
    await show(-1);
    let result: Promise<number | undefined> | undefined;
    await act(async () => { result = handle!.run(); });
    expect(text()).toBe("true:");
    await act(async () => outcomes.shift()!());
    expect(await result).toBeUndefined();
    expect(text()).toBe("false:negative");
    await show(5);
    await act(async () => { result = handle!.run(); });
    expect(text()).toBe("true:");
    await act(async () => outcomes.shift()!());
    expect(await result).toBe(5);
    expect(text()).toBe("false:");
  });

  test("keeps a declared failure apart from error; the latest run wins", async () => {
    type Fail = { readonly $case: "titleRequired" } | { readonly $case: "titleTaken"; readonly existingTitle: string };
    const outcomes: ((value: BridgeOutcome<number, Fail> | Error) => void)[] = [];
    let handle: CommandHandle<[], BridgeOutcome<number, Fail>> | undefined;
    let plain: CommandHandle<[], number> | undefined;
    function Runner(): ReactNode {
      handle = useCommand(() => new Promise<BridgeOutcome<number, Fail>>((resolve, reject) => {
        outcomes.push(value => value instanceof Error ? reject(value) : resolve(value));
      }));
      plain = useCommand(() => Promise.resolve(1));
      return createElement("output", null, handle.failure?.$case ?? (handle.error ? "error" : "none"));
    }
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(Runner));
    });
    expectTypeOf(handle!.failure).toEqualTypeOf<Fail | undefined>();
    expectTypeOf(plain!.failure).toEqualTypeOf<undefined>();
    const settle = async (index: number, value: BridgeOutcome<number, Fail> | Error) => act(async () => outcomes[index]!(value));
    let run: Promise<unknown> | undefined;
    await act(async () => { run = handle!.run(); });
    await settle(0, bridgeFailure({ $case: "titleRequired" }));
    await run;
    expect(text()).toBe("titleRequired");
    let stale: Promise<unknown> | undefined;
    await act(async () => { stale = handle!.run(); run = handle!.run(); });
    expect(text()).toBe("none");
    await settle(2, bridgeSuccess(2));
    await settle(1, bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
    await Promise.all([stale, run]);
    expect(text()).toBe("none");
    await act(async () => { run = handle!.run(); });
    await settle(3, new Error("broken"));
    await run;
    expect(text()).toBe("error");
    expect(handle!.failure).toBeUndefined();
    await act(async () => { run = handle!.run(); });
    await settle(4, bridgeFailure({ $case: "titleRequired" }));
    await act(async () => { handle!.reset(); });
    expect(text()).toBe("none");
  });
});

describe("useCollectionViewport", () => {
  test("measures the attached container and follows the row count", async () => {
    let handle: CollectionViewportHandle | undefined;
    function List({ count }: { readonly count: number }): ReactNode {
      handle = useCollectionViewport({ totalCount: count, rowHeight: 20, overscan: 0 });
      return createElement("div", { ref: handle.ref });
    }
    const show = (count: number) => act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(List, { count }));
    });
    await show(100);
    expect(handle?.viewport).toEqual({ start: 0, size: 0, offset: 0, totalSize: 2000 });
    const element = document.querySelector("div div") as HTMLDivElement;
    Object.defineProperty(element, "clientHeight", { configurable: true, value: 200 });
    await show(50);
    expect(handle?.viewport).toEqual({ start: 0, size: 10, offset: 0, totalSize: 1000 });
    element.scrollTop = 400;
    await act(async () => { element.dispatchEvent(new Event("scroll")); await new Promise(resolve => setTimeout(resolve, 50)); });
    expect(handle?.viewport).toEqual({ start: 20, size: 10, offset: 400, totalSize: 1000 });
  });
});

describe("useView source changes", () => {
  test("never returns a released client when a reference returns after null", async () => {
    const reference = fakeReference();
    await render(reference);
    await act(async () => reference.resolveAll());
    const first = reference.clients[0]!;
    expect(latest?.client).toBe(first);
    await render(null);
    expect(first.disposed).toBe(1);
    expect(latest?.client).toBeUndefined();
    await render(reference);
    expect(latest?.client).toBeUndefined();
    expect(latest?.state).toBeUndefined();
    expect(latest?.pending).toBe(true);
    expect(text()).toBe("connecting");
    await act(async () => reference.resolveAll());
    expect(latest?.client).toBe(reference.clients[1]);
    expect(text()).toBe("2");
  });

  test("never returns a released client after a rapid A, B, A change", async () => {
    const a = fakeReference(10);
    const b = fakeReference(20);
    await render(a);
    await act(async () => a.resolveAll());
    const first = a.clients[0]!;
    await render(b);
    await render(a);
    expect(first.disposed).toBe(1);
    expect(latest?.client).toBeUndefined();
    expect(latest?.pending).toBe(true);
    await act(async () => { b.resolveAll(); a.resolveAll(); });
    expect(b.clients[0]!.disposed).toBe(1);
    expect(latest?.client).toBe(a.clients[1]);
    expect(text()).toBe("11");
  });
});
