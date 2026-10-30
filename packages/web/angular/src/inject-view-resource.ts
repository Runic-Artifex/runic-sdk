import {
  assertInInjectionContext, computed, effect, inject, Injector, isSignal, resource, signal, untracked,
  type Resource, type ResourceStreamItem, type Signal, type WritableSignal,
} from "@angular/core";
import { createViewController, type ViewClient, type ViewController, type ViewSource } from "@runic-artifex/views";
import { viewSourceIdentity } from "@runic-artifex/views/bindings";
import type { InjectViewOptions } from "./inject-view.js";

/**
 * A View connection as an Angular `Resource`: `value()` is the View's state, `status()` is `loading` until the
 * client connected (`reloading` after `reload()`), then `resolved`, or `error` with `error()` the connection
 * failure; `idle` while the source is `null` or `undefined`.
 */
export interface ViewResource<TClient extends ViewClient> extends Resource<TClient["snapshot"] | undefined> {
  /** The connected client for commands, or undefined until it connects. */
  readonly client: Signal<TClient | undefined>;
  /** Connects again, for example after a failure. Returns false while the source is `null` or `undefined`. */
  reload(): boolean;
  /** Releases the client and returns the resource to `idle`; the injector's destruction does this too. */
  destroy(): void;
}

export type InjectViewResourceOptions<TClient extends ViewClient> = InjectViewOptions<TClient>;

function asError(cause: unknown): Error {
  return cause instanceof Error ? cause : new Error(String(cause), { cause });
}

/**
 * Connects a generated View reference as an Angular `resource()` for the lifetime of the injection context, the
 * asynchronous counterpart of `injectView` (D053.11): a template branches on `hasValue()`, `isLoading()` and
 * `error()` as for any resource, and `value()` is never `undefined` once `hasValue()` is true.
 *
 * ```ts
 * readonly page = input.required<CounterPageReference>();
 * readonly counter = injectViewResource(this.page);
 * ```
 * ```html
 * @if (counter.hasValue()) {
 *   <button (click)="counter.client()?.increment()">{{ counter.value().count }}</button>
 * } @else if (counter.error()) {
 *   <p role="alert">{{ describe(counter.error()) }}</p> <button (click)="counter.reload()">Retry</button>
 * } @else { <p>Connecting…</p> }
 * ```
 *
 * A changed reference (by its `connect` function) releases the previous client and loads the new one. Released
 * clients are kept for `keepAlive` milliseconds, as with `injectView`, so a component that is created again
 * resolves at once.
 */
export function injectViewResource<TClient extends ViewClient>(source: Signal<ViewSource<TClient>> | ViewSource<TClient>,
  options: InjectViewResourceOptions<TClient> = {}): ViewResource<TClient> {
  if (!options.injector) assertInInjectionContext(injectViewResource);
  const injector = options.injector ?? inject(Injector);
  const read = (): ViewSource<TClient> => isSignal(source) ? source() : source;
  const client = signal<TClient | undefined>(undefined);
  const controllerOptions = {
    ...(options.release ? { release: options.release } : {}),
    ...(options.keepAlive === undefined ? {} : { keepAlive: options.keepAlive }),
  };
  // The source is identified by its client or `connect` function, so a new wrapper object does not reload.
  const identity = computed(() => viewSourceIdentity(read()));
  // The controller of the latest load. A resource that becomes idle does not abort its last load, so it is released here.
  let active: ViewController<TClient> | undefined;
  effect(() => {
    if (identity() !== undefined) return;
    active?.dispose();
    active = undefined;
    client.set(undefined);
  }, { injector });
  const ref = resource<TClient["snapshot"] | undefined, object | undefined>({
    injector,
    params: () => identity(),
    stream: async ({ abortSignal }) => {
      const controller = createViewController<TClient>(controllerOptions);
      active = controller;
      abortSignal.addEventListener("abort", () => {
        if (active === controller) active = undefined;
        controller.dispose();
        if (untracked(client) === controller.current.client) client.set(undefined);
      }, { once: true });
      controller.setSource(untracked(read));
      if (controller.current.pending) {
        await new Promise<void>(resolve => {
          const stop = controller.subscribe(() => { if (!controller.current.pending) { stop(); resolve(); } });
          abortSignal.addEventListener("abort", () => { stop(); resolve(); }, { once: true });
        });
      }
      const value: WritableSignal<ResourceStreamItem<TClient["snapshot"] | undefined>> = signal(
        controller.current.error !== undefined ? { error: asError(controller.current.error) } : { value: controller.current.state });
      if (abortSignal.aborted) return value;
      client.set(controller.current.client);
      controller.subscribe(() => {
        const current = controller.current;
        client.set(current.client);
        value.set(current.error !== undefined ? { error: asError(current.error) } : { value: current.state });
      });
      return value;
    },
  });
  return {
    value: ref.value.asReadonly(),
    status: ref.status,
    error: ref.error,
    isLoading: ref.isLoading,
    snapshot: ref.snapshot,
    hasValue: () => ref.hasValue(),
    client: client.asReadonly(),
    reload: () => ref.reload(),
    destroy: () => ref.destroy(),
  } as ViewResource<TClient>;
}
