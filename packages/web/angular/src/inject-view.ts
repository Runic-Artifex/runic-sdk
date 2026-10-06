import { assertInInjectionContext, computed, DestroyRef, effect, inject, Injector, isSignal, signal, untracked, type Signal } from "@angular/core";
import { createViewController, isViewClient, viewSourceIdentity, type ViewClient, type ViewSource } from "@runic-artifex/views";

export interface ViewHandle<TClient extends ViewClient> {
  /** The latest state, or undefined until the client connects. */
  readonly state: Signal<TClient["snapshot"] | undefined>;
  /** The connected client for commands, or undefined until it connects. */
  readonly client: Signal<TClient | undefined>;
  /** The connection failure, if any. */
  readonly error: Signal<unknown>;
  /** True while a reference is connecting. */
  readonly pending: Signal<boolean>;
  /** Connects a reference again after a failure. */
  retry(): void;
}

export interface InjectViewOptions<TClient extends ViewClient> {
  /** Required outside an injection context. */
  readonly injector?: Injector;
  /** Releases a client this helper connected. Defaults to `client.dispose()`. */
  readonly release?: (client: TClient) => void;
}

/**
 * Connects a generated View reference for the lifetime of the injection
 * context (normally a component) and exposes its state as signals. Pass a
 * signal, such as a `page` input, to follow it: a changed reference releases
 * the previous client and connects the new one.
 *
 * A reference is identified by its `connect` function, so a signal producing a
 * new `{ connect: connectShell }` object does not reconnect.
 */
export function injectView<TClient extends ViewClient>(source: Signal<ViewSource<TClient>> | ViewSource<TClient>,
  options: InjectViewOptions<TClient> = {}): ViewHandle<TClient> {
  if (!options.injector) assertInInjectionContext(injectView);
  const injector = options.injector ?? inject(Injector);
  const controller = createViewController<TClient>(options.release ? { release: options.release } : {});
  const current = signal(controller.current);
  controller.subscribe(() => current.set(controller.current));
  const read = (): ViewSource<TClient> => isSignal(source) ? source() : source;
  const identity = computed(() => viewSourceIdentity(read()));
  const observed = computed(() => {
    const value = read();
    return value && isViewClient(value) ? value : undefined;
  });
  // A changed reference does not show the previous one until the effect follows it.
  const fresh = computed(() => viewSourceIdentity(current().source) === identity());

  effect(() => {
    identity();
    untracked(() => {
      controller.setSource(read());
      current.set(controller.current);
    });
  }, { injector });
  injector.get(DestroyRef).onDestroy(() => controller.dispose());

  return {
    // A connected client is readable before the effect observes it.
    state: computed(() => {
      const value = observed();
      if (value && value !== current().client) return value.snapshot;
      return fresh() ? current().state : undefined;
    }),
    client: computed(() => observed() ?? (fresh() ? current().client : undefined)),
    error: computed(() => fresh() ? current().error : undefined),
    pending: computed(() => observed() ? false : fresh() ? current().pending : identity() !== undefined),
    retry: () => controller.retry(),
  };
}
