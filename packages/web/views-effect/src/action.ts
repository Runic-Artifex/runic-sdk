import * as Cause from "effect/Cause";
import * as Effect from "effect/Effect";
import * as Exit from "effect/Exit";
import type * as Fiber from "effect/Fiber";
import * as Option from "effect/Option";

export type EffectActionStatus = "idle" | "running" | "success" | "failure" | "interrupted";

/** One consistent reading of an {@link EffectAction}. A new object is published for every change. */
export interface EffectActionState<A, E> {
  readonly status: EffectActionStatus;
  /** True while a run is in flight. */
  readonly pending: boolean;
  /** The value of the last successful run. */
  readonly value: A | undefined;
  /** The typed failure of the last run, such as `ViewCommandFailed`. */
  readonly error: E | undefined;
  /** The complete cause of the last failed or interrupted run, including defects. */
  readonly cause: Cause.Cause<E> | undefined;
}

export interface EffectActionOptions<A, E, R> {
  /**
   * Forks the program. Defaults to `Effect.runFork`, which requires a program
   * without services; pass `runtime.runFork` from a `ManagedRuntime` otherwise.
   */
  readonly runFork?: (effect: Effect.Effect<A, E, R>) => Fiber.Fiber<A, E>;
}

/** A latest-wins Effect workflow projected into framework-neutral state. */
export interface EffectAction<Args extends readonly unknown[], A, E> {
  readonly current: EffectActionState<A, E>;
  /** Calls `listener` after each change of `current`, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /**
   * Interrupts the previous run of this action, then runs the program. Resolves
   * to the run's `Exit` and never rejects. A superseded run does not update `current`.
   */
  run(...args: Args): Promise<Exit.Exit<A, E>>;
  /** Interrupts the current run, which cancels an interrupted operation in .NET. */
  interrupt(): Promise<void>;
  /** Returns to `idle`. Has no effect while a run is in flight. */
  reset(): void;
  /** Interrupts the current run and stops publishing. */
  dispose(): Promise<void>;
}

const idle = { status: "idle", pending: false, value: undefined, error: undefined, cause: undefined } as const;

function awaitExit<A, E>(fiber: Fiber.Fiber<A, E>): Promise<Exit.Exit<A, E>> {
  return new Promise(resolve => { fiber.addObserver(resolve); });
}

/**
 * Creates the framework-neutral runner behind `useEffectAction`. Framework
 * bindings render `current`; `run` replaces an older run, as a search box or a
 * repeated button press expects.
 */
export function createEffectAction<Args extends readonly unknown[], A, E, R = never>(
  program: (...args: Args) => Effect.Effect<A, E, R>,
  ...[options]: [R] extends [never] ? [options?: EffectActionOptions<A, E, R>] : [options: EffectActionOptions<A, E, R> & {
    readonly runFork: (effect: Effect.Effect<A, E, R>) => Fiber.Fiber<A, E>;
  }]
): EffectAction<Args, A, E> {
  const runFork = options?.runFork ?? (Effect.runFork as (effect: Effect.Effect<A, E, R>) => Fiber.Fiber<A, E>);
  const listeners = new Set<() => void>();
  let current: EffectActionState<A, E> = idle;
  let fiber: Fiber.Fiber<A, E> | undefined;
  let generation = 0;
  let disposed = false;

  function publish(next: EffectActionState<A, E>): void {
    if (disposed) return;
    current = next;
    for (const listener of [...listeners]) listener();
  }

  function settle(exit: Exit.Exit<A, E>): void {
    if (Exit.isSuccess(exit)) {
      publish({ status: "success", pending: false, value: exit.value, error: undefined, cause: undefined });
      return;
    }
    publish({
      status: Cause.hasInterruptsOnly(exit.cause) ? "interrupted" : "failure", pending: false, value: undefined,
      error: Option.getOrUndefined(Cause.findErrorOption(exit.cause)), cause: exit.cause,
    });
  }

  // A run replaced by a newer one finishes without updating `current`.
  const superseded = new WeakSet<Fiber.Fiber<A, E>>();
  async function stop(replaced: boolean): Promise<void> {
    const running = fiber;
    fiber = undefined;
    if (running === undefined) return;
    if (replaced) superseded.add(running);
    running.interruptUnsafe();
    await awaitExit(running);
  }

  return {
    get current() { return current; },
    subscribe(listener) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    async run(...args) {
      if (disposed) return Exit.interrupt();
      const run = ++generation;
      await stop(true);
      // A newer run, interrupt() or dispose() came first.
      if (disposed || run !== generation) return Exit.interrupt();
      publish({ ...current, status: "running", pending: true, error: undefined, cause: undefined });
      const started = runFork(Effect.suspend(() => program(...args)));
      fiber = started;
      const exit = await awaitExit(started);
      if (fiber === started) fiber = undefined;
      if (!superseded.has(started)) settle(exit);
      return exit;
    },
    async interrupt() {
      // Also abandons a run that is still waiting for its predecessor to stop.
      generation++;
      await stop(false);
      if (current.pending && fiber === undefined) publish({ ...current, status: "interrupted", pending: false });
    },
    reset() {
      if (fiber === undefined && current !== idle) publish(idle);
    },
    async dispose() {
      if (disposed) return;
      disposed = true;
      generation++;
      listeners.clear();
      await stop(true);
    },
  };
}
