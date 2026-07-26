import {
  computed,
  getCurrentScope,
  inject,
  onScopeDispose,
  provide,
  shallowReadonly,
  shallowRef,
  type ComputedRef,
  type InjectionKey,
  type ShallowRef,
} from "vue";
import type {
  JsonValue,
  MemberIdentifier,
  MvvmCollection,
  MvvmCommand,
  MvvmCommandWithArgument,
  MvvmProperty,
  MvvmProjectedCommandInvocation,
  MvvmProjectedCommandState,
  MvvmProjection,
  MvvmProjectionEvent,
  MvvmProjectionSnapshot,
  MvvmReadonlyProperty,
} from "@webuitoolkit/mvvm";

export interface VueMvvmAdapterOptions {
  /**
   * Dispose the supplied projection when the adapter is disposed.
   * The default is false because projections are commonly shared.
   */
  readonly ownsProjection?: boolean;
}

export interface VueMvvmAdapter {
  /** The latest projection snapshot. A state event replaces this ref atomically. */
  readonly state: Readonly<ShallowRef<MvvmProjectionSnapshot>>;
  readonly disposed: Readonly<ShallowRef<boolean>>;
  property(member: MemberIdentifier): ComputedRef<JsonValue | undefined>;
  collection(member: MemberIdentifier): ComputedRef<readonly JsonValue[] | undefined>;
  command(member: MemberIdentifier): ComputedRef<Readonly<MvvmProjectedCommandState> | undefined>;
  validation(member: MemberIdentifier): ComputedRef<readonly string[] | undefined>;
  subscribe(listener: (event: MvvmProjectionEvent) => void): () => void;
  setProperty(
    member: MemberIdentifier,
    value: JsonValue,
  ): ReturnType<MvvmProjection["setProperty"]>;
  execute<T extends JsonValue = JsonValue>(
    member: MemberIdentifier,
    options?: Readonly<{ argument?: JsonValue }>,
  ): MvvmProjectedCommandInvocation<T>;
  dispose(): void;
}

/** Stable key used by {@link provideVueMvvmAdapter} and {@link useVueMvvm}. */
export const vueMvvmKey: InjectionKey<VueMvvmAdapter> = Symbol("webuitoolkit.mvvm.vue");

/**
 * Creates a Vue adapter over the public framework-neutral projection.
 *
 * The adapter owns only its projection subscription unless `ownsProjection`
 * is explicitly enabled.
 */
