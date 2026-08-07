import { describe, expect, test, vi } from "vitest";
import { createSvelteApplicationBridge } from "../src/bridge.svelte.js";

type Command = { _tag: "Increment"; step: number };
type Receipt = { _tag: "Incremented"; snapshot: Snapshot };
type Event = { _tag: "Changed"; snapshot: Snapshot };
type Snapshot = { count: number; revision: number };

function controller() {
  let event: ((value: Event) => void) | undefined;
  const dispose = vi.fn(async () => undefined);
  const uiReady = vi.fn(async () => undefined);
  const uiRendered = vi.fn(async () => undefined);
  return {
    value: {
      initialize: async () => ({ count: 0, revision: 0 }),
      dispatch: async (command: Command) => ({
        _tag: "Incremented" as const,
        snapshot: { count: command.step, revision: 1 },
      }),
      cancel: async () => undefined,
      reconnect: async () => ({ count: 2, revision: 2 }),
      uiReady,
      uiRendered,
      subscribe: (next: (value: Event) => void) => {
        event = next;
        return () => { event = undefined; };
      },
      dispose,
    },
    emit: (value: Event) => event?.(value),
    dispose,
    uiReady,
    uiRendered,
  };
}

describe("SvelteApplicationBridge", () => {
  test("owns initialization, domain events, reconnect, and disposal", async () => {
    const host = controller();
    const traces: string[] = [];
    const bridge = createSvelteApplicationBridge<Command, Receipt, Event, Snapshot>(host.value, {
      reduce: (_snapshot, event) => event.snapshot,
      observer: {
        state: () => undefined,
        trace: (entry) => traces.push(`${entry.kind}:${entry.label}`),
      },
      inspectSnapshot: (snapshot) => ({ revision: snapshot.revision }),
    });
    await bridge.start();
    expect(bridge.status).toBe("connected");
    expect(bridge.snapshot?.count).toBe(0);
    expect(host.uiReady).toHaveBeenCalledOnce();
    expect(host.uiRendered).toHaveBeenCalledOnce();
    await bridge.start();
    expect(host.uiReady).toHaveBeenCalledOnce();
    expect(host.uiRendered).toHaveBeenCalledOnce();
    host.emit({ _tag: "Changed", snapshot: { count: 1, revision: 1 } });
    expect(bridge.snapshot?.count).toBe(1);
    const receipt = await bridge.dispatch({ _tag: "Increment", step: 3 });
    expect(receipt._tag).toBe("Incremented");
    await bridge.reconnect();
    expect(bridge.snapshot?.revision).toBe(2);
    await bridge.dispose();
    expect(bridge.status).toBe("disposed");
    expect(host.dispose).toHaveBeenCalledOnce();
    expect(traces).toContain("command:Increment");
    expect(traces).toContain("event:Changed");
    expect(traces).toContain("connection:ui-rendered");
  });
});
