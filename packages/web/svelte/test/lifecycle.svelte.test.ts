// @vitest-environment happy-dom

import { flushSync, mount, unmount } from "svelte";
import { beforeEach, describe, expect, test, vi } from "vitest";
import { useCollectionViewport, useCommand, useView, type CommandHandle } from "../src/views/index.js";
import LifecycleOwner from "./fixtures/LifecycleOwner.svelte";

// Records which framework-neutral controllers the helpers dispose.
const disposed = vi.hoisted(() => [] as string[]);
vi.mock("@runic-artifex/views", async importOriginal => {
  const actual = await importOriginal<typeof import("@runic-artifex/views")>();
  const tracked = <T extends (...args: never[]) => { dispose(): void }>(name: string, create: T): T =>
    ((...args: Parameters<T>) => {
      const controller = create(...args);
      const dispose = controller.dispose.bind(controller);
      controller.dispose = () => { disposed.push(name); dispose(); };
      return controller;
    }) as T;
  return {
    ...actual,
    createViewController: tracked("view", actual.createViewController),
    createCommandController: tracked("command", actual.createCommandController),
    createCollectionViewportController: tracked("viewport", actual.createCollectionViewportController),
  };
});

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}
const never = { connect: () => new Promise<never>(() => {}) };

describe("helper lifecycle (svelte-10)", () => {
  beforeEach(() => { disposed.length = 0; });

  test("an owner destroyed before its first effect flush disposes every controller", () => {
    const destroy = $effect.root(() => {
      useView(() => never);
      useCollectionViewport(() => ({ totalCount: 10, rowHeight: 20 }));
      useCommand(() => 1);
    });
    destroy();
    expect([...disposed].sort()).toEqual(["command", "view", "viewport"]);
  });

  test("a component unmounted before its first effect flush disposes its controllers", async () => {
    const target = document.createElement("div");
    // mount() runs component initialization but defers user effects until a flush.
    const component = mount(LifecycleOwner, { target, props: { source: never, command: () => Promise.resolve(1) } });
    await unmount(component);
    expect([...disposed].sort()).toEqual(["command", "view", "viewport"]);
  });

  test("useCommand stops publishing after its owner is destroyed; a run in flight still settles", async () => {
    const result = deferred<number>();
    let command!: CommandHandle<[], number>;
    const destroy = $effect.root(() => { command = useCommand(() => result.promise); });
    flushSync();
    const run = command.run();
    expect(command.pending).toBe(true);
    destroy();
    expect(disposed).toEqual(["command"]);
    result.resolve(2);
    expect(await run).toBe(2);
    expect(command.pending).toBe(true);
  });
});
