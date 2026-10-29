import { assertInInjectionContext, computed, DestroyRef, inject, Injector, signal, type Signal } from "@angular/core";
import {
  createLatestOperationController,
  type LatestOperationController,
  type LatestOperationState,
} from "@runic-artifex/views";

/** Each field of the latest-operation feedback as a signal, with `run` and `clear`. */
export type LatestOperationHandle<TResult, TFailure = never> =
  { readonly [K in keyof LatestOperationState<TResult, TFailure>]: Signal<LatestOperationState<TResult, TFailure>[K]> }
  & Pick<LatestOperationController<TResult, TFailure>, "run" | "clear">;

export interface InjectLatestOperationOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/**
 * Admits only the newest selection, for reads such as "load the selected item", as signals
 * for the lifetime of the injection context. Each `run(intent)` replaces the queued intent
 * and cancels the active read, and the next read starts only after the previous one has
 * finished (including `waitForCompletion`). `blocked` means admission is uncertain; only
 * `clear("replace")`, after the application has established a fresh session, clears it.
 * Destruction settles run promises without cancelling accepted work.
 *
 * ```ts
 * readonly load = injectLatestOperation<Item>();
 * select(id: string) { void this.load.run({ start: () => this.items.client()?.startLoad(id) }); }
 * // @if (load.outcome()?.ok) { <item-view [item]="load.outcome()!.value" /> }
 * ```
 */
export function injectLatestOperation<TResult = void, TFailure = never>(
  { injector: provided }: InjectLatestOperationOptions = {},
): LatestOperationHandle<TResult, TFailure> {
  if (!provided) assertInInjectionContext(injectLatestOperation);
  const injector = provided ?? inject(Injector);
  const controller = createLatestOperationController<TResult, TFailure>();
  const state = signal(controller.current);
  controller.subscribe(() => state.set(controller.current));
  injector.get(DestroyRef).onDestroy(() => controller.dispose());
  const field = <K extends keyof LatestOperationState<TResult, TFailure>>(key: K) => computed(() => state()[key]);
  return {
    run: controller.run, clear: controller.clear,
    pending: field("pending"), admitting: field("admitting"), operation: field("operation"), status: field("status"),
    outcome: field("outcome"), failure: field("failure"), error: field("error"), cancelled: field("cancelled"),
    cancellationRequested: field("cancellationRequested"), cancelling: field("cancelling"),
    progress: field("progress"), cancellation: field("cancellation"), cancelError: field("cancelError"), blocked: field("blocked"),
  };
}
