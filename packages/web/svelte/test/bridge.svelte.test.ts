import { describe, expect, test, vi } from "vitest";
import { Cause, Effect, Exit, Fiber, Stream } from "effect";
import { createSvelteApplicationBridge } from "../src/bridge.svelte.js";
import { createEffectSvelteApplicationBridge } from "../src/effect-bridge.svelte.js";
import type { EffectApplicationBridgeController } from "../src/types.js";

type Command = { _tag: "Increment"; step: number };
type Receipt = { _tag: "Incremented"; snapshot: Snapshot };
type Event = { _tag: "Changed"; snapshot: Snapshot };
type Snapshot = { count: number; revision: number };
type TestFailure = { _tag: "Rejected"; message: string };

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

function effectController(): EffectApplicationBridgeController<
  Command,
  Receipt,
  Event,
  Snapshot,
  TestFailure,
  never
> {
  const host = controller();
  const effects = {
    initialize: Effect.succeed({ count: 0, revision: 0 }),
    dispatch: (command: Command) => command.step < 0
      ? Effect.fail<TestFailure>({ _tag: "Rejected", message: "The step was negative." })
      : Effect.succeed({
        _tag: "Incremented" as const,
        snapshot: { count: command.step, revision: 1 },
      }),
    cancel: (_operationId: string) => Effect.void,
    reconnect: Effect.succeed({ count: 2, revision: 2 }),
    uiReady: Effect.void,
    uiRendered: Effect.void,
    events: Stream.empty as Stream.Stream<Event, TestFailure>,
  };
  const run = <A, E>(program: Effect.Effect<A, E>) => Effect.runPromise(program);
  return {
    ...host.value,
    effects,
    initialize: () => run(effects.initialize),
    dispatch: (command) => run(effects.dispatch(command)),
    cancel: (operationId) => run(effects.cancel(operationId)),
    reconnect: () => run(effects.reconnect),
    uiReady: () => run(effects.uiReady),
    uiRendered: () => run(effects.uiRendered),
    run,
    runExit: (program) => run(Effect.exit(program)),
    fork: (program) => Effect.runFork(program),
    await: (fiber) => run(Fiber.await(fiber)),
    interrupt: (fiber) => run(Fiber.interrupt(fiber)),
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

  test("offers typed Effect composition without replacing the Promise API", async () => {
    const bridge = createEffectSvelteApplicationBridge(effectController());
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
    expect(action.error?._tag).toBe("Rejected");
    await bridge.dispose();
  });

  test("Effect actions are latest-wins and bridge-owned", async () => {
    const bridge = createEffectSvelteApplicationBridge(effectController());
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
});
