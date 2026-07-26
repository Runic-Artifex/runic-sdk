import {
  InjectionToken,
  computed,
  inject,
  signal,
  type Provider,
  type Signal,
} from "@angular/core";
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

export interface AngularMvvmStoreOptions {
  /** Dispose the framework-neutral projection with this store. */
  readonly ownsProjection?: boolean;
}

/** Signal-backed Angular facade over the frozen framework-neutral projection. */
export class AngularMvvmStore {
  private readonly stateSource;
  private readonly listeners = new Set<(event: MvvmProjectionEvent) => void>();
  private readonly propertySignals = new Map<MemberIdentifier, Signal<JsonValue | undefined>>();
  private readonly collectionSignals = new Map<MemberIdentifier, Signal<readonly JsonValue[] | undefined>>();
  private readonly typedCollectionSignals =
    new WeakMap<object, Signal<readonly unknown[]>>();
  private readonly commandSignals =
    new Map<MemberIdentifier, Signal<Readonly<MvvmProjectedCommandState> | undefined>>();
  private readonly validationSignals = new Map<MemberIdentifier, Signal<readonly string[] | undefined>>();
  private readonly unsubscribeProjection: () => void;
  private disposed = false;

  /** Latest accepted snapshot; one protocol state event causes one signal replacement. */
  public readonly snapshot: Signal<MvvmProjectionSnapshot>;

  public constructor(
    private readonly projection: MvvmProjection,
    private readonly options: Readonly<AngularMvvmStoreOptions> = {},
  ) {
    this.stateSource = signal(projection.snapshot, { equal: Object.is });
    this.snapshot = this.stateSource.asReadonly();
    this.unsubscribeProjection = projection.subscribe((event) => {
      if (this.disposed) return;
      if (event.type === "state") this.stateSource.set(event.snapshot);
      for (const listener of [...this.listeners]) {
        try {
          listener(event);
        } catch {
          // A failed view listener may not suppress sibling subscribers.
        }
      }
    });
  }

  public property<T>(
    property: MvvmReadonlyProperty<T> | MvvmProperty<T>,
  ): Signal<T | undefined>;
  public property(member: MemberIdentifier): Signal<JsonValue | undefined>;
  public property<T>(
    memberOrProperty: MemberIdentifier | MvvmReadonlyProperty<T> | MvvmProperty<T>,
  ): Signal<JsonValue | T | undefined> {
    this.assertActive();
    const member = toMember(memberOrProperty);
    return cached(this.propertySignals, member, () => computed(
      () => this.stateSource().properties.get(member),
    )) as Signal<JsonValue | T | undefined>;
  }

  public collection<T>(collection: MvvmCollection<T>): Signal<readonly T[]>;
  public collection(member: MemberIdentifier): Signal<readonly JsonValue[] | undefined>;
  public collection<T>(
    memberOrCollection: MemberIdentifier | MvvmCollection<T>,
  ): Signal<readonly JsonValue[] | readonly T[] | undefined> {
    this.assertActive();
    if (typeof memberOrCollection !== "number") {
      const existing = this.typedCollectionSignals.get(memberOrCollection);
      if (existing !== undefined) return existing as Signal<readonly T[]>;
      const created = computed(() => memberOrCollection.from(this.stateSource()));
      this.typedCollectionSignals.set(memberOrCollection, created);
      return created;
    }
    return cached(this.collectionSignals, memberOrCollection, () => computed(
      () => this.stateSource().collections.get(memberOrCollection),
    ));
  }

  public command<TResult>(
    command: MvvmCommand<TResult>,
  ): Signal<Readonly<MvvmProjectedCommandState> | undefined>;
  public command<TArgument, TResult>(
    command: MvvmCommandWithArgument<TArgument, TResult>,
  ): Signal<Readonly<MvvmProjectedCommandState> | undefined>;
  public command(
    member: MemberIdentifier,
  ): Signal<Readonly<MvvmProjectedCommandState> | undefined>;
  public command(
    memberOrCommand:
      | MemberIdentifier
      | MvvmCommand<unknown>
      | MvvmCommandWithArgument<unknown, unknown>,
  ): Signal<Readonly<MvvmProjectedCommandState> | undefined> {
    this.assertActive();
    const member = toMember(memberOrCommand);
    return cached(this.commandSignals, member, () => computed(
      () => this.stateSource().commands.get(member),
    ));
  }

