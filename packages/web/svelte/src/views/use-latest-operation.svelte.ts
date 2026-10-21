import {
  createLatestOperationController,
  type LatestOperationController,
  type LatestOperationState,
} from "@runic-artifex/views";

export interface LatestOperationHandle<TResult, TFailure = never>
  extends LatestOperationState<TResult, TFailure>, Pick<LatestOperationController<TResult, TFailure>, "run" | "clear"> {}

/**
 * Admits only the newest selection, for reads such as "load the selected item": each
 * `run(intent)` replaces the queued intent and cancels the active read, and the next read
 * starts only after the previous one has finished (including `waitForCompletion`). The
 * state fields are reactive properties. `blocked` means admission is uncertain; only
 * `clear("replace")`, after the application has established a fresh session, clears it.
 * Call during component initialization. Owner destruction detaches observation and
 * settles run promises without cancelling accepted work.
 */
export function useLatestOperation<TResult = void, TFailure = never>(): LatestOperationHandle<TResult, TFailure> {
  const controller = createLatestOperationController<TResult, TFailure>();
  let current = $state.raw(controller.current);
  const unsubscribe = controller.subscribe(() => { current = controller.current; });
  // Registered before the first flush, so a destroyed owner never leaks the controller (see useOperation).
  $effect.pre(() => () => { unsubscribe(); controller.dispose(); });

  return {
    run: controller.run,
    clear: controller.clear,
    get pending() { return current.pending; },
    get admitting() { return current.admitting; },
    get operation() { return current.operation; },
    get status() { return current.status; },
    get outcome() { return current.outcome; },
    get failure() { return current.failure; },
    get error() { return current.error; },
    get cancelled() { return current.cancelled; },
    get cancellationRequested() { return current.cancellationRequested; },
    get cancelling() { return current.cancelling; },
    get cancellation() { return current.cancellation; },
    get cancelError() { return current.cancelError; },
    get blocked() { return current.blocked; },
  };
}
