import { untrack } from "svelte";
import { createViewController, isViewClient, viewSourceIdentity, type ViewClient, type ViewSource } from "@runic-artifex/views";

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
 * prop disposes the previous client and connects the new one.
 *
 * A reference is identified by its `connect` function, so a getter returning a
 * new `{ connect: connectShell }` object does not reconnect.
 */
export function useView<TClient extends ViewClient>(source: () => ViewSource<TClient>): ViewHandle<TClient> {
  const controller = createViewController<TClient>();
  let current = $state.raw(controller.current);
  controller.subscribe(() => { current = controller.current; });
  const identity = $derived(viewSourceIdentity(source()));

  $effect(() => {
    void identity;
    untrack(() => {
      controller.setSource(source());
      current = controller.current;
    });
  });
  $effect(() => () => controller.dispose());

  // A connected client is readable before the effect observes it, and a
  // changed reference does not show the previous one until the effect follows it.
  const observed = () => {
    const value = source();
    return value && isViewClient(value) ? value : undefined;
  };
  const fresh = () => viewSourceIdentity(current.source) === identity;
  return {
    get state() {
      const value = observed();
      if (value && value !== current.client) return value.snapshot;
      return fresh() ? current.state : undefined;
    },
    get client() { return observed() ?? (fresh() ? current.client : undefined); },
    get error() { return fresh() ? current.error : undefined; },
    get pending() {
      if (observed()) return false;
      return fresh() ? current.pending : identity !== undefined;
    },
    retry() { controller.retry(); },
  };
}
