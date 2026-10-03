import { assertInInjectionContext, computed, effect, inject, Injector, isSignal, signal, untracked, type Signal } from "@angular/core";

/** The framework-neutral surface of a generated `<Name>Client`. */
export interface ViewClient<TState = unknown> {
  readonly snapshot: TState;
  subscribe(listener: (state: TState) => void): () => void;
  dispose(): void;
}

/**
 * A generated content reference or `{ connect: connect<Name> }` for a root
 * ViewModel, which `injectView` connects and disposes, or an already connected
 * client, which it only observes.
 */
export type ViewSource<TClient extends ViewClient> = { connect(): Promise<TClient> } | TClient | null | undefined;

export interface ViewHandle<TClient extends ViewClient> {
  /** The latest state, or undefined until the client connects. */
  readonly state: Signal<TClient["snapshot"] | undefined>;
  /** The connected client for commands, or undefined until it connects. */
  readonly client: Signal<TClient | undefined>;
  /** The connection failure, if any. */
  readonly error: Signal<unknown>;
  /** Connects a reference again after a failure. */
  retry(): void;
}

export interface InjectViewOptions<TClient extends ViewClient> {
  /** Required outside an injection context. */
  readonly injector?: Injector;
  /** Releases a client this helper connected. Defaults to `client.dispose()`. */
  readonly release?: (client: TClient) => void;
}

function isClient<TClient extends ViewClient>(source: { connect(): Promise<TClient> } | TClient): source is TClient {
  return typeof (source as Partial<ViewClient>).subscribe === "function";
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
  const release = options.release ?? ((client: TClient) => client.dispose());
  const read = (): ViewSource<TClient> => isSignal(source) ? source() : source;
  const identity = computed(() => {
    const value = read();
    return value ? isClient(value) ? value : value.connect : undefined;
  });
  const observed = computed(() => {
    const value = read();
    return value && isClient(value) ? value : undefined;
  });
  const attempt = signal(0);
  const latest = signal<TClient["snapshot"] | undefined>(undefined);
  const connectedClient = signal<TClient | undefined>(undefined);
  const error = signal<unknown>(undefined);

  effect(onCleanup => {
    identity();
    attempt();
    const value = untracked(read);
    latest.set(undefined);
    connectedClient.set(undefined);
    error.set(undefined);
    if (!value) return;
    let active = true;
    let connected: TClient | undefined;
    let unsubscribe: (() => void) | undefined;
    const observe = (next: TClient) => {
      connectedClient.set(next);
      unsubscribe = next.subscribe(update => { if (active) latest.set(update); });
    };
    if (isClient(value)) observe(value);
    else value.connect().then(next => {
      if (!active) { release(next); return; }
      connected = next;
      observe(next);
    }, cause => { if (active) error.set(cause); });
    onCleanup(() => {
      active = false;
      unsubscribe?.();
      if (connected) release(connected);
    });
  }, { injector });

  return {
    // A connected client is readable before the effect subscribes to it.
    state: computed(() => {
      const value = observed();
      return value && value !== connectedClient() ? value.snapshot : latest();
    }),
    client: computed(() => observed() ?? connectedClient()),
    error: error.asReadonly(),
    retry: () => attempt.update(value => value + 1),
  };
}
