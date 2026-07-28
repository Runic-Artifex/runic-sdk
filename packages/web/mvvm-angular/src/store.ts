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
  MvvmCommandExecution,
  MvvmCommandExecutionSnapshot,
  MvvmCommandWithArgument,
  MvvmProperty,
  MvvmProjectedCommandInvocation,
  MvvmProjectedCommandState,
  MvvmProjection,
  MvvmProjectionEvent,
  MvvmProjectionSnapshot,
  MvvmReadonlyProperty,
  CancelResult,
} from "@webuitoolkit/mvvm";
import { createMvvmCommandExecution } from "@webuitoolkit/mvvm";

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
  private readonly commandFacades = new Set<AngularMvvmCommandFacade<unknown, JsonValue>>();
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

  public commandFacade<TResult>(
    command: MvvmCommand<TResult>,
  ): AngularMvvmCommandFacade<void, TResult & JsonValue>;
  public commandFacade<TArgument, TResult>(
    command: MvvmCommandWithArgument<TArgument, TResult>,
  ): AngularMvvmCommandFacade<TArgument, TResult & JsonValue>;
  public commandFacade<TArgument, TResult>(
    command:
      | MvvmCommand<TResult>
      | MvvmCommandWithArgument<TArgument, TResult>,
  ): AngularMvvmCommandFacade<TArgument, TResult & JsonValue> {
    this.assertActive();
    let facade: AngularMvvmCommandFacade<TArgument, TResult & JsonValue>;
    facade = new AngularMvvmCommandFacade(
      this.command(command.member),
      command as MvvmCommandWithArgument<TArgument, TResult & JsonValue>,
      (): void => {
        this.commandFacades.delete(
          facade as AngularMvvmCommandFacade<unknown, JsonValue>,
        );
      },
    );
    this.commandFacades.add(
      facade as AngularMvvmCommandFacade<unknown, JsonValue>,
    );
    return facade;
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
    for (const facade of this.commandFacades) facade.destroy();
    this.commandFacades.clear();
    if (this.options.ownsProjection === true) this.projection.dispose();
  }

  private assertActive(): void {
    if (this.disposed) throw new Error("The Angular MVVM store has been destroyed.");
  }
}

/** Signal-native command result/error/running/cancellation facade. */
export class AngularMvvmCommandFacade<
  TArgument = void,
  TResult extends JsonValue = JsonValue,
> {
  private readonly execution: MvvmCommandExecution<TArgument, TResult>;
  private readonly lifecycleSource;
  private readonly unsubscribe: () => void;
  private destroyed = false;

  public readonly lifecycle: Signal<MvvmCommandExecutionSnapshot<TResult>>;
  public readonly status;
  public readonly result;
  public readonly error;
  public readonly isRunning;
  public readonly canExecute;
  public readonly canCancel;
  public readonly execute: MvvmCommandExecution<TArgument, TResult>["execute"];

  public constructor(
    commandState: Signal<Readonly<MvvmProjectedCommandState> | undefined>,
    command: MvvmCommandWithArgument<TArgument, TResult>,
    private readonly onDestroy: () => void = () => undefined,
  ) {
    this.execution = createMvvmCommandExecution(command);
    this.lifecycleSource = signal(this.execution.snapshot, { equal: Object.is });
    this.lifecycle = this.lifecycleSource.asReadonly();
    this.status = computed(() => this.lifecycleSource().status);
    this.result = computed(() => this.lifecycleSource().result);
    this.error = computed(() => this.lifecycleSource().error);
    this.isRunning = computed(
      () => this.lifecycleSource().isRunning || commandState()?.isExecuting === true,
    );
    this.canExecute = computed(() => commandState()?.canExecute === true);
    this.canCancel = computed(() => this.lifecycleSource().canCancel);
    this.execute = this.execution.execute;
    this.unsubscribe = this.execution.subscribe(
      () => this.lifecycleSource.set(this.execution.snapshot),
    );
  }

  public cancel(): Promise<CancelResult | undefined> {
    this.assertActive();
    return this.execution.cancel();
  }

  public reset(): void {
    this.assertActive();
    this.execution.reset();
  }

  public destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    this.unsubscribe();
    this.execution.dispose();
    this.onDestroy();
  }

  private assertActive(): void {
    if (this.destroyed) {
      throw new Error("The Angular MVVM command facade has been destroyed.");
    }
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
