import { assertInInjectionContext, computed, DestroyRef, inject, Injector, signal, type Signal } from "@angular/core";
import {
  createOperationController,
  type BridgeOperation,
  type OperationController,
  type OperationControllerOptions,
  type OperationState,
} from "@runic-artifex/views";

/** Each field of the operation's feedback as a signal, with `run`, `cancel` and `reset`. */
export type OperationHandle<TArgs extends readonly unknown[], TResult, TFailure = never> =
  { readonly [K in keyof OperationState<TResult, TFailure>]: Signal<OperationState<TResult, TFailure>[K]> }
  & Pick<OperationController<TArgs, TResult, TFailure>, "run" | "cancel" | "reset">;

export interface InjectOperationOptions<TResult, TFailure = never> extends OperationControllerOptions<TResult, TFailure> {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/**
 * Tracks Start admission, pending work, cancellation and terminal feedback of a
 * generated `start<Command>()` operation as signals for the lifetime of the
 * injection context. A run that ends `cancelled` sets `cancelled`, not `error`.
 * Destruction settles run promises without cancelling accepted work.
 *
 * ```ts
 * readonly push = injectOperation((remote: string) => this.workspace.client()?.startPush(remote));
 * // <button [disabled]="push.pending()" (click)="push.run('origin')">Push</button>
 * // @if (push.cancelled()) { <p role="status">Push cancelled.</p> }
 * ```
 */
export function injectOperation<TArgs extends readonly unknown[], TResult, TFailure = never>(
  start: (...args: TArgs) => BridgeOperation<TResult, TFailure> | undefined | PromiseLike<BridgeOperation<TResult, TFailure> | undefined>,
  { injector: provided, ...options }: InjectOperationOptions<TResult, TFailure> = {},
): OperationHandle<TArgs, TResult, TFailure> {
  if (!provided) assertInInjectionContext(injectOperation);
  const injector = provided ?? inject(Injector);
  const controller = createOperationController(start, options);
  const state = signal(controller.current);
  controller.subscribe(() => state.set(controller.current));
  injector.get(DestroyRef).onDestroy(() => controller.dispose());
  const field = <K extends keyof OperationState<TResult, TFailure>>(key: K) => computed(() => state()[key]);
  return {
    run: controller.run, cancel: controller.cancel, reset: controller.reset,
    pending: field("pending"), admitting: field("admitting"), operation: field("operation"), status: field("status"),
    outcome: field("outcome"), failure: field("failure"), error: field("error"), cancelled: field("cancelled"),
    cancellationRequested: field("cancellationRequested"), cancelling: field("cancelling"),
    cancellation: field("cancellation"), cancelError: field("cancelError"),
  };
}
