import {
  createOperationController,
  type BridgeOperation,
  type OperationController,
  type OperationControllerOptions,
  type OperationState,
} from "@runic-artifex/views";
import { derivedFields } from "./derived-fields.svelte.js";

export interface OperationHandle<TArgs extends readonly unknown[], TResult, TFailure = never>
  extends OperationState<TResult, TFailure>, Pick<OperationController<TArgs, TResult, TFailure>, "run" | "cancel" | "reset"> {}

/**
 * Tracks Start admission, pending work, cancellation and terminal feedback as
 * reactive properties. Call during component initialization. Owner destruction
 * detaches observation and settles run promises without cancelling accepted work.
 * `progress` follows the running operation (`BridgeOperation.subscribe`).
 */
export function useOperation<TArgs extends readonly unknown[], TResult, TFailure = never>(
  start: (...args: TArgs) => BridgeOperation<TResult, TFailure> | undefined | PromiseLike<BridgeOperation<TResult, TFailure> | undefined>,
  options: OperationControllerOptions<TResult, TFailure> = {},
): OperationHandle<TArgs, TResult, TFailure> {
  const controller = createOperationController(start, options);
  let current = $state.raw(controller.current);
  const unsubscribe = controller.subscribe(() => { current = controller.current; });
  // Svelte runs a pre effect when it is created, during owner initialization,
  // so this teardown exists even if the owner is destroyed before its first
  // flush. onMount/onDestroy and $effect are deferred to that flush and would
  // leak the controller then. The effect reads no state, so it never re-runs.
  $effect.pre(() => () => { unsubscribe(); controller.dispose(); });

  // One derived per member, so a reader of one member runs again only when that member changes.
  const fields = derivedFields(() => current, ["pending", "admitting", "operation", "status", "progress", "outcome", "failure", "error", "cancelled", "cancellationRequested", "cancelling", "cancellation", "cancelError"] as const);
  return {
    run: controller.run,
    cancel: controller.cancel,
    reset: controller.reset,
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
  };
}
