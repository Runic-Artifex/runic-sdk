import { getCurrentScope, onScopeDispose, shallowRef, toValue, watch, type MaybeRefOrGetter, type Ref } from "vue";

/** The framework-neutral surface of a generated `<Name>Client`. */
export interface ViewClient<TState = unknown> {
  readonly snapshot: TState;
  subscribe(listener: (state: TState) => void): () => void;
  dispose(): void;
}

/** A generated content reference, or `{ connect: connect<Name> }` for a root ViewModel. */
export interface ViewReference<TClient extends ViewClient = ViewClient> {
  connect(): Promise<TClient>;
}

/**
 * A reference is connected and disposed by the composable. An already
 * connected client is only observed; its owner disposes it.
 */
export type ViewSource<TClient extends ViewClient> = ViewReference<TClient> | TClient | null | undefined;

export interface ViewHandle<TClient extends ViewClient> {
  /** The latest state, or undefined until the client connects. */
  readonly state: Readonly<Ref<TClient["snapshot"] | undefined>>;
  /** The connected client for commands, or undefined until it connects. */
  readonly client: Readonly<Ref<TClient | undefined>>;
  /** The connection failure, if any. */
  readonly error: Readonly<Ref<unknown>>;
  /** Connects a reference again after a failure. */
  retry(): void;
}

function isClient<TClient extends ViewClient>(source: ViewReference<TClient> | TClient): source is TClient {
  return typeof (source as Partial<ViewClient>).subscribe === "function";
}

/**
 * Connects a generated View reference while the current effect scope (for
 * example a component) is active and exposes its state as shallow refs. A
 * changed reference disposes the previous client; disposing the scope
 * disposes the current one.
 *
 * Pass a getter such as `() => props.page` to follow a prop. A reference is
 * identified by its `connect` function, so a getter returning a new
 * `{ connect: connectWorkspace }` object does not reconnect.
 */
export function useView<TClient extends ViewClient>(source: MaybeRefOrGetter<ViewSource<TClient>>): ViewHandle<TClient> {
  const state = shallowRef<TClient["snapshot"] | undefined>();
  const client = shallowRef<TClient | undefined>();
  const error = shallowRef<unknown>();
  let release: (() => void) | undefined;

  function start(): void {
    release?.();
    release = undefined;
    const value = toValue(source);
    state.value = undefined;
    client.value = undefined;
    error.value = undefined;
    if (!value) return;
    let active = true;
    let connected: TClient | undefined;
    let unsubscribe: (() => void) | undefined;
    const observe = (next: TClient) => {
      client.value = next;
      unsubscribe = next.subscribe(update => { if (active) state.value = update; });
    };
    release = () => {
      active = false;
      unsubscribe?.();
      connected?.dispose();
    };
    if (isClient(value)) observe(value);
    else value.connect().then(next => {
      if (!active) { next.dispose(); return; }
      connected = next;
      observe(next);
    }, cause => { if (active) error.value = cause; });
  }

  watch(() => {
    const value = toValue(source);
    return value ? isClient(value) ? value : value.connect : undefined;
  }, start, { immediate: true });
  if (getCurrentScope()) onScopeDispose(() => { release?.(); release = undefined; });
  return { state, client, error, retry: start };
}
