import type { ViewSource } from "@runic-artifex/views";
import * as React from "react";
import { Component, createElement, StrictMode, Suspense, useEffect, useState, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { renderToString } from "react-dom/server";
import { afterEach, describe, expect, expectTypeOf, test, vi } from "vitest";
import {
  BridgeError, bridgeFailure, bridgeSuccess, type BridgeConnectionState, type BridgeOperation, type BridgeOperationStatus, type BridgeOutcome,
  type MenuBarMenu, type MenuBarRequest, type MenuBarState, type RunicBridgeClient,
} from "@runic-artifex/views";
import { installMockMenuBar } from "@runic-artifex/views/mock";
import {
  retrySuspenseView, useBridgeConnection, useCollectionViewport, useCommand, useLatestOperation, useMenuBar, useOperation, useSuspenseView,
  useView, ViewOutlet, type BridgeConnectionHandle, type MenuBarHandle, type UseMenuBarOptions, type CollectionViewportHandle, type CommandHandle, type LatestOperationHandle, type OperationHandle, type SuspenseViewOptions,
  type ViewHandle, type ViewRegistry,
} from "../dist/index.js";
import * as root_ from "../dist/index.js";
import { onBridgeConnection as viewsOnBridgeConnection, type BridgeConnectionOptions as ViewsBridgeConnectionOptions, type BridgeConnectionState as ViewsBridgeConnectionState } from "@runic-artifex/views";

// React 18.3.0, the lowest supported React, exports act only as unstable_act.
const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;

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
  latest = useView(source, { keepAlive: 0 });
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

describe("interactions", () => {
  type Prompt = { readonly itemName: string };
  type Handler = (prompt: Prompt, context: { readonly signal: AbortSignal }) => unknown;

  // Mirrors a generated client's interaction surface: handle registers the View's
  // answer and returns the function that removes it.
  function interactiveClient() {
    const handlers = new Set<Handler>();
    return Object.assign(fakeClient(1), {
      handlers,
      interactions: {
        confirmDiscard: {
          handle(handler: (prompt: Prompt, context: { readonly signal: AbortSignal }) => boolean | Promise<boolean>) {
            handlers.add(handler);
            return () => { handlers.delete(handler); };
          },
        },
      },
    });
  }

  test("the README's effect answers while the page is mounted and is removed when it unmounts", async () => {
    const page = interactiveClient();
    function EditPage({ page }: { readonly page: ReturnType<typeof interactiveClient> }): ReactNode {
      const { client } = useView(page);
      useEffect(() => client?.interactions.confirmDiscard.handle(
        async (prompt, { signal }) => !signal.aborted && prompt.itemName === "Groceries",
      ), [client]);
      return null;
    }
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(StrictMode, null, createElement(EditPage, { page })));
    });
    expect(page.handlers.size).toBe(1);
    const [handler] = page.handlers;
    expect(await handler!({ itemName: "Groceries" }, { signal: new AbortController().signal })).toBe(true);
    await act(async () => root!.render(null));
    expect(page.handlers.size).toBe(0);
  });
});

