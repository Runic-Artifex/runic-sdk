import type {
  JsonValue,
  MemberIdentifier,
  MvvmCollection,
  MvvmCommand,
  MvvmCommandExecution,
  MvvmCommandExecutionSnapshot,
  MvvmCommandWithArgument,
  MvvmProperty,
  MvvmProjectedCommandState,
  MvvmProjectionSnapshot,
  MvvmReadonlyProperty,
  CancelResult,
} from "@webuitoolkit/mvvm";
import { createMvvmCommandExecution } from "@webuitoolkit/mvvm";
import {
  createContext,
  createElement,
  useContext,
  useEffect,
  useMemo,
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

export function useMvvmProperty<T>(
  property: MvvmReadonlyProperty<T> | MvvmProperty<T>,
): T | undefined;
export function useMvvmProperty(member: MemberIdentifier): JsonValue | undefined;
export function useMvvmProperty<T>(
  memberOrProperty: MemberIdentifier | MvvmReadonlyProperty<T> | MvvmProperty<T>,
): JsonValue | T | undefined {
  const snapshot = useMvvmSnapshot();
  return typeof memberOrProperty === "number"
    ? snapshot.properties.get(memberOrProperty)
    : memberOrProperty.from(snapshot);
}

export function useMvvmCollection<T>(collection: MvvmCollection<T>): readonly T[];
export function useMvvmCollection(member: MemberIdentifier): readonly JsonValue[] | undefined;
export function useMvvmCollection<T>(
  memberOrCollection: MemberIdentifier | MvvmCollection<T>,
): readonly JsonValue[] | readonly T[] | undefined {
  const snapshot = useMvvmSnapshot();
  return typeof memberOrCollection === "number"
    ? snapshot.collections.get(memberOrCollection)
    : memberOrCollection.from(snapshot);
}

export function useMvvmCommand<TResult>(
  command: MvvmCommand<TResult>,
): Readonly<MvvmProjectedCommandState> | undefined;
export function useMvvmCommand<TArgument, TResult>(
  command: MvvmCommandWithArgument<TArgument, TResult>,
): Readonly<MvvmProjectedCommandState> | undefined;
export function useMvvmCommand(
  member: MemberIdentifier,
): Readonly<MvvmProjectedCommandState> | undefined;
export function useMvvmCommand(
  memberOrCommand:
    | MemberIdentifier
    | MvvmCommand<unknown>
    | MvvmCommandWithArgument<unknown, unknown>,
): Readonly<MvvmProjectedCommandState> | undefined {
  const member = typeof memberOrCommand === "number"
    ? memberOrCommand
    : memberOrCommand.member;
  return useMvvmSnapshot().commands.get(member);
}

export interface ReactMvvmCommandFacade<
  TArgument = void,
  TResult extends JsonValue = JsonValue,
> extends MvvmCommandExecutionSnapshot<TResult> {
  readonly canExecute: boolean;
  readonly projectedRunning: boolean;
  execute: MvvmCommandExecution<TArgument, TResult>["execute"];
  cancel(): Promise<CancelResult | undefined>;
  reset(): void;
}

/**
 * Composes projected command state with per-invocation result, failure,
 * cancellation, and transition state.
 */
export function useMvvmCommandFacade<TResult>(
  command: MvvmCommand<TResult>,
): ReactMvvmCommandFacade<void, TResult & JsonValue>;
export function useMvvmCommandFacade<TArgument, TResult>(
  command: MvvmCommandWithArgument<TArgument, TResult>,
): ReactMvvmCommandFacade<TArgument, TResult & JsonValue>;
export function useMvvmCommandFacade<TArgument, TResult>(
  command:
    | MvvmCommand<TResult>
    | MvvmCommandWithArgument<TArgument, TResult>,
): ReactMvvmCommandFacade<TArgument, TResult & JsonValue> {
  const execution = useMemo(
    () => createMvvmCommandExecution(
      command as MvvmCommandWithArgument<TArgument, TResult & JsonValue>,
    ),
    [command],
  );
  useEffect(() => () => execution.dispose(), [execution]);
  const lifecycle = useSyncExternalStore(
    execution.subscribe.bind(execution),
    () => execution.snapshot,
    () => execution.snapshot,
  );
  const projected = useMvvmCommand(command.member);
  return {
    ...lifecycle,
    canExecute: projected?.canExecute === true,
    projectedRunning: projected?.isExecuting === true,
    execute: execution.execute,
    cancel: () => execution.cancel(),
    reset: () => execution.reset(),
  };
}

export function useMvvmValidation<T>(
  binding: MvvmReadonlyProperty<T> | MvvmProperty<T> | MvvmCollection<T>,
): readonly string[] | undefined;
export function useMvvmValidation(member: MemberIdentifier): readonly string[] | undefined;
export function useMvvmValidation<T>(
  memberOrBinding:
    | MemberIdentifier
    | MvvmReadonlyProperty<T>
    | MvvmProperty<T>
    | MvvmCollection<T>,
): readonly string[] | undefined {
  const member = typeof memberOrBinding === "number"
    ? memberOrBinding
    : memberOrBinding.member;
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
