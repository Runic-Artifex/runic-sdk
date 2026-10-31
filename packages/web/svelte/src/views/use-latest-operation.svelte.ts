import {
  createLatestOperationController,
  type LatestOperationController,
  type LatestOperationState,
} from "@runic-artifex/views";
import { derivedFields } from "./derived-fields.svelte.js";

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

  // One derived per member, so a reader of one member runs again only when that member changes.
  const fields = derivedFields(() => current, ["pending", "admitting", "operation", "status", "progress", "outcome", "failure", "error", "cancelled", "cancellationRequested", "cancelling", "cancellation", "cancelError", "blocked"] as const);
  return {
    run: controller.run,
    clear: controller.clear,
    get pending() { return fields.pending(); },
    get admitting() { return fields.admitting(); },
    get operation() { return fields.operation(); },
    get status() { return fields.status(); },
    get progress() { return fields.progress(); },
    get outcome() { return fields.outcome(); },
    get failure() { return fields.failure(); },
    get error() { return fields.error(); },
    get cancelled() { return fields.cancelled(); },
    get cancellationRequested() { return fields.cancellationRequested(); },
    get cancelling() { return fields.cancelling(); },
    get cancellation() { return fields.cancellation(); },
    get cancelError() { return fields.cancelError(); },
    get blocked() { return fields.blocked(); },
  };
}
