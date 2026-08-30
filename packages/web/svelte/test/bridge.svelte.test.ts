// @vitest-environment happy-dom

import { describe, expect, test, vi } from "vitest";
import { mount, tick, unmount } from "svelte";
import { Cause, Effect, Exit, Schema } from "effect";
import {
  MockApplicationBridge,
  bridgeError,
  createApplicationBridgeController,
  defineApplicationContract,
} from "@runic-artifex/application-bridge";
import type { ApplicationContract } from "@runic-artifex/application-bridge";
import type { ApplicationBridgeController } from "../src/types.js";
import { createSvelteApplicationBridge } from "../src/bridge.svelte.js";
import { createEffectSvelteApplicationBridge } from "../src/effect-bridge.svelte.js";
import BridgeProvider from "./BridgeProvider.svelte";

const Snapshot = Schema.Struct({ count: Schema.Number, revision: Schema.Number });
const Command = Schema.TaggedStruct("Increment", { step: Schema.Number });
const Receipt = Schema.TaggedStruct("Incremented", { snapshot: Snapshot });
const Event = Schema.TaggedStruct("Changed", { snapshot: Snapshot });

type Snapshot = typeof Snapshot.Type;
type Command = typeof Command.Type;
type Receipt = typeof Receipt.Type;
type Event = typeof Event.Type;

const contract: ApplicationContract<Command, Receipt, Event, Snapshot> = defineApplicationContract<
  Command,
  Receipt,
  Event,
  Snapshot
>({
  identity: "runic-svelte-test",
  version: 1,
  command: Command,
  receipt: Receipt,
  event: Event,
  snapshot: Snapshot,
  initialize: { _tag: "Increment", step: 0 },
});

function controller() {
  let revision = -1;
  return createApplicationBridgeController(
    contract,
    MockApplicationBridge<Command, Receipt, Event, Snapshot>({
      initialize: () => Effect.sync(() => ({ count: 0, revision: ++revision })),
      dispatch: (command, publish) => command.step < 0
        ? Effect.fail(bridgeError("CommandRejected", "The step was negative."))
        : Effect.gen(function* () {
          const snapshot = { count: command.step, revision: ++revision };
          yield* publish({ _tag: "Changed", snapshot });
          return { _tag: "Incremented" as const, snapshot };
        }),
    }),
  );
}

function deferred<Value>() {
  let resolve!: (value: Value) => void;
  const promise = new Promise<Value>((next) => { resolve = next; });
  return { promise, resolve };
}

