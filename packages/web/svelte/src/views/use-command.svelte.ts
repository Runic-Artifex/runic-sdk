import { createCommandController, type BridgeOutcomeFailure } from "@runic-artifex/views";
import { derivedFields } from "./derived-fields.svelte.js";

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
 * Tracks a command's pending state and failure as reactive properties. Called
 * during component initialization (or in `$effect.root`), owner destruction
 * stops publishing state without cancelling a run in flight, whose promise
 * still settles. The
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
  const unsubscribe = controller.subscribe(() => { current = controller.current; });
  // Registered before the first flush, so a destroyed owner never leaks the controller (see useOperation).
  // Outside an owner, which useCommand has always allowed, there is nothing to release it with.
  try {
    $effect.pre(() => () => { unsubscribe(); controller.dispose(); });
  } catch (error) {
    if (!String((error as { message?: unknown } | undefined)?.message).includes("effect_orphan")) throw error;
  }
  // One derived per member, so a reader of one member runs again only when that member changes.
  const fields = derivedFields(() => current, ["pending", "error", "failure"] as const);
  return {
    run: controller.run,
    get pending() { return fields.pending(); },
    get error() { return fields.error(); },
    get failure() { return fields.failure(); },
    reset: controller.reset,
  };
}
