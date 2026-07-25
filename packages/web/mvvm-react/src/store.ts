import type {
  JsonValue,
  MemberIdentifier,
  MvvmProjectedCommandInvocation,
  MvvmProjectedCommandState,
  MvvmProjection,
  MvvmProjectionSnapshot,
  Revision,
} from "@webuitoolkit/mvvm";

export interface ReactMvvmStoreOptions {
  /**
   * Disposes the supplied projection when the store is disposed.
   * The default is false because callers normally own protocol lifetimes.
   */
  readonly ownsProjection?: boolean;
}

/** A React-compatible external store over a framework-neutral MVVM projection. */
export interface ReactMvvmStore {
  /** Returns the same immutable object until the projection accepts new state. */
  getSnapshot(): MvvmProjectionSnapshot;
  /** Server rendering observes the same accepted protocol snapshot. */
  getServerSnapshot(): MvvmProjectionSnapshot;
  subscribe(listener: () => void): () => void;
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
  dispose(): void;
}

/** Creates a useSyncExternalStore-compatible adapter without changing G4 semantics. */
export function createReactMvvmStore(
  projection: MvvmProjection,
  options: Readonly<ReactMvvmStoreOptions> = {},
): ReactMvvmStore {
  return new ProjectionExternalStore(projection, options.ownsProjection === true);
}

class ProjectionExternalStore implements ReactMvvmStore {
  private readonly listeners = new Set<() => void>();
  private readonly unsubscribeProjection: () => void;
  private current: MvvmProjectionSnapshot;
  private disposed = false;

  public constructor(
    private readonly projection: MvvmProjection,
    private readonly ownsProjection: boolean,
  ) {
    this.current = projection.snapshot;
    this.unsubscribeProjection = projection.subscribe((event) => {
      if (this.disposed || event.type !== "state") return;
      this.current = event.snapshot;
      for (const listener of [...this.listeners]) {
        try {
          listener();
        } catch {
          // A failed React subscriber must not prevent sibling roots from updating.
        }
      }
    });
  }

  public getSnapshot = (): MvvmProjectionSnapshot => this.current;

  public getServerSnapshot = (): MvvmProjectionSnapshot => this.current;

  public subscribe = (listener: () => void): (() => void) => {
    this.assertActive();
    this.listeners.add(listener);
    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      this.listeners.delete(listener);
    };
  };

  public property(member: MemberIdentifier): JsonValue | undefined {
    return this.current.properties.get(member);
  }

  public collection(member: MemberIdentifier): readonly JsonValue[] | undefined {
    return this.current.collections.get(member);
  }

  public command(member: MemberIdentifier): Readonly<MvvmProjectedCommandState> | undefined {
    return this.current.commands.get(member);
  }

  public validation(member: MemberIdentifier): readonly string[] | undefined {
    return this.current.validation.get(member);
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
    this.unsubscribeProjection();
    this.listeners.clear();
    if (this.ownsProjection) this.projection.dispose();
  }

  private assertActive(): void {
    if (this.disposed) throw new Error("The React MVVM store has been disposed.");
  }
}
