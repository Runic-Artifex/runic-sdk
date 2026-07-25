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
  MvvmProjectedCommandInvocation,
  MvvmProjectedCommandState,
  MvvmProjection,
  MvvmProjectionEvent,
  MvvmProjectionSnapshot,
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

  public property(member: MemberIdentifier): Signal<JsonValue | undefined> {
    this.assertActive();
    return cached(this.propertySignals, member, () => computed(
      () => this.stateSource().properties.get(member),
    ));
  }

  public collection(member: MemberIdentifier): Signal<readonly JsonValue[] | undefined> {
    this.assertActive();
    return cached(this.collectionSignals, member, () => computed(
      () => this.stateSource().collections.get(member),
    ));
  }

  public command(
    member: MemberIdentifier,
  ): Signal<Readonly<MvvmProjectedCommandState> | undefined> {
    this.assertActive();
    return cached(this.commandSignals, member, () => computed(
      () => this.stateSource().commands.get(member),
    ));
  }

  public validation(member: MemberIdentifier): Signal<readonly string[] | undefined> {
    this.assertActive();
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