  public validation<T>(
    binding: MvvmReadonlyProperty<T> | MvvmProperty<T> | MvvmCollection<T>,
  ): Signal<readonly string[] | undefined>;
  public validation(member: MemberIdentifier): Signal<readonly string[] | undefined>;
  public validation<T>(
    memberOrBinding:
      | MemberIdentifier
      | MvvmReadonlyProperty<T>
      | MvvmProperty<T>
      | MvvmCollection<T>,
  ): Signal<readonly string[] | undefined> {
    this.assertActive();
    const member = toMember(memberOrBinding);
    return cached(this.validationSignals, member, () => computed(
      () => this.stateSource().validation.get(member),
    ));
  }

  public subscribe(listener: (event: MvvmProjectionEvent) => void): () => void {
    this.assertActive();
    this.listeners.add(listener);
    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      this.listeners.delete(listener);
    };
  }

  public setProperty(
    member: MemberIdentifier,
    value: JsonValue,
  ): ReturnType<MvvmProjection["setProperty"]> {
    this.assertActive();
    return this.projection.setProperty(member, value);
  }

  public execute<T extends JsonValue = JsonValue>(
    member: MemberIdentifier,
    options: Readonly<{ argument?: JsonValue }> = {},
  ): MvvmProjectedCommandInvocation<T> {
    this.assertActive();
    return this.projection.execute<T>(member, options);
  }

  public destroy(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.unsubscribeProjection();
    this.listeners.clear();
    this.propertySignals.clear();
    this.collectionSignals.clear();
    this.commandSignals.clear();
    this.validationSignals.clear();
    if (this.options.ownsProjection === true) this.projection.dispose();
  }

  private assertActive(): void {
    if (this.disposed) throw new Error("The Angular MVVM store has been destroyed.");
  }
}

/** Angular injection token for a caller-scoped MVVM store. */
export const ANGULAR_MVVM_STORE =
  new InjectionToken<AngularMvvmStore>("webuitoolkit.mvvm.angular.store");

/** Provides a caller-owned store without transferring its lifetime. */
export function provideAngularMvvmStore(store: AngularMvvmStore): Provider {
  return { provide: ANGULAR_MVVM_STORE, useValue: store };
}

/** Injects the nearest caller-provided store. */
export function injectAngularMvvmStore(): AngularMvvmStore {
  return inject(ANGULAR_MVVM_STORE);
}

/** Framework-independent lifetime kernel used by the Angular DataContext directive. */
export class AngularMvvmDirectiveLifetime {
  private current: AngularMvvmStore | undefined;

  public wutMvvmOwnsStore = false;

  public set store(value: AngularMvvmStore) {
    if (value === this.current) return;
    if (this.wutMvvmOwnsStore) this.current?.destroy();
    this.current = value;
  }

  public get dataContext(): AngularMvvmStore {
    if (this.current === undefined) {
      throw new Error("The wutMvvmStore directive requires an AngularMvvmStore input.");
    }
    return this.current;
  }

  public destroy(): void {
    if (this.wutMvvmOwnsStore) this.current?.destroy();
    this.current = undefined;
  }
}

function cached<T>(
  cache: Map<MemberIdentifier, Signal<T>>,
  member: MemberIdentifier,
  factory: () => Signal<T>,
): Signal<T> {
  const existing = cache.get(member);
  if (existing !== undefined) return existing;
  const created = factory();
  cache.set(member, created);
  return created;
}

function toMember(value: MemberIdentifier | { readonly member: MemberIdentifier }): MemberIdentifier {
  return typeof value === "number" ? value : value.member;
}
