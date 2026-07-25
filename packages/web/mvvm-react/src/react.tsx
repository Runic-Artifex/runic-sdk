import type {
  JsonValue,
  MemberIdentifier,
  MvvmProjectedCommandState,
  MvvmProjectionSnapshot,
} from "@webuitoolkit/mvvm";
import {
  createContext,
  createElement,
  useContext,
  useEffect,
  useSyncExternalStore,
  type ReactNode,
} from "react";

import type { ReactMvvmStore } from "./store.js";

const ReactMvvmContext = createContext<ReactMvvmStore | null>(null);
const ownedStoreLeases = new WeakMap<ReactMvvmStore, { owners: number; generation: number }>();

export interface ReactMvvmProviderProps {
  readonly store: ReactMvvmStore;
  readonly children?: ReactNode;
  /**
   * Disposes store on unmount. This is false by default so a store can be
   * shared by multiple roots without the provider taking implicit ownership.
   */
  readonly ownsStore?: boolean;
}

export function ReactMvvmProvider({
  store,
  children,
  ownsStore = false,
}: ReactMvvmProviderProps) {
  useEffect(() => {
    if (!ownsStore) return;
    return acquireStoreOwnership(store);
  }, [ownsStore, store]);
  return createElement(ReactMvvmContext.Provider, { value: store }, children);
}

export function useReactMvvmStore(): ReactMvvmStore {
  const store = useContext(ReactMvvmContext);
  if (store === null) {
    throw new Error("React MVVM hooks require a ReactMvvmProvider.");
  }
  return store;
}

export function useMvvmSnapshot(): MvvmProjectionSnapshot {
  const store = useReactMvvmStore();
  return useSyncExternalStore(store.subscribe, store.getSnapshot, store.getServerSnapshot);
}

export function useMvvmProperty(member: MemberIdentifier): JsonValue | undefined {
  return useMvvmSnapshot().properties.get(member);
}

export function useMvvmCollection(member: MemberIdentifier): readonly JsonValue[] | undefined {
  return useMvvmSnapshot().collections.get(member);
}

export function useMvvmCommand(
  member: MemberIdentifier,
): Readonly<MvvmProjectedCommandState> | undefined {
  return useMvvmSnapshot().commands.get(member);
}

export function useMvvmValidation(member: MemberIdentifier): readonly string[] | undefined {
  return useMvvmSnapshot().validation.get(member);
}

function acquireStoreOwnership(store: ReactMvvmStore): () => void {
  const lease = ownedStoreLeases.get(store) ?? { owners: 0, generation: 0 };
  lease.owners += 1;
  ownedStoreLeases.set(store, lease);
  let released = false;
  return () => {
    if (released) return;
    released = true;
    lease.owners -= 1;
    const generation = ++lease.generation;
    // StrictMode immediately replays effects. Deferring to the microtask
    // boundary distinguishes that replay from a real final unmount.
    queueMicrotask(() => {
      if (lease.owners !== 0 || lease.generation !== generation) return;
      ownedStoreLeases.delete(store);
      store.dispose();
    });
  };
}
