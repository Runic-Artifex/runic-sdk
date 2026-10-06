import { createCommandController } from "@runic-artifex/views";

export interface CommandHandle<TArgs extends readonly unknown[], TResult> {
  /** Runs the command. Resolves to its result, or to undefined after a failure, which `error` then holds. Never rejects. */
  run(...args: TArgs): Promise<TResult | undefined>;
  /** True while a run is in flight. */
  readonly pending: boolean;
  /** Why the latest run failed, until the next run starts. */
  readonly error: unknown;
  /** Clears `error`. */
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
export function useCommand<TArgs extends readonly unknown[], TResult>(
  command: (...args: TArgs) => TResult | PromiseLike<TResult>,
): CommandHandle<TArgs, Awaited<TResult>> {
  const controller = createCommandController(command);
  let current = $state.raw(controller.current);
  controller.subscribe(() => { current = controller.current; });
  return {
    run: controller.run,
    get pending() { return current.pending; },
    get error() { return current.error; },
    reset: controller.reset,
  };
}
