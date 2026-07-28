import type {
  CancelResult,
  CommandResult,
} from "./client.js";
import type { JsonValue } from "./protocol.js";
import type { MvvmProjectedCommandInvocation } from "./projection.js";

export type MvvmCommandExecutionStatus =
  | "idle"
  | "running"
  | "succeeded"
  | "failed"
  | "canceled";

export interface MvvmCommandExecutionSnapshot<TResult extends JsonValue = JsonValue> {
  readonly status: MvvmCommandExecutionStatus;
  readonly transition: number;
  readonly request?: string;
  readonly result?: CommandResult<TResult>;
  readonly error?: unknown;
  readonly isRunning: boolean;
  readonly canCancel: boolean;
  readonly cancellationRequested: boolean;
}

export interface MvvmCommandExecution<
  TArgument = void,
  TResult extends JsonValue = JsonValue,
> {
  readonly snapshot: MvvmCommandExecutionSnapshot<TResult>;
  execute: [TArgument] extends [void]
    ? () => MvvmProjectedCommandInvocation<TResult>
    : (argument: TArgument) => MvvmProjectedCommandInvocation<TResult>;
  cancel(): Promise<CancelResult | undefined>;
  subscribe(listener: () => void): () => void;
  reset(): void;
  dispose(): void;
}

export interface MvvmParameterlessCommand<TResult extends JsonValue = JsonValue> {
  execute(): MvvmProjectedCommandInvocation<TResult>;
}

export interface MvvmParameterizedCommand<
  TArgument,
  TResult extends JsonValue = JsonValue,
> {
  execute(argument: TArgument): MvvmProjectedCommandInvocation<TResult>;
}

/**
 * Tracks one command handle without changing its protocol semantics.
 *
 * Starting a newer invocation makes it authoritative for this facade. A late
 * completion from an older invocation is still observed, but cannot overwrite
 * the newer result. Disposal detaches listeners and leaves protocol ownership
 * with the command/projection owner.
 */
export function createMvvmCommandExecution<TResult extends JsonValue>(
  command: MvvmParameterlessCommand<TResult>,
): MvvmCommandExecution<void, TResult>;
export function createMvvmCommandExecution<TArgument, TResult extends JsonValue>(
  command: MvvmParameterizedCommand<TArgument, TResult>,
): MvvmCommandExecution<TArgument, TResult>;
export function createMvvmCommandExecution<TArgument, TResult extends JsonValue>(
  command:
    | MvvmParameterlessCommand<TResult>
    | MvvmParameterizedCommand<TArgument, TResult>,
): MvvmCommandExecution<TArgument, TResult> {
  return new CommandExecution(command);
}

const idleSnapshot = Object.freeze({
  status: "idle",
  transition: 0,
  isRunning: false,
  canCancel: false,
  cancellationRequested: false,
}) satisfies MvvmCommandExecutionSnapshot<never>;

class CommandExecution<TArgument, TResult extends JsonValue>
  implements MvvmCommandExecution<TArgument, TResult>
{
  private readonly listeners = new Set<() => void>();
  private current: MvvmCommandExecutionSnapshot<TResult> = idleSnapshot;
  private active:
    | {
        readonly generation: number;
        readonly invocation: MvvmProjectedCommandInvocation<TResult>;
        cancellation?: Promise<CancelResult>;
      }
    | undefined;
  private disposed = false;
  private generation = 0;

  public constructor(
    private readonly command:
      | MvvmParameterlessCommand<TResult>
      | MvvmParameterizedCommand<TArgument, TResult>,
  ) {}

  public get snapshot(): MvvmCommandExecutionSnapshot<TResult> {
    return this.current;
  }

  public readonly execute = ((...arguments_: [] | [TArgument]) => {
    this.assertActive();
    const invocation = arguments_.length === 0
      ? (this.command as MvvmParameterlessCommand<TResult>).execute()
      : (this.command as MvvmParameterizedCommand<TArgument, TResult>)
          .execute(arguments_[0]);
    const generation = ++this.generation;
    this.active = { generation, invocation };
    this.publish({
      status: "running",
      transition: this.current.transition + 1,
      request: invocation.request,
      isRunning: true,
      canCancel: true,
      cancellationRequested: false,
    });
    void invocation.completion.then(
      (result) => this.complete(generation, "succeeded", { result }),
      (error: unknown) => this.complete(
        generation,
        this.current.cancellationRequested ? "canceled" : "failed",
        { error },
      ),
    );
    return invocation;
  }) as MvvmCommandExecution<TArgument, TResult>["execute"];

  public cancel(): Promise<CancelResult | undefined> {
    this.assertActive();
    const active = this.active;
    if (active === undefined) return Promise.resolve(undefined);
    if (active.cancellation !== undefined) return active.cancellation;
    this.publish({
      ...this.current,
      transition: this.current.transition + 1,
      canCancel: false,
      cancellationRequested: true,
    });
    active.cancellation = active.invocation.cancel();
    return active.cancellation;
  }

  public subscribe(listener: () => void): () => void {
    this.assertActive();
    this.listeners.add(listener);
    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      this.listeners.delete(listener);
    };
  }

  public reset(): void {
    this.assertActive();
    if (this.current.isRunning) {
      throw new Error("A running command execution cannot be reset.");
    }
    this.publish({
      ...idleSnapshot,
      transition: this.current.transition + 1,
    });
  }

  public dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.active = undefined;
    this.listeners.clear();
  }

  private complete(
    generation: number,
    status: "succeeded" | "failed" | "canceled",
    outcome:
      | { readonly result: CommandResult<TResult> }
      | { readonly error: unknown },
  ): void {
    if (this.disposed || this.active?.generation !== generation) return;
    const request = this.active.invocation.request;
    this.active = undefined;
    this.publish({
      status,
      transition: this.current.transition + 1,
      request,
      ...outcome,
      isRunning: false,
      canCancel: false,
      cancellationRequested: this.current.cancellationRequested,
    });
  }

  private publish(snapshot: MvvmCommandExecutionSnapshot<TResult>): void {
    this.current = Object.freeze(snapshot);
    for (const listener of [...this.listeners]) {
      try {
        listener();
      } catch {
        // Command observers cannot interfere with sibling views.
      }
    }
  }

  private assertActive(): void {
    if (this.disposed) {
      throw new Error("The MVVM command execution facade has been disposed.");
    }
  }
}