describe("SvelteApplicationBridge", () => {
  test("provider initializes once, projects events, signals rendered once, and releases only frontend resources", async () => {
    const listeners = new Set<(event: Event) => void>();
    const host = {
      initialize: vi.fn(async () => ({ count: 0, revision: 0 })),
      dispatch: vi.fn(),
      cancel: vi.fn(async () => undefined),
      reconnect: vi.fn(async () => ({ count: 0, revision: 0 })),
      uiReady: vi.fn(async () => undefined),
      uiRendered: vi.fn(async () => undefined),
      dispose: vi.fn(async () => undefined),
      subscribe: vi.fn((next: (event: Event) => void) => {
        listeners.add(next);
        return () => listeners.delete(next);
      }),
    } as unknown as ApplicationBridgeController<Command, Receipt, Event, Snapshot>;
    const bridge = createSvelteApplicationBridge(host, { reduce: (_snapshot, event) => event.snapshot });
    const component = mount(BridgeProvider, { target: document.body, props: { bridge } });

    await tick();
    await vi.waitFor(() => expect(host.initialize).toHaveBeenCalledOnce());
    expect(host.uiReady).toHaveBeenCalledOnce();
    expect(host.uiRendered).toHaveBeenCalledOnce();
    listeners.forEach((listener) => listener({ _tag: "Changed", snapshot: { count: 3, revision: 1 } }));
    await tick();
    expect(document.querySelector("output")?.textContent).toBe("3");

    await unmount(component);
    expect(listeners.size).toBe(0);
    expect(host.dispose).not.toHaveBeenCalled();
    expect(bridge.status).toBe("disposed");
  });

  test("owns initialization, domain events, reconnect, and disposal", async () => {
    const traces: string[] = [];
    const bridge = createSvelteApplicationBridge(controller(), {
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
    await bridge.start();
    expect(bridge.snapshot?.count).toBe(0);
    const receipt = await bridge.dispatch({ _tag: "Increment", step: 3 });
    expect(receipt._tag).toBe("Incremented");
    await Promise.resolve();
    expect(bridge.snapshot?.count).toBe(3);
    await bridge.reconnect();
    expect(bridge.snapshot?.revision).toBe(2);
    await bridge.dispose();
    expect(bridge.status).toBe("disposed");
    expect(traces).toContain("command:Increment");
    expect(traces).toContain("event:Changed");
    expect(traces).toContain("connection:ui-rendered");
  });

  test("offers typed Effect composition without replacing the Promise API", async () => {
    const bridge = createEffectSvelteApplicationBridge(controller());
    const receipt = await bridge.run(bridge.effects.dispatch({ _tag: "Increment", step: 3 }));
    expect(receipt.snapshot.count).toBe(3);

    const action = bridge.createAction((step: number, effects) =>
      effects.dispatch({ _tag: "Increment", step }).pipe(Effect.map((value) => value.snapshot.count))
    );
    const success = await action.run(4);
    expect(Exit.isSuccess(success)).toBe(true);
    expect(action.status).toBe("success");
    expect(action.value).toBe(4);

    const failure = await action.run(-1);
    expect(Exit.isFailure(failure)).toBe(true);
    expect(action.status).toBe("failure");
    expect(action.error?._tag).toBe("CommandRejected");
    await bridge.dispose();
  });

  test("Effect actions are latest-wins and bridge-owned", async () => {
    const bridge = createEffectSvelteApplicationBridge(controller());
    const action = bridge.createAction((value: number) => value === 0
      ? Effect.succeed(value)
      : Effect.never
    );
    const first = action.run(1);
    await Promise.resolve();
    const second = await action.run(0);
    const interrupted = await first;
    expect(Exit.isFailure(interrupted) && Cause.isInterruptedOnly(interrupted.cause)).toBe(true);
    expect(Exit.isSuccess(second)).toBe(true);
    expect(action.status).toBe("success");

    const owned = bridge.createAction((_input: void) => Effect.never);
    const running = owned.run(undefined);
    await Promise.resolve();
    await bridge.dispose();
    expect(owned.status).toBe("disposed");
    const disposedExit = await running;
    expect(Exit.isFailure(disposedExit) && Cause.isInterruptedOnly(disposedExit.cause)).toBe(true);
  });

  test("keeps disposal terminal across all public operations and in-flight races", async () => {
    const base = controller();
    const pendingDispatch = deferred<Receipt>();
    const dispatch = vi.fn(() => pendingDispatch.promise);
    const host: ApplicationBridgeController<Command, Receipt, Event, Snapshot> = { ...base, dispatch };
    const bridge = createSvelteApplicationBridge(host);

    const running = bridge.dispatch({ _tag: "Increment", step: 1 });
    await bridge.dispose();
    pendingDispatch.resolve({ _tag: "Incremented", snapshot: { count: 1, revision: 1 } });

    await expect(running).rejects.toThrow("disposed");
    await expect(bridge.dispatch({ _tag: "Increment", step: 2 })).rejects.toThrow("disposed");
    await expect(bridge.cancel("operation-1")).rejects.toThrow("disposed");
    await expect(bridge.reconnect()).rejects.toThrow("disposed");
    await expect(bridge.uiReady()).rejects.toThrow("disposed");
    await expect(bridge.uiRendered()).rejects.toThrow("disposed");
    expect(dispatch).toHaveBeenCalledOnce();
    expect(bridge.status).toBe("disposed");
  });

  test("does not let an initialization completion revive a disposed bridge", async () => {
    const base = controller();
    const pendingInitialize = deferred<Snapshot>();
    const host: ApplicationBridgeController<Command, Receipt, Event, Snapshot> = {
      ...base,
      initialize: () => pendingInitialize.promise,
    };
    const bridge = createSvelteApplicationBridge(host);
    const starting = bridge.start();
    await bridge.dispose();
    pendingInitialize.resolve({ count: 1, revision: 1 });

    await expect(starting).rejects.toThrow("disposed");
    expect(bridge.status).toBe("disposed");
    expect(bridge.snapshot).toBeUndefined();
  });
});
