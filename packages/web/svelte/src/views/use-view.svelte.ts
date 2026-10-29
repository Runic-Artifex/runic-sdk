import { untrack } from "svelte";
import { createViewController, type ViewClient, type ViewConnector, type ViewSource } from "@runic-artifex/views";
import { isViewClient, viewSourceIdentity, viewSourcePreview } from "@runic-artifex/views/bindings";

export interface ViewHandle<TClient extends ViewClient> {
  /** The latest state, or undefined until the client connects. */
  readonly state: TClient["snapshot"] | undefined;
  /** The connected client for commands, or undefined until it connects. */
  readonly client: TClient | undefined;
  /** The connection failure, if any. */
  readonly error: unknown;
  /** True while a reference is connecting. */
  readonly pending: boolean;
  /** Connects a reference again after a failure. */
  retry(): void;
}

/**
 * Connects a generated View reference while the calling component is mounted
 * and exposes its state as reactive properties. Call it during component
 * initialization with a getter, such as `useView(() => page)`, so a changed
 * prop releases the previous client and connects the new one.
 *
 * A released client stays connected for `keepAlive` milliseconds (2000 by
 * default), and the next `useView` of the same reference takes it over: an
 * `{#if}` that toggles, HMR and A→B→A navigation show the View at once.
 *
 * A reference is identified by its `connect` function, so a getter returning a
 * new `{ connect: connectShell }` object does not reconnect.
 */
export interface UseViewOptions<TClient extends ViewClient> {
  /** Releases a client this helper connected. Defaults to `client.dispose()`. */
  readonly release?: (client: TClient) => void;
  /**
   * Milliseconds a client this helper connected stays connected after the component unmounts or the source changes,
   * for the next helper that follows the same reference (StrictMode, HMR, `<Activity>`, A→B→A navigation). `0`
   * disposes it at once. Defaults to 2000. Read once, when the helper is created.
   */
  readonly keepAlive?: number;
}

export function useView<TClient extends ViewClient>(source: () => ViewSource<TClient>, options: UseViewOptions<TClient> = {}): ViewHandle<TClient> {
  const controller = createViewController<TClient>({
    ...(options.release ? { release: options.release } : {}),
    ...(options.keepAlive === undefined ? {} : { keepAlive: options.keepAlive }),
  });
  let current = $state.raw(controller.current);
  const unsubscribe = controller.subscribe(() => { current = controller.current; });
  // A pre effect runs when it is created, so the teardown exists even if the
  // owner is destroyed before its first flush (see useOperation).
  $effect.pre(() => () => { unsubscribe(); controller.dispose(); });
  const identity = $derived(viewSourceIdentity(source()));

  $effect(() => {
    void identity;
    untrack(() => {
      controller.setSource(source());
      current = controller.current;
    });
  });

  // A connected client is readable before the effect observes it, and a changed reference does not show the
  // previous one until the effect follows it, but a client kept for it (keep-alive) instead.
  const preview = () => {
    const value = source();
    if (value && isViewClient(value)) return value === current.client ? undefined : viewSourcePreview(value);
    return fresh() ? undefined : viewSourcePreview(value);
  };
  const fresh = () => viewSourceIdentity(current.source) === identity;
  return {
    get state() { return preview()?.state ?? (fresh() ? current.state : undefined); },
    get client() { return preview()?.client ?? (fresh() ? current.client : undefined); },
    get error() { return fresh() ? current.error : undefined; },
    get pending() {
      if (preview()) return false;
      return fresh() ? current.pending : identity !== undefined;
    },
    retry() { controller.retry(); },
  };
}

export interface SuspenseViewHandle<TClient extends ViewClient> {
  /** The latest state. */
  readonly state: TClient["snapshot"];
  /** The connected client. */
  readonly client: TClient;
}

/**
 * Connects a generated View reference while the calling component is mounted, like {@link useView}, and returns a
 * promise that resolves once it connected, with reactive `state` and `client` that are never `undefined`, or
 * rejects with the connection failure. Call it during component initialization and await it in the markup:
 *
 * ```svelte
 * <script lang="ts">
 *   let { page }: { page: CounterPageReference } = $props();
 *   const counter = useSuspenseView(page);
 * </script>
 *
 * {#await counter}
 *   <p>Connecting…</p>
 * {:then view}
 *   <button onclick={() => view.client.increment()}>{view.state.count}</button>
 * {:catch error}
 *   <p role="alert">{describeBridgeError(error)}</p>
 * {/await}
 * ```
 *
 * The source is read once; key the component (`{#key page}`) to follow another reference. A reference whose client
 * another component let go of within its `keepAlive` period resolves at once.
 */
export function useSuspenseView<TClient extends ViewClient>(source: ViewConnector<TClient> | TClient,
  options: UseViewOptions<TClient> = {}): Promise<SuspenseViewHandle<TClient>> {
  const view = useView<TClient>(() => source, options);
  let settle!: () => void;
  const settled = new Promise<void>(resolve => { settle = resolve; });
  // Reads the handle in an effect, so the promise settles once the effect followed the source.
  $effect.pre(() => { if (!view.pending) settle(); });
  return settled.then(() => {
    if (view.error !== undefined || view.client === undefined) throw view.error;
    const client = view.client;
    return { get state() { return view.state as TClient["snapshot"]; }, client };
  });
}
