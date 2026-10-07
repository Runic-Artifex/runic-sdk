import { createCommandController, type BridgeOutcomeFailure } from "@runic-artifex/views";

export interface CommandHandle<TArgs extends readonly unknown[], TResult, TFailure = BridgeOutcomeFailure<TResult>> {
  /**
   * Runs the command. Resolves to its result, including a `BridgeOutcome` with a
   * declared failure, which `failure` then holds, or to undefined after an
   * unexpected failure, which `error` then holds. Never rejects.
   */
  run(...args: TArgs): Promise<TResult | undefined>;
  /** True while a run is in flight. */
  readonly pending: boolean;
  /** Why the latest run failed unexpectedly, until the next run starts. */
  readonly error: unknown;
  /**
   * The declared failure of the latest run, for a command that resolves a
   * `BridgeOutcome`, until the next run starts. A superseded run sets neither.
   */
  readonly failure: TFailure | undefined;
  /** Clears `error` and `failure`. */
  reset(): void;
}

/**
 * Tracks a command's pending state and failure as reactive properties. The
 * command may close over reactive values:
 *
 * ```svelte
 * const increment = useCommand(() => counter.client?.increment());
 * <button disabled={!counter.client || increment.pending} onclick={() => increment.run()}>…</button>
 * ```
 */
export function useCommand<TArgs extends readonly unknown[], TReturn>(
  command: (...args: TArgs) => TReturn,
): CommandHandle<TArgs, Awaited<TReturn>> {
  const controller = createCommandController(command);
  let current = $state.raw(controller.current);
  controller.subscribe(() => { current = controller.current; });
  return {
    run: controller.run,
    get pending() { return current.pending; },
    get error() { return current.error; },
    get failure() { return current.failure; },
    reset: controller.reset,
  };
}
