import { createSubscriber } from "svelte/reactivity";

interface Readable<S> {
  readonly snapshot: S;
  subscribe(listener: (state: S) => void): () => void;
}

interface PageReference<V> {
  connect(): Promise<V>;
}

interface PageView<S> extends Readable<S> {
  dispose(): void;
}

export function bridgeState<S>(view: Readable<S>) {
  let current = view.snapshot;
  const track = createSubscriber(update => view.subscribe(next => {
    current = next;
    update();
  }));
  return {
    get current(): S {
      track();
      return current;
    },
  };
}

export function pageState<V extends PageView<any>>(getReference: () => PageReference<V>) {
  let state: V["snapshot"] | undefined;
  let view: V | undefined;
  let error: unknown;
  let generation = 0;
  const track = createSubscriber(update => {
    const reference = getReference();
    const connectionGeneration = ++generation;
    let active = true;
    let unsubscribe: (() => void) | undefined;
    let connectedView: V | undefined;
    state = undefined;
    view = undefined;
    error = undefined;
    void reference.connect().then(connected => {
      if (!active || generation !== connectionGeneration) {
        connected.dispose();
        return;
      }
      try {
        unsubscribe = connected.subscribe(next => {
          if (!active || generation !== connectionGeneration) return;
          state = next;
          update();
        });
        connectedView = connected;
        view = connected;
      } catch (cause) {
        connected.dispose();
        throw cause;
      }
      update();
    }).catch(cause => {
      if (!active || generation !== connectionGeneration) return;
      error = cause;
      update();
    });
    return () => {
      active = false;
      unsubscribe?.();
      connectedView?.dispose();
      if (generation === connectionGeneration) {
        generation++;
        state = undefined;
        view = undefined;
        error = undefined;
      }
    };
  });
  return {
    get state(): V["snapshot"] | undefined {
      track();
      return state;
    },
    get view(): V | undefined {
      track();
      return view;
    },
    get error(): unknown {
      track();
      return error;
    },
  };
}