export function createVueMvvmAdapter(
  projection: MvvmProjection,
  options: VueMvvmAdapterOptions = {},
): VueMvvmAdapter {
  const stateSource = shallowRef(projection.snapshot);
  const disposedSource = shallowRef(false);
  const listeners = new Set<(event: MvvmProjectionEvent) => void>();
  const properties = new Map<MemberIdentifier, ComputedRef<JsonValue | undefined>>();
  const collections = new Map<MemberIdentifier, ComputedRef<readonly JsonValue[] | undefined>>();
  const commands = new Map<MemberIdentifier, ComputedRef<Readonly<MvvmProjectedCommandState> | undefined>>();
  const validation = new Map<MemberIdentifier, ComputedRef<readonly string[] | undefined>>();

  const unsubscribeProjection = projection.subscribe((event) => {
    if (disposedSource.value) return;
    if (event.type === "state") stateSource.value = event.snapshot;
    for (const listener of [...listeners]) {
      try {
        listener(event);
      } catch {
        // A view subscriber cannot interrupt another subscriber or projection dispatch.
      }
    }
  });

  function assertActive(): void {
    if (disposedSource.value) throw new Error("The Vue MVVM adapter has been disposed.");
  }

  const adapter: VueMvvmAdapter = {
    state: shallowReadonly(stateSource),
    disposed: shallowReadonly(disposedSource),
    property(member) {
      assertActive();
      return cached(properties, member, () => computed(() => stateSource.value.properties.get(member)));
    },
    collection(member) {
      assertActive();
      return cached(collections, member, () => computed(() => stateSource.value.collections.get(member)));
    },
    command(member) {
      assertActive();
      return cached(commands, member, () => computed(() => stateSource.value.commands.get(member)));
    },
    validation(member) {
      assertActive();
      return cached(validation, member, () => computed(() => stateSource.value.validation.get(member)));
    },
    subscribe(listener) {
      assertActive();
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    setProperty(member, value) {
      assertActive();
      return projection.setProperty(member, value);
    },
    execute<T extends JsonValue = JsonValue>(
      member: MemberIdentifier,
      executeOptions: Readonly<{ argument?: JsonValue }> = {},
    ): MvvmProjectedCommandInvocation<T> {
      assertActive();
      return projection.execute<T>(member, executeOptions);
    },
    dispose() {
      if (disposedSource.value) return;
      disposedSource.value = true;
      unsubscribeProjection();
      listeners.clear();
      properties.clear();
      collections.clear();
      commands.clear();
      validation.clear();
      if (options.ownsProjection === true) projection.dispose();
    },
  };
  return Object.freeze(adapter);
}

/**
 * Creates an adapter owned by the current Vue effect scope. Stopping the scope
 * (including component unmount) disposes the adapter.
 */
export function createScopedVueMvvmAdapter(
  projection: MvvmProjection,
  options: VueMvvmAdapterOptions = {},
): VueMvvmAdapter {
  if (getCurrentScope() === undefined) {
    throw new Error("createScopedVueMvvmAdapter must run inside an active Vue effect scope.");
  }
  const adapter = createVueMvvmAdapter(projection, options);
  onScopeDispose(() => adapter.dispose());
  return adapter;
}

/**
 * Creates and provides a scope-owned adapter. The adapter is disposed when the
 * providing component/effect scope stops.
 */
export function provideVueMvvm(
  projection: MvvmProjection,
  options: VueMvvmAdapterOptions = {},
): VueMvvmAdapter {
  const adapter = createScopedVueMvvmAdapter(projection, options);
  provide(vueMvvmKey, adapter);
  return adapter;
}

/** Provides a caller-owned adapter. This function never disposes it. */
export function provideVueMvvmAdapter(adapter: VueMvvmAdapter): VueMvvmAdapter {
  provide(vueMvvmKey, adapter);
  return adapter;
}

/** Returns the nearest provided adapter, failing clearly when none exists. */
export function useVueMvvm(): VueMvvmAdapter {
  const adapter = inject(vueMvvmKey);
  if (adapter === undefined) {
    throw new Error("No Vue MVVM adapter was provided.");
  }
  return adapter;
}

/** Adapts a generated property handle to a typed Vue computed ref. */
export function toVueMvvmProperty<T>(
  adapter: VueMvvmAdapter,
  property: MvvmReadonlyProperty<T> | MvvmProperty<T>,
): ComputedRef<T | undefined> {
  return adapter.property(property.member) as ComputedRef<T | undefined>;
}

/** Adapts a generated collection handle to a typed Vue computed ref. */
export function toVueMvvmCollection<T>(
  adapter: VueMvvmAdapter,
  collection: MvvmCollection<T>,
): ComputedRef<readonly T[]> {
  if (adapter.disposed.value) throw new Error("The Vue MVVM adapter has been disposed.");
  return computed(() => collection.from(adapter.state.value));
}

/** Adapts a generated command handle to its reactive command-state ref. */
export function toVueMvvmCommand<TResult>(
  adapter: VueMvvmAdapter,
  command: MvvmCommand<TResult>,
): ComputedRef<Readonly<MvvmProjectedCommandState> | undefined>;
export function toVueMvvmCommand<TArgument, TResult>(
  adapter: VueMvvmAdapter,
  command: MvvmCommandWithArgument<TArgument, TResult>,
): ComputedRef<Readonly<MvvmProjectedCommandState> | undefined>;
export function toVueMvvmCommand(
  adapter: VueMvvmAdapter,
  command: MvvmCommand<unknown> | MvvmCommandWithArgument<unknown, unknown>,
): ComputedRef<Readonly<MvvmProjectedCommandState> | undefined> {
  return adapter.command(command.member);
}

/** Adapts generated property/collection validation to a reactive ref. */
export function toVueMvvmValidation<T>(
  adapter: VueMvvmAdapter,
  binding: MvvmReadonlyProperty<T> | MvvmProperty<T> | MvvmCollection<T>,
): ComputedRef<readonly string[] | undefined> {
  return adapter.validation(binding.member);
}

/** Injects the current adapter and returns a typed generated-property ref. */
export function useVueMvvmProperty<T>(
  property: MvvmReadonlyProperty<T> | MvvmProperty<T>,
): ComputedRef<T | undefined> {
  return toVueMvvmProperty(useVueMvvm(), property);
}

/** Injects the current adapter and returns a typed generated-collection ref. */
export function useVueMvvmCollection<T>(
  collection: MvvmCollection<T>,
): ComputedRef<readonly T[]> {
  return toVueMvvmCollection(useVueMvvm(), collection);
}

/** Injects the current adapter and returns a generated command-state ref. */
export function useVueMvvmCommand<TResult>(
  command: MvvmCommand<TResult>,
): ComputedRef<Readonly<MvvmProjectedCommandState> | undefined>;
export function useVueMvvmCommand<TArgument, TResult>(
  command: MvvmCommandWithArgument<TArgument, TResult>,
): ComputedRef<Readonly<MvvmProjectedCommandState> | undefined>;
export function useVueMvvmCommand(
  command: MvvmCommand<unknown> | MvvmCommandWithArgument<unknown, unknown>,
): ComputedRef<Readonly<MvvmProjectedCommandState> | undefined> {
  return useVueMvvm().command(command.member);
}

/** Injects the current adapter and returns validation for a generated handle. */
export function useVueMvvmValidation<T>(
  binding: MvvmReadonlyProperty<T> | MvvmProperty<T> | MvvmCollection<T>,
): ComputedRef<readonly string[] | undefined> {
  return toVueMvvmValidation(useVueMvvm(), binding);
}

function cached<T>(
  cache: Map<MemberIdentifier, ComputedRef<T>>,
  member: MemberIdentifier,
  factory: () => ComputedRef<T>,
): ComputedRef<T> {
  const existing = cache.get(member);
  if (existing !== undefined) return existing;
  const created = factory();
  cache.set(member, created);
  return created;
}
