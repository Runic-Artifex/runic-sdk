import { assertInInjectionContext, DestroyRef, inject, Injector, signal, type Signal } from "@angular/core";
import { createCommandController, type BridgeOutcomeFailure } from "@runic-artifex/views";

export interface CommandHandle<TArgs extends readonly unknown[], TResult, TFailure = BridgeOutcomeFailure<TResult>> {
  /**
   * Runs the command. Resolves to its result, including a `BridgeOutcome` with a
   * declared failure, which `failure` then holds, or to undefined after an
   * unexpected failure, which `error` then holds. Never rejects.
   */
  run(...args: TArgs): Promise<TResult | undefined>;
  /** True while a run is in flight. */
  readonly pending: Signal<boolean>;
  /** Why the latest run failed unexpectedly, until the next run starts. */
  readonly error: Signal<unknown>;
  /**
   * The declared failure of the latest run, for a command that resolves a
   * `BridgeOutcome`, until the next run starts. A superseded run sets neither.
   */
  readonly failure: Signal<TFailure | undefined>;
  /** Clears `error` and `failure`. */
  reset(): void;
}

export interface InjectCommandOptions {
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/**
 * Tracks a command's pending state and failure as signals for the lifetime of
 * the injection context. The command may read signals, such as a client:
 *
 * ```ts
 * readonly increment = injectCommand(() => this.counter.client()?.increment());
 * // <button [disabled]="!counter.client() || increment.pending()" (click)="increment.run()">
 * ```
 */
export function injectCommand<TArgs extends readonly unknown[], TReturn>(
  command: (...args: TArgs) => TReturn,
  options: InjectCommandOptions = {},
): CommandHandle<TArgs, Awaited<TReturn>> {
  if (!options.injector) assertInInjectionContext(injectCommand);
  const injector = options.injector ?? inject(Injector);
  const controller = createCommandController(command);
  const pending = signal(false);
  const error = signal<unknown>(undefined);
  const failure = signal<BridgeOutcomeFailure<Awaited<TReturn>> | undefined>(undefined);
  controller.subscribe(() => {
    pending.set(controller.current.pending);
    error.set(controller.current.error);
    failure.set(controller.current.failure);
  });
  injector.get(DestroyRef).onDestroy(() => controller.dispose());
  return {
    run: controller.run, pending: pending.asReadonly(), error: error.asReadonly(), failure: failure.asReadonly(), reset: controller.reset,
  };
}
