import type {
  JsonValue,
  MemberIdentifier,
  MvvmProjectedCommandInvocation,
  MvvmProjectedCommandState,
  MvvmProjection,
  MvvmProjectionSnapshot,
  Revision,
} from "@webuitoolkit/mvvm";
import { onDestroy } from "svelte";
import type { Readable, Subscriber, Unsubscriber } from "svelte/store";

/** Controls whether disposing the store also disposes its input projection. */
export interface SvelteMvvmStoreOptions {
  /**
   * When true, {@link SvelteMvvmStore.dispose} disposes the projection.
   * The default is false because projections may be shared between adapters.
   */
  readonly ownsProjection?: boolean;
}

/** A Svelte-readable view over the frozen framework-neutral MVVM projection. */
export interface SvelteMvvmStore extends Readable<MvvmProjectionSnapshot> {
  /** The most recent immutable projection snapshot. */
  readonly snapshot: MvvmProjectionSnapshot;
  property(member: MemberIdentifier): JsonValue | undefined;
  collection(member: MemberIdentifier): readonly JsonValue[] | undefined;
  command(member: MemberIdentifier): Readonly<MvvmProjectedCommandState> | undefined;
  validation(member: MemberIdentifier): readonly string[] | undefined;
  setProperty(
    member: MemberIdentifier,
    value: JsonValue,
  ): Promise<{ readonly request: string; readonly revision: Revision }>;
  execute<T extends JsonValue = JsonValue>(
    member: MemberIdentifier,
    options?: Readonly<{ argument?: JsonValue }>,
  ): MvvmProjectedCommandInvocation<T>;
  /** Detaches every subscriber and, when explicitly owned, disposes the projection. */
  dispose(): void;
}

/** The lifecycle registration shape accepted by Svelte's `onDestroy`. */
export type SvelteDestroyRegistrar = (cleanup: () => void) => void;

/**
 * Creates a lazy readable store. Exactly one projection subscription exists
 * while at least one Svelte subscriber is active, and none exists otherwise.
 */
export function createSvelteMvvmStore(
  projection: MvvmProjection,
  options: Readonly<SvelteMvvmStoreOptions> = {},
): SvelteMvvmStore {
  return new ProjectionReadableStore(projection, options.ownsProjection === true);
}

/**
 * Registers store disposal with a component lifecycle.
 *
 * The optional registrar makes lifecycle ownership explicit in tests and
 * integration wrappers; normal Svelte components use the imported `onDestroy`.
 */
export function disposeSvelteMvvmStoreOnDestroy(
  store: Pick<SvelteMvvmStore, "dispose">,
  register: SvelteDestroyRegistrar = onDestroy,
): void {
  register(() => store.dispose());
}

interface StoreSubscriber {
  readonly run: Subscriber<MvvmProjectionSnapshot>;
  readonly invalidate: () => void;
}

class ProjectionReadableStore implements SvelteMvvmStore {
  private readonly subscribers = new Set<StoreSubscriber>();
  private unsubscribeProjection: Unsubscriber | undefined;
  private current: MvvmProjectionSnapshot;
  private disposed = false;

  public constructor(
    private readonly projection: MvvmProjection,
    private readonly ownsProjection: boolean,
  ) {
    this.current = projection.snapshot;
  }

  public get snapshot(): MvvmProjectionSnapshot {
    this.assertActive();
    return this.current;
  }

  public readonly subscribe = (
    run: Subscriber<MvvmProjectionSnapshot>,
    invalidate: () => void = () => undefined,
  ): Unsubscriber => {
    this.assertActive();
    const subscriber: StoreSubscriber = { run, invalidate };
    this.subscribers.add(subscriber);

    try {
      if (this.subscribers.size === 1) {
        this.current = this.projection.snapshot;
        this.unsubscribeProjection = this.projection.subscribe((event) => {
          if (event.type === "state") this.publish(event.snapshot);
        });
      }
      run(this.current);
    } catch (error) {
      this.removeSubscriber(subscriber);
      throw error;
    }

    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      this.removeSubscriber(subscriber);
    };
  };

  public property(member: MemberIdentifier): JsonValue | undefined {
    this.assertActive();
    return this.projection.property(member);
  }

  public collection(member: MemberIdentifier): readonly JsonValue[] | undefined {
    this.assertActive();
    return this.projection.collection(member);
  }

  public command(member: MemberIdentifier): Readonly<MvvmProjectedCommandState> | undefined {
    this.assertActive();
    return this.projection.command(member);
  }

  public validation(member: MemberIdentifier): readonly string[] | undefined {
    this.assertActive();
    return this.projection.validation(member);
  }

  public setProperty(
    member: MemberIdentifier,
    value: JsonValue,
  ): Promise<{ readonly request: string; readonly revision: Revision }> {
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

  public dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.unsubscribeProjection?.();
    this.unsubscribeProjection = undefined;
    this.subscribers.clear();
    if (this.ownsProjection) this.projection.dispose();
  }

  private publish(snapshot: MvvmProjectionSnapshot): void {
    if (this.disposed) return;
    this.current = snapshot;
    for (const subscriber of [...this.subscribers]) {
      try {
        subscriber.invalidate();
        subscriber.run(snapshot);
      } catch {
        // One component cannot suppress delivery to another component.
      }
    }
  }

  private removeSubscriber(subscriber: StoreSubscriber): void {
    this.subscribers.delete(subscriber);
    if (this.subscribers.size !== 0) return;
    this.unsubscribeProjection?.();
    this.unsubscribeProjection = undefined;
  }

  private assertActive(): void {
    if (this.disposed) throw new Error("The Svelte MVVM store has been disposed.");
  }
}
