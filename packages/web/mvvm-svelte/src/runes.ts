import { createSubscriber } from "svelte/reactivity";
import { get, type Readable } from "svelte/store";

export interface SvelteMvvmRune<T> {
  /** Reading this getter inside a Svelte 5 effect or derived rune is reactive. */
  readonly current: T;
}

/**
 * Adapts any toolkit readable to a Svelte 5 getter backed by
 * `createSubscriber`. Svelte 4 consumers should continue using `$store`.
 */
export function toSvelteMvvmRune<T>(store: Readable<T>): SvelteMvvmRune<T> {
  let current = get(store);
  const track = createSubscriber((update) => store.subscribe((value) => {
    current = value;
    update();
  }));
  return Object.freeze({
    get current(): T {
      track();
      return current;
    },
  });
}
