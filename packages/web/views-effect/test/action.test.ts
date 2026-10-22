import { Context, Deferred, Effect, Exit, Layer, ManagedRuntime } from "effect";
import * as React from "react";
import { createElement, StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { describe, expect, test } from "vitest";
import { createEffectAction, ViewDisconnected } from "../dist/index.js";
import { useEffectAction, type EffectActionHandle } from "../dist/react.js";
import { BridgeError } from "@runic-artifex/views";

// React 18.3.0, the lowest supported React, exports act only as unstable_act.
const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const disconnected = () => new ViewDisconnected({ message: "Gone", route: "counterIncrement", cause: new BridgeError("disconnected", "Gone") });

describe("createEffectAction", () => {
  test("an await using block disposes the action, which interrupts its run", async () => {
    let interrupted = false;
    let run: Promise<Exit.Exit<never>> | undefined;
    {
      await using action = createEffectAction(() => Effect.never.pipe(Effect.onInterrupt(() => Effect.sync(() => { interrupted = true; }))));
      expect(typeof action[Symbol.asyncDispose]).toBe("function");
      run = action.run();
      await new Promise(resolve => setTimeout(resolve, 1));
    }
    expect(interrupted).toBe(true);
    expect(Exit.hasInterrupts(await run)).toBe(true);
  });

  test("loads and disposes where Symbol.asyncDispose is undefined", async () => {
    // An engine without explicit resource management (older WebKitGTK and WKWebView). Symbol's own
    // properties cannot be deleted, so a copy without the two well-known symbols replaces it while
    // a fresh copy of the module loads and creates an action.
    const native = globalThis.Symbol;
    const legacy = function Symbol(description?: string | number) { return native(description); } as unknown as Record<PropertyKey, unknown>;
    for (const key of Reflect.ownKeys(native))
      if (!["dispose", "asyncDispose", "length", "name", "prototype"].includes(key as string)) legacy[key] = (native as unknown as Record<PropertyKey, unknown>)[key];
    const specifier = "../dist/action.js?legacy-engine";
    let action: ReturnType<typeof createEffectAction<[], number, never>>;
    (globalThis as { Symbol: unknown }).Symbol = legacy;
    try {
      expect((Symbol as { asyncDispose?: symbol }).asyncDispose).toBeUndefined();
      const legacyModule = await import(/* @vite-ignore */ specifier) as { createEffectAction: typeof createEffectAction };
      expect(legacyModule.createEffectAction).not.toBe(createEffectAction);
      action = legacyModule.createEffectAction(() => Effect.succeed(1));
    } finally {
      (globalThis as { Symbol: unknown }).Symbol = native;
    }
    expect(Object.getOwnPropertySymbols(action)).toEqual([]);
    expect(await action.run()).toStrictEqual(Exit.succeed(1));
    await action.dispose();
    expect(action.current.status).toBe("success");
  });

  test("publishes running, success and typed failure", async () => {
    const action = createEffectAction((value: number) => value < 0 ? Effect.fail(disconnected()) : Effect.succeed(value * 2));
    const seen: string[] = [];
    action.subscribe(() => { seen.push(action.current.status); });
    expect(await action.run(2)).toStrictEqual(Exit.succeed(4));
    expect(action.current).toMatchObject({ status: "success", pending: false, value: 4 });
    await action.run(-1);
    expect(action.current.status).toBe("failure");
    expect(action.current.error?._tag).toBe("ViewDisconnected");
    expect(seen).toEqual(["running", "success", "running", "failure"]);
    action.reset();
    expect(action.current.status).toBe("idle");
  });

  test("a new run interrupts the previous one, and dispose interrupts the current one", async () => {
    const started: number[] = [];
    const interrupted: number[] = [];
    const action = createEffectAction((id: number) => Effect.sync(() => { started.push(id); }).pipe(
      Effect.andThen(Effect.never), Effect.onInterrupt(() => Effect.sync(() => { interrupted.push(id); }))));
    const begun = async (id: number) => { while (!started.includes(id)) await new Promise(resolve => setTimeout(resolve, 1)); };
    const first = action.run(1);
    await begun(1);
    const second = action.run(2);
    expect(Exit.hasInterrupts(await first)).toBe(true);
    await begun(2);
    expect(interrupted).toEqual([1]);
    expect(action.current.status).toBe("running");
    await action.interrupt();
    expect(Exit.hasInterrupts(await second)).toBe(true);
    expect(action.current.status).toBe("interrupted");
    const third = action.run(3);
    await begun(3);
    await action.dispose();
    expect(Exit.hasInterrupts(await third)).toBe(true);
    expect(interrupted).toEqual([1, 2, 3]);
    // A run that has not started yet is abandoned by interrupt().
    const fresh = createEffectAction(() => Effect.succeed(1));
    const abandoned = fresh.run();
    await fresh.interrupt();
    expect(Exit.hasInterrupts(await abandoned)).toBe(true);
    expect(fresh.current.pending).toBe(false);
  });

  test("runs programs with services through a ManagedRuntime", async () => {
    class Greeting extends Context.Service<Greeting, string>()("Greeting") {}
    const runtime = ManagedRuntime.make(Layer.succeed(Greeting, "hello"));
    const action = createEffectAction(() => Effect.service(Greeting), { runFork: runtime.runFork });
    expect(await action.run()).toStrictEqual(Exit.succeed("hello"));
    await runtime.dispose();
  });
});

describe("useEffectAction", () => {
  test("renders the action state and interrupts on unmount", async () => {
    const host = document.createElement("div");
    const root = createRoot(host);
    let gate = Effect.runSync(Deferred.make<number>());
    let interrupted = 0;
    let handle!: EffectActionHandle<[], number, never>;
    function Save() {
      handle = useEffectAction(() => Deferred.await(gate).pipe(Effect.onInterrupt(() => Effect.sync(() => { interrupted++; }))));
      return createElement("p", null, `${handle.status}:${handle.value ?? ""}`);
    }
    await act(async () => { root.render(createElement(StrictMode, null, createElement(Save))); });
    expect(host.textContent).toBe("idle:");
    let run!: Promise<Exit.Exit<number>>;
    await act(async () => { run = handle.run(); });
    expect(host.textContent).toBe("running:");
    await act(async () => { Effect.runSync(Deferred.succeed(gate, 5)); await run; });
    expect(host.textContent).toBe("success:5");
    gate = Effect.runSync(Deferred.make<number>());
    let next!: Promise<Exit.Exit<number>>;
    await act(async () => { next = handle.run(); });
    await act(async () => { root.unmount(); });
    expect(Exit.hasInterrupts(await next)).toBe(true);
    expect(interrupted).toBe(1);
  });
});