describe("useView options", () => {
  test("release replaces dispose for a client the hook connected", async () => {
    const reference = fakeReference();
    const released: unknown[] = [];
    function Releasing(): ReactNode {
      useView(reference, { release: client => released.push(client), keepAlive: 0 });
      return null;
    }
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(Releasing));
    });
    await act(async () => reference.resolveAll());
    await act(async () => root?.unmount());
    root = undefined;
    expect(released).toEqual(reference.clients);
    expect(reference.clients.every(client => client.disposed === 0)).toBe(true);
  });

  test("the hooks that graduated in 0.10 are root exports", () => {
    for (const name of ["useSuspenseView", "retrySuspenseView", "useCollectionViewport", "useLatestOperation"])
      expect(typeof (root_ as Record<string, unknown>)[name]).toBe("function");
  });

  test("the root entry keeps every earlier export and adds useBridgeConnection and useMenuBar (0.11), useField and FieldError (0.14)", () => {
    expect(Object.keys(root_).sort()).toEqual([
      "FieldError", "ViewOutlet", "onBridgeConnection", "retrySuspenseView", "useBridgeConnection", "useCollectionViewport", "useCommand", "useField", "useLatestOperation",
      "useMenuBar", "useOperation", "useSuspenseView", "useTextField", "useView",
    ]);
    // The connection API that useBridgeConnection wraps is re-exported, as the menu-bar types are (0.11).
    expect(root_.onBridgeConnection).toBe(viewsOnBridgeConnection);
    expectTypeOf<root_.BridgeConnectionState>().toEqualTypeOf<ViewsBridgeConnectionState>();
    expectTypeOf<root_.BridgeConnectionOptions>().toEqualTypeOf<ViewsBridgeConnectionOptions>();
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

  test("renders missing for a kind without a component instead of the English alert", async () => {
    const content: Page = { kind: "counter", connect: fakeReference().connect };
    const missing = (page: Page) => createElement("output", null, `Keine Ansicht für ${page.kind}`);
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(ViewOutlet<Page>, { content, registry: { welcome: Welcome } as unknown as ViewRegistry<Page>, missing }));
    });
    expect(text()).toBe("Keine Ansicht für counter");
    expect(document.querySelector("[role=alert]")).toBeNull();
    await show(content);
    expect(text()).toBe("counter:counter");
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
  type Source = { connect(): Promise<FakeClient> } | FakeClient;
  function SuspenseProbe({ source, options }: { readonly source: Source; readonly options?: SuspenseViewOptions }): ReactNode {
    const { state } = useSuspenseView(source, options);
    return createElement("output", null, state.count);
  }
  async function renderSuspense(source: Source, strict = false, ...options: (SuspenseViewOptions | undefined)[]) {
    const probes = (options.length ? options : [undefined]).map((option, index) =>
      createElement(SuspenseProbe, { key: index, source, ...(option ? { options: option } : {}) }));
    const tree = createElement(Boundary, null,
      createElement(Suspense, { fallback: createElement("output", null, "suspended") }, ...probes));
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

  // Renders, abandons the render before it mounts, then connects with fake timers.
  async function abandonedConnection(...options: SuspenseViewOptions[]) {
    const reference = fakeReference();
    await renderSuspense(reference, false, ...options);
    expect(text()).toBe("suspended");
    await act(async () => root?.unmount());
    root = undefined;
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    reference.resolveAll();
    for (let turn = 0; turn < 5; turn++) await Promise.resolve();
    expect(reference.clients).toHaveLength(1);
    return reference.clients[0]!;
  }

  test("releaseTimeout sets how long an unclaimed connection stays cached", async () => {
    expectTypeOf(useSuspenseView<FakeClient>).parameter(1).toEqualTypeOf<SuspenseViewOptions | undefined>();
    try {
      const client = await abandonedConnection({ releaseTimeout: 50 });
      vi.advanceTimersByTime(49);
      expect(client.disposed).toBe(0);
      vi.advanceTimersByTime(1);
      expect(client.disposed).toBe(1);
    } finally { vi.useRealTimers(); }
  });

  test("components that share a connect function keep the largest releaseTimeout", async () => {
    try {
      const client = await abandonedConnection({ releaseTimeout: 20 }, { releaseTimeout: 100 });
      vi.advanceTimersByTime(99);
      expect(client.disposed).toBe(0);
      vi.advanceTimersByTime(1);
      expect(client.disposed).toBe(1);
    } finally { vi.useRealTimers(); }
  });

  test("releaseTimeout Infinity sets no timer", async () => {
    try {
      const client = await abandonedConnection({ releaseTimeout: Infinity });
      expect(vi.getTimerCount()).toBe(0);
      vi.advanceTimersByTime(2 ** 31);
      expect(client.disposed).toBe(0);
    } finally { vi.useRealTimers(); }
  });

  test("an unretried failure is forgotten after releaseTimeout", async () => {
    const reference = fakeReference(3);
    reference.failNext();
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      await renderSuspense(reference, false, { releaseTimeout: 30 });
      await act(async () => { reference.resolveAll(); await new Promise(resolve => setTimeout(resolve, 0)); });
      expect(text()).toBe("failed:offline");
      await new Promise(resolve => setTimeout(resolve, 60));
      // The failure was forgotten, so resetting the boundary connects again without retrySuspenseView.
      await act(async () => boundary!.reset());
      expect(text()).toBe("suspended");
      await act(async () => { reference.resolveAll(); await new Promise(resolve => setTimeout(resolve, 0)); });
      expect(text()).toBe("3");
    } finally { error.mockRestore(); }
  });

  test("an invalid releaseTimeout throws a RangeError", async () => {
    const reference = fakeReference();
    const caught: unknown[] = [];
    function Invalid({ releaseTimeout }: { readonly releaseTimeout: number }): ReactNode {
      try { useSuspenseView(reference, { releaseTimeout }); } catch (error) { caught.push(error); }
      return null;
    }
    for (const releaseTimeout of [0, -1, Number.NaN, 2 ** 31, 1.5, -Infinity]) {
      caught.length = 0;
      await act(async () => {
        root ??= createRoot(document.body.appendChild(document.createElement("div")));
        root.render(createElement(Invalid, { releaseTimeout }));
      });
      expect(caught[0], String(releaseTimeout)).toBeInstanceOf(RangeError);
    }
    expect(reference.clients).toHaveLength(0);
    expect(() => retrySuspenseView(reference)).not.toThrow();
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

  test("infers the result of a command that returns one of several command promises", async () => {
    type State = { readonly title: string };
    type SaveFailure = { readonly $case: "titleRequired" };
    type PublishFailure = { readonly $case: "offline" };
    const client = {
      save: () => Promise.resolve<BridgeOutcome<State, SaveFailure>>(bridgeFailure({ $case: "titleRequired" })),
      publish: () => Promise.resolve<BridgeOutcome<State, PublishFailure>>(bridgeSuccess({ title: "Published" })),
      discard: () => Promise.resolve<State>({ title: "Discarded" }),
    };
    const useEditorCommand = () => useCommand((name: "save" | "publish" | "discard") =>
      name === "save" ? client.save() : name === "publish" ? client.publish() : client.discard());
    const useOptionalCommand = () => useCommand((name: "save" | "discard") => name === "save" ? undefined : client.discard());
    let handle: ReturnType<typeof useEditorCommand> | undefined;
    function Runner(): ReactNode {
      handle = useEditorCommand();
      useOptionalCommand();
      return createElement("output", null, handle.failure?.$case ?? "none");
    }
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(Runner));
    });
    expectTypeOf(handle!.run).returns.resolves.toEqualTypeOf<BridgeOutcome<State, SaveFailure> | BridgeOutcome<State, PublishFailure> | State | undefined>();
    expectTypeOf(handle!.failure).toEqualTypeOf<SaveFailure | PublishFailure | undefined>();
    expectTypeOf<ReturnType<typeof useOptionalCommand>["failure"]>().toEqualTypeOf<undefined>();
    await act(async () => { await handle!.run("save"); });
    expect(text()).toBe("titleRequired");
    let discarded: unknown;
    await act(async () => { discarded = await handle!.run("discard"); });
    expect(discarded).toEqual({ title: "Discarded" });
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

function fakeOperation(requestId: string) {
  let finish!: (status: BridgeOperationStatus<number>) => void;
  const terminal = new Promise<BridgeOperationStatus<number>>(resolve => { finish = resolve; });
  let cancels = 0;
  const operation: BridgeOperation<number> = {
    requestId,
    status: () => terminal,
    completion: terminal,
    wait: () => terminal,
    subscribe: () => () => {},
    async cancel() { cancels++; return { contract: "test", requestId, kind: "cancellation-requested" }; },
    async outcome() {
      const status = await terminal;
      if (status.kind === "succeeded") return bridgeSuccess(status.result as number);
      throw new BridgeError(status.kind === "cancelled" ? "cancelled" : status.kind === "timed-out" ? "timeout" : "failed", status.kind);
    },
  };
  return { operation, finish, cancels: () => cancels };
}

// D40 b: the hooks dispose their controllers on unmount, and StrictMode's simulated unmount keeps them working.
describe("controller lifetime", () => {
  test("useOperation disposes on unmount: an in-flight run settles and completion is not awaited", async () => {
    let handle: OperationHandle<[], number> | undefined;
    const current = fakeOperation("lifetime");
    let completions = 0;
    function Runner(): ReactNode {
      handle = useOperation(() => current.operation, { waitForCompletion: () => { completions++; } });
      return createElement("output", null, `${handle.pending}`);
    }
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(StrictMode, null, createElement(Runner)));
    });
    let result: Promise<unknown> | undefined;
    await act(async () => { result = handle!.run(); });
    expect(text()).toBe("true");
    await act(async () => root?.unmount());
    root = undefined;
    expect(await result).toBeUndefined();
    current.finish({ contract: "test", requestId: "lifetime", kind: "succeeded", result: 1 });
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(completions).toBe(0);
    expect(current.cancels()).toBe(0);
  });

  test("useCommand keeps tracking runs after StrictMode's extra mount", async () => {
    let handle: CommandHandle<[], number> | undefined;
    let finish: ((value: number) => void) | undefined;
    function Runner(): ReactNode {
      handle = useCommand(() => new Promise<number>(resolve => { finish = resolve; }));
      return createElement("output", null, `${handle.pending}`);
    }
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(StrictMode, null, createElement(Runner)));
    });
    let result: Promise<number | undefined> | undefined;
    await act(async () => { result = handle!.run(); });
    expect(text()).toBe("true");
    await act(async () => finish!(3));
    expect(await result).toBe(3);
    expect(text()).toBe("false");
  });
});

