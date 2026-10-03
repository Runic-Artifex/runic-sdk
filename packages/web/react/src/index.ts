import { useCallback, useEffect, useState, useSyncExternalStore } from "react";

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
 * A reference is connected and disposed by the hook. An already connected
 * client is only observed; its owner disposes it.
 */
export type ViewSource<TClient extends ViewClient> = ViewReference<TClient> | TClient | null | undefined;

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

function isClient<TClient extends ViewClient>(source: ViewReference<TClient> | TClient): source is TClient {
  return typeof (source as Partial<ViewClient>).subscribe === "function";
}

/**
 * Connects a generated View reference for the lifetime of the component and
 * renders its state through `useSyncExternalStore`. A changed reference
 * disposes the previous client; StrictMode's extra mount disposes its
 * connection when it resolves.
 *
 * A reference is identified by its `connect` function, so an inline
 * `{ connect: connectWorkspace }` does not reconnect on every render.
 */
export function useView<TClient extends ViewClient>(source: ViewSource<TClient>): ViewHandle<TClient> {
  const observed = source && isClient(source) ? source : undefined;
  const reference = source && !isClient(source) ? source : undefined;
  const connect = reference?.connect;
  const [attempt, setAttempt] = useState(0);
  const [connection, setConnection] = useState<{
    readonly connect: unknown; readonly attempt: number; readonly client?: TClient; readonly error?: unknown;
  }>();

  useEffect(() => {
    if (!reference) return;
    let active = true;
    let connected: TClient | undefined;
    reference.connect().then(client => {
      if (!active) { client.dispose(); return; }
      connected = client;
      setConnection({ connect, attempt, client });
    }, error => {
      if (active) setConnection({ connect, attempt, error });
    });
    return () => {
      active = false;
      connected?.dispose();
    };
    // An inline reference object is recreated on each render; its connect function identifies it.
  }, [connect, attempt]);

  const current = connection && connection.connect === connect && connection.attempt === attempt ? connection : undefined;
  const client = observed ?? current?.client;
  const subscribe = useCallback(
    (onChange: () => void) => client ? client.subscribe(() => onChange()) : () => {},
    [client]);
  const snapshot = useCallback(() => client?.snapshot as TClient["snapshot"] | undefined, [client]);
  const state = useSyncExternalStore(subscribe, snapshot, snapshot);
  const retry = useCallback(() => setAttempt(value => value + 1), []);
  return { state, client, error: observed ? undefined : current?.error, retry };
}
