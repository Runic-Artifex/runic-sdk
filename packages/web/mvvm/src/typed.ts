import type {
  JsonValue,
  MemberIdentifier,
  Revision,
} from "./protocol.js";
import type {
  MvvmProjectedCommandInvocation,
  MvvmProjectedCommandState,
  MvvmProjection,
  MvvmProjectionSnapshot,
} from "./projection.js";

/** Strongly typed read-only property generated from one binding contract. */
export class MvvmReadonlyProperty<T> {
  public constructor(
    protected readonly projection: MvvmProjection,
    public readonly member: MemberIdentifier,
  ) {}

  public get value(): T | undefined {
    return this.projection.property(this.member) as T | undefined;
  }

  public from(snapshot: MvvmProjectionSnapshot): T | undefined {
    return snapshot.properties.get(this.member) as T | undefined;
  }

  public get validation(): readonly string[] {
    return this.projection.validation(this.member) ?? [];
  }
}

/** Strongly typed writable property generated from one binding contract. */
export class MvvmProperty<T> extends MvvmReadonlyProperty<T> {
  public set(value: T): Promise<{ readonly request: string; readonly revision: Revision }> {
    return this.projection.setProperty(this.member, value as JsonValue);
  }
}

/** Strongly typed collection generated from one binding contract. */
export class MvvmCollection<T> {
  public constructor(
    private readonly projection: MvvmProjection,
    public readonly member: MemberIdentifier,
  ) {}

  public get value(): readonly T[] {
    return (this.projection.collection(this.member) ?? []) as readonly T[];
  }

  public from(snapshot: MvvmProjectionSnapshot): readonly T[] {
    return (snapshot.collections.get(this.member) ?? []) as readonly T[];
  }

  public get validation(): readonly string[] {
    return this.projection.validation(this.member) ?? [];
  }
}

/** Strongly typed parameterless command generated from one binding contract. */
export class MvvmCommand<TResult = null> {
  public constructor(
    protected readonly projection: MvvmProjection,
    public readonly member: MemberIdentifier,
  ) {}

  public get state(): Readonly<MvvmProjectedCommandState> | undefined {
    return this.projection.command(this.member);
  }

  public execute(): MvvmProjectedCommandInvocation<TResult & JsonValue> {
    return this.projection.execute<TResult & JsonValue>(this.member);
  }
}

/** Strongly typed parameterized command generated from one binding contract. */
export class MvvmCommandWithArgument<TArgument, TResult = null>
{
  public constructor(
    private readonly projection: MvvmProjection,
    public readonly member: MemberIdentifier,
  ) {}

  public get state(): Readonly<MvvmProjectedCommandState> | undefined {
    return this.projection.command(this.member);
  }

  public execute(argument: TArgument): MvvmProjectedCommandInvocation<TResult & JsonValue> {
    return this.projection.execute<TResult & JsonValue>(this.member, {
      argument: argument as JsonValue,
    });
  }
}
