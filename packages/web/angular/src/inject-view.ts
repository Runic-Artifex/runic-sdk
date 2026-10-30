import { assertInInjectionContext, computed, DestroyRef, effect, inject, Injector, isSignal, signal, untracked, type Signal } from "@angular/core";
import { createViewController, type ViewClient, type ViewSource } from "@runic-artifex/views";
import { isViewClient, viewSourceIdentity, viewSourcePreview } from "@runic-artifex/views/bindings";

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
  /**
   * Milliseconds a client this helper connected stays connected after the component unmounts or the source changes,
   * for the next helper that follows the same reference (StrictMode, HMR, `<Activity>`, A→B→A navigation). `0`
   * disposes it at once. Defaults to 2000. Read once, when the helper is created.
   */
  readonly keepAlive?: number;
}

/**
 * Connects a generated View reference for the lifetime of the injection
 * context (normally a component) and exposes its state as signals. Pass a
 * signal, such as a `page` input, to follow it: a changed reference releases
 * the previous client and connects the new one.
 *
 * A released client stays connected for `keepAlive` milliseconds (2000 by
 * default), and the next `injectView` of the same reference takes it over with
 * its state: an `@if` that toggles, HMR and A→B→A navigation show the View at
 * once instead of opening the route again.
 *
 * A reference is identified by its `connect` function, so a signal producing a
 * new `{ connect: connectShell }` object does not reconnect.
 */
export function injectView<TClient extends ViewClient>(source: Signal<ViewSource<TClient>> | ViewSource<TClient>,
  options: InjectViewOptions<TClient> = {}): ViewHandle<TClient> {
  if (!options.injector) assertInInjectionContext(injectView);
  const injector = options.injector ?? inject(Injector);
  const controller = createViewController<TClient>({
    ...(options.release ? { release: options.release } : {}),
    ...(options.keepAlive === undefined ? {} : { keepAlive: options.keepAlive }),
  });
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

  // A connected client is readable before the effect observes it, and so is a client kept for the reference
  // (keep-alive), so a component that is created again shows its View at once.
  const preview = computed(() => {
    const value = observed();
    if (value) return value === current().client ? undefined : viewSourcePreview(value);
    return fresh() ? undefined : viewSourcePreview(read());
  });
  return {
    state: computed(() => preview()?.state ?? (fresh() ? current().state : undefined)),
    client: computed(() => preview()?.client ?? (fresh() ? current().client : undefined)),
    error: computed(() => fresh() ? current().error : undefined),
    pending: computed(() => preview() ? false : fresh() ? current().pending : identity() !== undefined),
    retry: () => controller.retry(),
  };
}
