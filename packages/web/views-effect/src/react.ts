import * as Effect from "effect/Effect";
import type * as Fiber from "effect/Fiber";
import { useCallback, useEffect, useInsertionEffect, useRef, useState, useSyncExternalStore } from "react";
import { createEffectAction, type EffectAction, type EffectActionOptions, type EffectActionState } from "./action.js";

export interface EffectActionHandle<Args extends readonly unknown[], A, E> extends EffectActionState<A, E> {
  readonly run: EffectAction<Args, A, E>["run"];
  readonly interrupt: EffectAction<Args, A, E>["interrupt"];
  readonly reset: EffectAction<Args, A, E>["reset"];
}

/**
 * Runs an Effect workflow from a component and renders its status, value and
 * typed error. A new run interrupts the previous one, and unmounting interrupts
 * the current run, which cancels an interrupted operation in .NET.
 *
 * ```tsx
 * const save = useEffectAction(() => operation(() => client!.startSave(), { timeout: "10 seconds" }));
 * <button disabled={!client || save.pending} onClick={() => void save.run()}>Save</button>
 * {save.error?._tag === "ViewOperationTimedOut" && <p role="alert">Saving took too long.</p>}
 * ```
 *
 * The latest `program` and `runFork` are used when `run` is called, so they may
 * close over the latest props.
 */
export function useEffectAction<Args extends readonly unknown[], A, E, R = never>(
  program: (...args: Args) => Effect.Effect<A, E, R>,
  ...[options]: [R] extends [never] ? [options?: EffectActionOptions<A, E, R>] : [options: EffectActionOptions<A, E, R> & {
    readonly runFork: (effect: Effect.Effect<A, E, R>) => Fiber.Fiber<A, E>;
  }]
): EffectActionHandle<Args, A, E> {
  const latest = useRef({ program, runFork: options?.runFork });
  useInsertionEffect(() => { latest.current = { program, runFork: options?.runFork }; });
  const [create] = useState(() => () => {
    const runFork = (effect: Effect.Effect<A, E, R>): Fiber.Fiber<A, E> =>
      (latest.current.runFork ?? (Effect.runFork as (effect: Effect.Effect<A, E, R>) => Fiber.Fiber<A, E>))(effect);
    return createEffectAction<Args, A, E, R>((...args: Args) => latest.current.program(...args), ...[{ runFork }] as never);
  });
  const [action, setAction] = useState(create);
  useEffect(() => {
    // StrictMode runs effects twice: a disposed action is replaced on the second mount.
    if (disposedActions.has(action)) { setAction(() => create()); return; }
    return () => { disposedActions.add(action); void action.dispose(); };
  }, [action, create]);
  const read = useCallback(() => action.current, [action]);
  const state = useSyncExternalStore(action.subscribe, read, read);
  return { ...state, run: action.run, interrupt: action.interrupt, reset: action.reset };
}

const disposedActions = new WeakSet<object>();