describe("useOperation", () => {
  test("Cancel ends in a cancelled state without an error; failed and timed-out runs are errors", async () => {
    let handle: OperationHandle<[], number> | undefined;
    let current = fakeOperation("first");
    function Runner(): ReactNode {
      handle = useOperation(() => current.operation);
      return createElement("output", null, `${handle.pending}:${handle.cancelled}:${handle.error ? (handle.error as BridgeError).kind : ""}`);
    }
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(StrictMode, null, createElement(Runner)));
    });
    expectTypeOf(handle!.cancelled).toEqualTypeOf<boolean>();
    expectTypeOf(handle!.run).returns.resolves.toEqualTypeOf<BridgeOutcome<number, never> | undefined>();
    let result: Promise<unknown> | undefined;
    await act(async () => { result = handle!.run(); });
    expect(text()).toBe("true:false:");
    await act(async () => { await handle!.cancel(); });
    expect(current.cancels()).toBe(1);
    expect(handle!.cancellationRequested).toBe(true);
    await act(async () => { current.finish({ contract: "test", requestId: "first", kind: "cancelled" }); await result; });
    expect(await result).toBeUndefined();
    expect(text()).toBe("false:true:");
    expect(handle!.status?.kind).toBe("cancelled");

    for (const [status, kind] of [
      [{ contract: "test", requestId: "next", kind: "failed", error: { kind: "failed", message: "boom" } }, "failed"],
      [{ contract: "test", requestId: "next", kind: "timed-out", cancellation: "cancellation-requested" }, "timeout"],
    ] as const) {
      current = fakeOperation("next");
      await act(async () => { result = handle!.run(); });
      expect(text()).toBe("true:false:");
      await act(async () => { current.finish(status); await result; });
      expect(text()).toBe(`false:false:${kind}`);
    }
    await act(async () => { handle!.reset(); });
    expect(text()).toBe("false:false:");
  });
});

