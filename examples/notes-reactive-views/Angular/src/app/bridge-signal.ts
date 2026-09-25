import { effect, signal, type Signal } from "@angular/core";

interface Readable<S> {
  readonly snapshot: S;
  subscribe(listener: (state: S) => void): () => void;
}
interface PageReference<V> { connect(): Promise<V>; }
interface PageView<S> extends Readable<S> { dispose(): void; }

export function bridgeSignal<V extends Readable<any>>(source: Signal<V>) {
  const state = signal<V["snapshot"] | undefined>(undefined);
  effect(onCleanup => {
    const view = source();
    state.set(view.snapshot);
    onCleanup(view.subscribe(next => state.set(next)));
  });
  return state.asReadonly();
}

export function pageSignal<V extends PageView<any>>(reference: Signal<PageReference<V>>) {
  const state = signal<V["snapshot"] | undefined>(undefined);
  const view = signal<V | undefined>(undefined);
  const error = signal<unknown>(undefined);
  const attempt = signal(0);
  effect(onCleanup => {
    attempt();
    const page = reference();
    let active = true;
    let connected: V | undefined;
    let unsubscribe: (() => void) | undefined;
    state.set(undefined);
    view.set(undefined);
    error.set(undefined);
    void page.connect().then(next => {
      if (!active) { next.dispose(); return; }
      try {
        unsubscribe = next.subscribe(value => state.set(value));
        connected = next;
        view.set(next);
      } catch (cause) { next.dispose(); throw cause; }
    }).catch(cause => { if (active) error.set(cause); });
    onCleanup(() => {
      active = false;
      unsubscribe?.();
      connected?.dispose();
    });
  });
  return { state: state.asReadonly(), view: view.asReadonly(), error: error.asReadonly(), retry: () => attempt.update(value => value + 1) };
}
