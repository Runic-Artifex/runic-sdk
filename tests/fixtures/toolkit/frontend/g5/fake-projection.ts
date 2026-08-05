import type {
  CancelResult,
  JsonValue,
  MemberIdentifier,
  MvvmProjectedCommandInvocation,
  MvvmProjection,
  MvvmProjectionEvent,
  MvvmProjectionSnapshot,
  MvvmProjectedCommandState,
} from "@runic-artifex/mvvm";

const request = "00000000-0000-4000-8000-000000000501";

export class G5Projection implements MvvmProjection {
  readonly #listeners = new Set<(event: MvvmProjectionEvent) => void>();
  #snapshot: MvvmProjectionSnapshot;
  #disposed = false;
  #submissions = 0;
  #commits = 0;

  public constructor() {
    this.#snapshot = snapshot(0n, 0, 0, "", []);
  }

  public get snapshot(): MvvmProjectionSnapshot {
    return this.#snapshot;
  }

  public get listenerCount(): number {
    return this.#listeners.size;
  }

  public get commits(): number {
    return this.#commits;
  }

  public property(member: MemberIdentifier): JsonValue | undefined {
    return this.#snapshot.properties.get(member);
  }

  public collection(member: MemberIdentifier): readonly JsonValue[] | undefined {
    return this.#snapshot.collections.get(member);
  }

  public command(member: MemberIdentifier): Readonly<MvvmProjectedCommandState> | undefined {
    return this.#snapshot.commands.get(member);
  }

  public validation(member: MemberIdentifier): readonly string[] | undefined {
    return this.#snapshot.validation.get(member);
  }

  public subscribe(listener: (event: MvvmProjectionEvent) => void): () => void {
    this.#assertActive();
    this.#listeners.add(listener);
    return () => {
      this.#listeners.delete(listener);
    };
  }

  public async setProperty(
    member: MemberIdentifier,
    value: JsonValue,
  ): Promise<{ readonly request: string; readonly revision: bigint }> {
    this.#assertActive();
    if (member !== 1 || typeof value !== "number") {
      throw new TypeError("The G5 fixture accepts numeric member 1 only.");
    }
    this.#commits++;
    this.#snapshot = snapshot(
      BigInt(this.#commits),
      value,
      this.#submissions,
      String(this.#snapshot.properties.get(3) ?? ""),
      this.#snapshot.validation.get(1) ?? [],
    );
    this.#emit();
    return { request, revision: this.#snapshot.revision! };
  }

  public execute<T extends JsonValue = JsonValue>(
    member: MemberIdentifier,
  ): MvvmProjectedCommandInvocation<T> {
    this.#assertActive();
    if (member !== 2) {
      throw new TypeError("The G5 fixture accepts command member 2 only.");
    }
    this.#submissions++;
    this.#commits++;
    this.#snapshot = snapshot(
      BigInt(this.#commits),
      Number(this.#snapshot.properties.get(1)),
      this.#submissions,
      String(this.#snapshot.properties.get(3) ?? ""),
      this.#snapshot.validation.get(1) ?? [],
    );
    this.#emit();
    const result = Object.freeze({
      request,
      revision: this.#snapshot.revision!,
      valuePresent: true,
      value: Object.freeze({ submissions: this.#submissions }) as T,
    });
    return Object.freeze({
      request,
      completion: Promise.resolve(result),
      cancel: async (): Promise<CancelResult> => Object.freeze({
        request,
        revision: this.#snapshot.revision!,
        targetRequest: request,
        accepted: false,
      }),
    });
  }

  public replaceSnapshot(amount: number, validation: readonly string[] = []): void {
    this.#assertActive();
    this.#commits++;
    this.#snapshot = snapshot(
      BigInt(this.#commits),
      amount,
      this.#submissions,
      String(this.#snapshot.properties.get(3) ?? ""),
      validation,
    );
    this.#emit();
  }

  public setHostileText(value: string): void {
    this.#assertActive();
    this.#commits++;
    this.#snapshot = snapshot(
      BigInt(this.#commits),
      Number(this.#snapshot.properties.get(1)),
      this.#submissions,
      value,
      this.#snapshot.validation.get(1) ?? [],
    );
    this.#emit();
  }

  public dispose(): void {
    if (this.#disposed) return;
    this.#disposed = true;
    this.#listeners.clear();
  }

  #emit(): void {
    const event = Object.freeze({ type: "state", snapshot: this.#snapshot }) satisfies MvvmProjectionEvent;
    for (const listener of [...this.#listeners]) {
      try {
        listener(event);
      } catch {
        // A framework subscriber cannot affect another adapter subscriber.
      }
    }
  }

  #assertActive(): void {
    if (this.#disposed) {
      throw new Error("The G5 projection has been disposed.");
    }
  }
}

function snapshot(
  revision: bigint,
  amount: number,
  submissions: number,
  hostileText: string,
  validation: readonly string[],
): MvvmProjectionSnapshot {
  return Object.freeze({
    phase: "connected",
    synchronized: true,
    revision,
    properties: new Map<MemberIdentifier, JsonValue>([
      [1, amount],
      [3, hostileText],
      [4, submissions],
    ]),
    collections: new Map<MemberIdentifier, readonly JsonValue[]>([
      [5, Object.freeze([amount, submissions])],
    ]),
    commands: new Map<MemberIdentifier, Readonly<MvvmProjectedCommandState>>([
      [2, Object.freeze({ canExecute: true, isExecuting: false })],
    ]),
    validation: new Map<MemberIdentifier, readonly string[]>([
      [1, Object.freeze([...validation])],
    ]),
  });
}