describe("useLatestOperation", () => {
  async function mount(Runner: () => ReactNode) {
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(StrictMode, null, createElement(Runner)));
    });
  }

  test("admits only the newest selection after the previous read finishes, in StrictMode", async () => {
    let handle: LatestOperationHandle<number> | undefined;
    function Runner(): ReactNode {
      handle = useLatestOperation<number>();
      return createElement("output", null, `${handle.pending}:${handle.outcome?.ok ? handle.outcome.value : ""}`);
    }
    await mount(Runner);
    expectTypeOf(handle!.blocked).toEqualTypeOf<boolean>();
    expectTypeOf(handle!.run).returns.resolves.toEqualTypeOf<BridgeOutcome<number, never> | undefined>();
    expectTypeOf(handle!.clear).parameter(0).toEqualTypeOf<"cancel" | "replace" | undefined>();
    const first = fakeOperation("first");
    const second = fakeOperation("second");
    const third = fakeOperation("third");
    const started: string[] = [];
    const intent = (current: ReturnType<typeof fakeOperation>) =>
      ({ start: () => { started.push(current.operation.requestId); return current.operation; } });
    let a: Promise<unknown> | undefined, b: Promise<unknown> | undefined, c: Promise<unknown> | undefined;
    await act(async () => { a = handle!.run(intent(first)); });
    expect(text()).toBe("true:");
    await act(async () => { b = handle!.run(intent(second)); c = handle!.run(intent(third)); });
    // The active read is cancelled once, the middle intent is dropped, and nothing else starts yet.
    expect(first.cancels()).toBe(1);
    expect(started).toEqual(["first"]);
    expect(await b).toBeUndefined();
    await act(async () => { first.finish({ contract: "test", requestId: "first", kind: "cancelled" }); await a; });
    expect(started).toEqual(["first", "third"]);
    await act(async () => { third.finish({ contract: "test", requestId: "third", kind: "succeeded", result: 3 }); await c; });
    expect(await c).toEqual(bridgeSuccess(3));
    expect(text()).toBe("false:3");
    expect(second.cancels()).toBe(0);
  });

  test("blocks after uncertain admission until clear(\"replace\")", async () => {
    let handle: LatestOperationHandle<number> | undefined;
    function Runner(): ReactNode {
      handle = useLatestOperation<number>();
      return createElement("output", null, `${handle.blocked}`);
    }
    await mount(Runner);
    await act(async () => { await handle!.run({ start: () => Promise.reject(new Error("lost Start reply")) }); });
    expect(text()).toBe("true");
    expect((handle!.error as Error).message).toBe("lost Start reply");
    const next = fakeOperation("next");
    let started = 0;
    await act(async () => { expect(await handle!.run({ start: () => { started++; return next.operation; } })).toBeUndefined(); });
    expect(started).toBe(0);
    await act(async () => { handle!.clear("replace"); });
    expect(text()).toBe("false");
    let result: Promise<unknown> | undefined;
    await act(async () => { result = handle!.run({ start: () => { started++; return next.operation; } }); });
    await act(async () => { next.finish({ contract: "test", requestId: "next", kind: "succeeded", result: 1 }); await result; });
    expect(started).toBe(1);
    expect(await result).toEqual(bridgeSuccess(1));
  });

  test("unmounting settles observation without cancelling accepted work", async () => {
    let handle: LatestOperationHandle<number> | undefined;
    function Runner(): ReactNode {
      handle = useLatestOperation<number>();
      return createElement("output", null, `${handle.pending}`);
    }
    await mount(Runner);
    const current = fakeOperation("lifetime");
    let completions = 0;
    let result: Promise<unknown> | undefined;
    await act(async () => {
      result = handle!.run({ start: () => current.operation, waitForCompletion: () => { completions++; } });
    });
    expect(text()).toBe("true");
    await act(async () => root?.unmount());
    root = undefined;
    expect(await result).toBeUndefined();
    current.finish({ contract: "test", requestId: "lifetime", kind: "succeeded", result: 1 });
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(completions).toBe(0);
    expect(current.cancels()).toBe(0);
  });
});

