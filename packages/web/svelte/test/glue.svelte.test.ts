// @vitest-environment happy-dom

import { flushSync, mount, unmount } from "svelte";
import { describe, expect, test } from "vitest";
import { useCollectionViewport, useCommand, useView, type ViewRegistry } from "../src/views/index.js";
import Counter from "./fixtures/Counter.svelte";
import Outlet from "./fixtures/Outlet.svelte";
import type { Page } from "./fixtures/pages.js";
import Welcome from "./fixtures/Welcome.svelte";

const settle = async () => { await Promise.resolve(); await Promise.resolve(); flushSync(); };

describe("useView glue", () => {
  test("reports pending while a reference connects", async () => {
    let resolve!: (client: { snapshot: number; subscribe(listener: (state: number) => void): () => void; dispose(): void }) => void;
    const connect = () => new Promise<Parameters<typeof resolve>[0]>(done => { resolve = done; });
    let view!: ReturnType<typeof useView<Parameters<typeof resolve>[0]>>;
    const destroy = $effect.root(() => { view = useView(() => ({ connect })); });
    expect(view.pending).toBe(true);
    flushSync();
    expect(view.pending).toBe(true);
    resolve({ snapshot: 1, subscribe: listener => { listener(1); return () => {}; }, dispose() {} });
    await settle();
    expect(view.pending).toBe(false);
    expect(view.state).toBe(1);
    destroy();
  });
});

describe("ViewOutlet", () => {
  test("renders the registered component, remounts on a new reference and alerts for a missing kind", () => {
    const registry = { counter: Counter, welcome: Welcome } satisfies ViewRegistry<Page>;
    const target = document.createElement("div");
    const props = $state<{ content: Page | undefined; registry: ViewRegistry<Page> }>({ content: undefined, registry });
    const component = mount(Outlet, { target, props });
    flushSync();
    expect(target.textContent).toBe("empty");
    props.content = { kind: "counter", connect: async () => undefined };
    flushSync();
    expect(target.textContent).toBe("counter:counter");
    const first = target.querySelector("output");
    props.content = { kind: "counter", connect: async () => undefined };
    flushSync();
    expect(target.querySelector("output")).not.toBe(first);
    props.content = { kind: "welcome", connect: async () => undefined };
    flushSync();
    expect(target.textContent).toBe("welcome:welcome");
    props.registry = { counter: Counter } as unknown as ViewRegistry<Page>;
    flushSync();
    expect(target.querySelector("[role=alert]")?.textContent).toBe("No web component is registered for welcome.");
    unmount(component);
  });
});

describe("useCommand", () => {
  test("exposes pending and error as reactive properties and never rejects", async () => {
    const outcomes: (() => void)[] = [];
    const command = useCommand((fail: boolean) => new Promise<number>((resolve, reject) => {
      outcomes.push(() => fail ? reject(new Error("rejected")) : resolve(1));
    }));
    const failed = command.run(true);
    expect(command.pending).toBe(true);
    outcomes.shift()!();
    expect(await failed).toBeUndefined();
    expect(command.pending).toBe(false);
    expect((command.error as Error).message).toBe("rejected");
    const succeeded = command.run(false);
    expect(command.error).toBeUndefined();
    outcomes.shift()!();
    expect(await succeeded).toBe(1);
  });
});

describe("useCollectionViewport", () => {
  test("measures the attached container and follows the options getter", () => {
    let count = $state(100);
    let list!: ReturnType<typeof useCollectionViewport>;
    const destroy = $effect.root(() => { list = useCollectionViewport(() => ({ totalCount: count, rowHeight: 20, overscan: 0 })); });
    flushSync();
    const element = document.createElement("div");
    Object.defineProperty(element, "clientHeight", { configurable: true, value: 200 });
    const detach = list.attach(element);
    expect(list.viewport).toEqual({ start: 0, size: 10, offset: 0, totalSize: 2000 });
    count = 5;
    flushSync();
    expect(list.viewport).toEqual({ start: 0, size: 5, offset: 0, totalSize: 100 });
    if (typeof detach === "function") detach();
    destroy();
  });
});
