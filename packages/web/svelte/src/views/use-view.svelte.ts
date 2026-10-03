import { untrack } from "svelte";

/** The framework-neutral surface of a generated `<Name>Client`. */
export interface ViewClient<TState = unknown> {
  readonly snapshot: TState;
  subscribe(listener: (state: TState) => void): () => void;
  dispose(): void;
}

/**
 * A generated content reference or `{ connect: connect<Name> }` for a root
 * ViewModel, which the helper connects and disposes, or an already connected
 * client, which it only observes.
 */
export type ViewSource<TClient extends ViewClient> = { connect(): Promise<TClient> } | TClient | null | undefined;

export interface ViewHandle<TClient extends ViewClient> {
  /** The latest state, or undefined until the client connects. */
  readonly state: TClient["snapshot"] | undefined;
  /** The connected client for commands, or undefined until it connects. */
  readonly client: TClient | undefined;
  /** The connection failure, if any. */
  readonly error: unknown;
  /** Connects a reference again after a failure. */
  retry(): void;
}

function isClient<TClient extends ViewClient>(source: { connect(): Promise<TClient> } | TClient): source is TClient {
  return typeof (source as Partial<ViewClient>).subscribe === "function";
}

/**
 * Connects a generated View reference while the calling component is mounted
 * and exposes its state as reactive properties. Call it during component
 * initialization with a getter, such as `useView(() => page)`, so a changed
 * prop disposes the previous client and connects the new one.
 *
 * A reference is identified by its `connect` function, so a getter returning a
 * new `{ connect: connectShell }` object does not reconnect.
 */
export function useView<TClient extends ViewClient>(source: () => ViewSource<TClient>): ViewHandle<TClient> {
  let state = $state.raw<TClient["snapshot"] | undefined>(undefined);
  let client = $state.raw<TClient | undefined>(undefined);
  let error = $state.raw<unknown>(undefined);
  let attempt = $state(0);
  const identity = $derived.by(() => {
    const value = source();
    return value ? isClient(value) ? value : value.connect : undefined;
  });

  $effect(() => {
    void identity;
    void attempt;
    const value = untrack(source);
    state = undefined;
    client = undefined;
    error = undefined;
    if (!value) return;
    let active = true;
    let connected: TClient | undefined;
    let unsubscribe: (() => void) | undefined;
    const observe = (next: TClient) => {
      client = next;
      unsubscribe = next.subscribe(update => { if (active) state = update; });
    };
    if (isClient(value)) observe(value);
    else value.connect().then(next => {
      if (!active) { next.dispose(); return; }
      connected = next;
      observe(next);
    }, cause => { if (active) error = cause; });
    return () => {
      active = false;
      unsubscribe?.();
      connected?.dispose();
    };
  });

  // A connected client is readable before the effect subscribes to it.
  const observed = () => {
    const value = source();
    return value && isClient(value) ? value : undefined;
  };
  return {
    get state() {
      const value = observed();
      return value && value !== client ? value.snapshot : state;
    },
    get client() { return observed() ?? client; },
    get error() { return error; },
    retry() { attempt++; },
  };
}
