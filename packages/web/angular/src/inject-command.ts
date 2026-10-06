import { assertInInjectionContext, DestroyRef, inject, Injector, signal, type Signal } from "@angular/core";
import { createCommandController } from "@runic-artifex/views";

export interface CommandHandle<TArgs extends readonly unknown[], TResult> {
  /** Runs the command. Resolves to its result, or to undefined after a failure, which `error` then holds. Never rejects. */
  run(...args: TArgs): Promise<TResult | undefined>;
  /** True while a run is in flight. */
  readonly pending: Signal<boolean>;
  /** Why the latest run failed, until the next run starts. */
  readonly error: Signal<unknown>;
  /** Clears `error`. */
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
export function injectCommand<TArgs extends readonly unknown[], TResult>(
  command: (...args: TArgs) => TResult | PromiseLike<TResult>,
  options: InjectCommandOptions = {},
): CommandHandle<TArgs, Awaited<TResult>> {
  if (!options.injector) assertInInjectionContext(injectCommand);
  const injector = options.injector ?? inject(Injector);
  const controller = createCommandController(command);
  const pending = signal(false);
  const error = signal<unknown>(undefined);
  controller.subscribe(() => {
    pending.set(controller.current.pending);
    error.set(controller.current.error);
  });
  injector.get(DestroyRef).onDestroy(() => controller.dispose());
  return { run: controller.run, pending: pending.asReadonly(), error: error.asReadonly(), reset: controller.reset };
}
