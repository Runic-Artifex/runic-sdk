import {
  createOperationController,
  type BridgeOperation,
  type OperationController,
  type OperationControllerOptions,
  type OperationState,
} from "@runic-artifex/views";

export interface OperationHandle<TArgs extends readonly unknown[], TResult, TFailure = never>
  extends OperationState<TResult, TFailure>, Pick<OperationController<TArgs, TResult, TFailure>, "run" | "cancel" | "reset"> {}

/**
 * Tracks Start admission, pending work, cancellation and terminal feedback as
 * reactive properties. Call during component initialization. Owner destruction
 * detaches observation and settles run promises without cancelling accepted work.
 * Progress remains in the generated View snapshot, where the application owns it.
 */
export function useOperation<TArgs extends readonly unknown[], TResult, TFailure = never>(
  start: (...args: TArgs) => BridgeOperation<TResult, TFailure> | undefined | PromiseLike<BridgeOperation<TResult, TFailure> | undefined>,
  options: OperationControllerOptions<TResult, TFailure> = {},
): OperationHandle<TArgs, TResult, TFailure> {
  const controller = createOperationController(start, options);
  let current = $state.raw(controller.current);
  const unsubscribe = controller.subscribe(() => { current = controller.current; });
  // Register teardown immediately, even if the owner is destroyed before the
  // first deferred effect flush. This effect has no reactive dependencies.
  $effect.pre(() => () => { unsubscribe(); controller.dispose(); });

  return {
    run: controller.run,
    cancel: controller.cancel,
    reset: controller.reset,
    get pending() { return current.pending; },
    get admitting() { return current.admitting; },
    get operation() { return current.operation; },
    get status() { return current.status; },
    get outcome() { return current.outcome; },
    get failure() { return current.failure; },
    get error() { return current.error; },
    get cancellationRequested() { return current.cancellationRequested; },
    get cancelling() { return current.cancelling; },
    get cancellation() { return current.cancellation; },
    get cancelError() { return current.cancelError; },
  };
}