// A host script's Bridge: `onConnectionChange` reports the current state, then each change.
function hostBridge(initial: boolean) {
  let connected = initial;
  const listeners = new Set<(connected: boolean) => void>();
  const bridge: RunicBridgeClient = {
    isConnected: () => connected,
    request: () => Promise.reject(new Error("not used")),
    onMessage: () => () => {},
    onConnectionChange(listener) {
      listeners.add(listener);
      listener(connected);
      return () => { listeners.delete(listener); };
    },
  };
  return {
    bridge,
    get listeners() { return listeners.size; },
    set(value: boolean) { connected = value; for (const listener of [...listeners]) listener(value); },
  };
}

describe("useBridgeConnection", () => {
  const host = globalThis as typeof globalThis & { __runicBridge?: RunicBridgeClient };
  afterEach(() => { delete host.__runicBridge; });

  test("renders the connection state in StrictMode and unsubscribes on unmount", async () => {
    const fake = hostBridge(true);
    host.__runicBridge = fake.bridge;
    const rendered: BridgeConnectionState[] = [];
    function Status(): ReactNode {
      const connection = useBridgeConnection({ lostAfter: 0 });
      expectTypeOf(connection).toEqualTypeOf<BridgeConnectionHandle>();
      rendered.push(connection.state);
      return createElement("output", null, connection.state);
    }
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(StrictMode, null, createElement(Status)));
    });
    // The first render already shows the state; StrictMode's extra mount keeps one subscription.
    expect(rendered[0]).toBe("connected");
    expect(text()).toBe("connected");
    expect(fake.listeners).toBe(1);
    await act(async () => fake.set(false));
    expect(text()).toBe("lost");
    await act(async () => fake.set(true));
    expect(text()).toBe("connected");
    await act(async () => root?.unmount());
    root = undefined;
    expect(fake.listeners).toBe(0);
  });

  test("is connecting without a Bridge and reconnecting after a drop", async () => {
    function Status(): ReactNode { return createElement("output", null, useBridgeConnection().state); }
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(createElement(Status));
    });
    expect(text()).toBe("connecting");
    const fake = hostBridge(true);
    host.__runicBridge = fake.bridge;
    // The subscription polls every 50 ms until a host script installs the Bridge.
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 120)); });
    expect(text()).toBe("connected");
    await act(async () => fake.set(false));
    expect(text()).toBe("reconnecting");
  });
});

