// @vitest-environment happy-dom

import { flushSync, mount, unmount } from "svelte";
import { afterEach, describe, expect, expectTypeOf, test } from "vitest";
import type { BridgeConnectionState, RunicBridgeClient } from "@runic-artifex/views";
import { useBridgeConnection, type BridgeConnectionHandle, type ViewRegistry } from "../src/views/index.js";
import * as root from "../src/index.js";
import ConnectionOwner from "./fixtures/ConnectionOwner.svelte";
import Counter from "./fixtures/Counter.svelte";
import MissingOutlet from "./fixtures/MissingOutlet.svelte";
import type { Page } from "./fixtures/pages.js";

const host = globalThis as typeof globalThis & { __runicBridge?: RunicBridgeClient };

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

afterEach(() => { delete host.__runicBridge; });

describe("useBridgeConnection", () => {
  test("follows the connection reactively and unsubscribes when its owner is destroyed", () => {
    const fake = hostBridge(true);
    host.__runicBridge = fake.bridge;
    let connection!: BridgeConnectionHandle;
    const seen: BridgeConnectionState[] = [];
    const destroy = $effect.root(() => {
      connection = useBridgeConnection();
      $effect(() => { seen.push(connection.state); });
    });
    expectTypeOf(connection.state).toEqualTypeOf<BridgeConnectionState>();
    expect(connection.state).toBe("connected");
    flushSync();
    expect(fake.listeners).toBe(1);
    fake.set(false);
    flushSync();
    expect(connection.state).toBe("reconnecting");
    fake.set(true);
    flushSync();
    expect(seen).toEqual(["connected", "reconnecting", "connected"]);
    destroy();
    expect(fake.listeners).toBe(0);
    fake.set(false);
    expect(connection.state).toBe("connected");
  });

  test("an owner destroyed before its first flush leaves no subscription; lostAfter is forwarded", () => {
    const fake = hostBridge(false);
    host.__runicBridge = fake.bridge;
    let connection!: BridgeConnectionHandle;
    const destroy = $effect.root(() => { connection = useBridgeConnection({ lostAfter: 0 }); });
    expect(connection.state).toBe("lost");
    destroy();
    expect(fake.listeners).toBe(0);
    expect(() => $effect.root(() => { useBridgeConnection({ lostAfter: -1 }); })).toThrow(RangeError);
  });

  test("a component renders the state and stops observing when unmounted", async () => {
    const fake = hostBridge(true);
    host.__runicBridge = fake.bridge;
    const target = document.createElement("div");
    const component = mount(ConnectionOwner, { target });
    flushSync();
    expect(target.textContent).toBe("connected");
    fake.set(false);
    flushSync();
    expect(target.textContent).toBe("reconnecting");
    await unmount(component);
    expect(fake.listeners).toBe(0);
  });

  test("is a root export (0.11) and the root keeps every earlier export", () => {
    expect(Object.keys(root).sort()).toEqual([
      "FieldError", "ViewOutlet", "onBridgeConnection", "preloadView", "useBridgeConnection", "useCollectionViewport", "useCommand", "useField",
      "useLatestOperation", "useMenuBar", "useOperation", "useSuspenseView", "useTextField", "useView",
    ]);
  });
});

describe("ViewOutlet missing", () => {
  test("renders the missing snippet for a kind without a component instead of the English alert", () => {
    const target = document.createElement("div");
    const component = mount(MissingOutlet, {
      target,
      props: { content: { kind: "welcome", connect: async () => undefined }, registry: { counter: Counter } as unknown as ViewRegistry<Page> },
    });
    flushSync();
    expect(target.textContent).toBe("Keine Ansicht für welcome");
    expect(target.querySelector("[role=alert]")).toBeNull();
    unmount(component);
  });
});