// A menu bar as Runic Desktop reports it on Linux (specs/desktop/menu-bar-page.md), served by the mock host routes.
// The lowest-peer run copies only this package, so the spec's fixture is not used here.
const menuBarFixture: { readonly menus: readonly MenuBarMenu[] } = {
  menus: [
    { kind: "menu", key: "1m", label: "File", accessKey: "F", accessKeyIndex: 0, role: "file", enabled: true, entries: [
      { kind: "item", key: "1m/0i", label: "New", accessKey: "N", accessKeyIndex: 0, commandId: "file.new", enabled: true,
        shortcut: "Primary+N", shortcutText: "Ctrl+N", ariaKeyShortcuts: "Control+N" },
      { kind: "item", key: "1m/1i", label: "Save", accessKey: "S", accessKeyIndex: 0, commandId: "file.save", enabled: true },
      { kind: "separator", key: "1m/2s" },
    ] },
    { kind: "menu", key: "0m", label: "Edit", accessKey: "E", accessKeyIndex: 0, role: "none", enabled: true, entries: [
      { kind: "item", key: "0m/0i", label: "Find", accessKey: "F", accessKeyIndex: 0, commandId: "edit.find", enabled: false },
    ] },
  ],
};

function keyDown(init: KeyboardEventInit) {
  window.dispatchEvent(new KeyboardEvent("keydown", { cancelable: true, ...init }));
}

describe("useMenuBar", () => {
  const host = globalThis as typeof globalThis & { __runicBridge?: RunicBridgeClient };
  afterEach(() => { delete host.__runicBridge; });
  let bar: MenuBarHandle | undefined;
  const rendered: MenuBarState[] = [];
  function MenuBar(options: UseMenuBarOptions): ReactNode {
    bar = useMenuBar(options);
    expectTypeOf(bar).toEqualTypeOf<MenuBarHandle>();
    rendered.push(bar.state);
    return createElement("output", null, `${bar.state.status} ${bar.state.menus.map(menu => menu.label).join(",")}`);
  }
  async function mount(options: UseMenuBarOptions, strict = true) {
    const element = createElement(MenuBar, options);
    await act(async () => {
      root ??= createRoot(document.body.appendChild(document.createElement("div")));
      root.render(strict ? createElement(StrictMode, null, element) : element);
    });
  }
  async function until(condition: () => boolean) {
    for (let round = 0; !condition(); round++) {
      if (round > 200) throw new Error("The condition was not met.");
      await act(async () => { await new Promise(resolve => setTimeout(resolve, 2)); });
    }
  }

  test("renders the host's menus in StrictMode with one controller, and disposes it on unmount", async () => {
    const mock = installMockMenuBar(menuBarFixture.menus, { platform: "linux" });
    rendered.length = 0;
    await mount({});
    expect(rendered[0]?.status).toBe("connecting");
    await until(() => text() === "ready File,Edit");
    expect(bar!.state.menus).toEqual(menuBarFixture.menus);
    expect(bar!.state.shortcuts).toBe("page");
    // StrictMode's extra mount disposed its controller: a shortcut runs once.
    await act(async () => keyDown({ key: "n", code: "KeyN", ctrlKey: true }));
    await until(() => mock.invocations.length > 0);
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)); });
    expect(mock.invocations.map(item => item.commandId)).toEqual(["file.new"]);
    // A pushed change renders.
    await act(async () => mock.update({ menus: menuBarFixture.menus.slice(0, 1) }));
    await until(() => text() === "ready File");
    await act(async () => root?.unmount());
    root = undefined;
    keyDown({ key: "n", code: "KeyN", ctrlKey: true });
    await new Promise(resolve => setTimeout(resolve, 10));
    expect(mock.invocations).toHaveLength(1);
  });

  test("invoke reaches the host and onRequest calls the latest function", async () => {
    const mock = installMockMenuBar(menuBarFixture.menus, { platform: "linux" });
    const first: MenuBarRequest[] = [];
    const second: MenuBarRequest[] = [];
    await mount({ onRequest: request => first.push(request) });
    await until(() => bar?.state.status === "ready");
    const file = bar!.state.menus[0]!;
    const save = file.entries[1]!;
    if (save.kind !== "item") throw new Error("Save is an item.");
    expect(await act(async () => bar!.invoke(save))).toBe("executed");
    expect(mock.invocations.map(item => item.key)).toEqual([save.key]);
    await mount({ onRequest: request => second.push(request) });
    await act(async () => keyDown({ key: "F10", code: "F10" }));
    await act(async () => keyDown({ key: "f", code: "KeyF", altKey: true }));
    expect(first).toEqual([]);
    expect(second).toEqual([{ kind: "focus" }, { kind: "open", menu: file }]);
    expect(bar!.state.accessKeyHints).toBe(true);
  });

  test("shortcuts: \"app\" holds the lease while mounted; a changed option creates a new controller", async () => {
    const mock = installMockMenuBar(menuBarFixture.menus, { platform: "linux" });
    await mount({ shortcuts: "app" }, false);
    expect(bar!.state.shortcuts).toBe("app");
    await until(() => bar?.state.status === "ready" && mock.leased);
    // The page's own key handling: the hook passes the event to the mounted controller.
    const event = new KeyboardEvent("keydown", { key: "n", code: "KeyN", ctrlKey: true, cancelable: true });
    expect(bar!.handleKeyDown(event)).toBe(true);
    expect(event.defaultPrevented).toBe(true);
    await until(() => mock.invocations.length === 1);
    await mount({ shortcuts: "runic" }, false);
    await until(() => bar?.state.status === "ready" && !mock.leased);
    expect(bar!.state.shortcuts).toBe("page");
    await mount({ shortcuts: "app" }, false);
    await until(() => mock.leased);
    await act(async () => root?.unmount());
    root = undefined;
    await until(() => !mock.leased);
  });

  test("renders connecting on the server, where no controller is created", () => {
    const states: MenuBarState[] = [];
    function Server(): ReactNode {
      const handle = useMenuBar({ shortcuts: "app" });
      states.push(handle.state);
      expect(handle.handleKeyDown(new KeyboardEvent("keydown", { key: "n", ctrlKey: true }))).toBe(false);
      return createElement("output", null, handle.state.status);
    }
    expect(renderToString(createElement(Server))).toBe("<output>connecting</output>");
    expect(states[0]).toMatchObject({ status: "connecting", placement: "none", shortcuts: "app", accessKeyHints: false, revision: 0, menus: [] });
  });

  test("invoke before the effect ran rejects as disconnected", async () => {
    let handle: MenuBarHandle | undefined;
    function Server(): ReactNode { handle = useMenuBar(); return null; }
    renderToString(createElement(Server));
    await expect(handle!.invoke({ kind: "item", key: "1m/0i", label: "New", accessKeyIndex: 0, commandId: "file.new", enabled: true }))
      .rejects.toMatchObject({ name: "BridgeError", kind: "disconnected" });
  });
});
