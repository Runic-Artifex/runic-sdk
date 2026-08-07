import type { Effect, Exit, Fiber, Stream } from "effect";

export interface ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot, Failure = unknown> {
  initialize(): Promise<Snapshot>;
  dispatch(command: Command): Promise<Receipt>;
  cancel(operationId: string): Promise<void>;
  reconnect(): Promise<Snapshot>;
  uiReady(): Promise<void>;
  uiRendered(): Promise<void>;
  subscribe(
    onEvent: (event: HostEvent) => void,
    onError?: (error: Failure) => void,
  ): () => void;
  dispose(): Promise<void>;
}

export interface ApplicationBridgeEffects<Command, Receipt, HostEvent, Snapshot, Failure, Requirements> {
  readonly initialize: Effect.Effect<Snapshot, Failure, Requirements>;
  readonly dispatch: (command: Command) => Effect.Effect<Receipt, Failure, Requirements>;
  readonly cancel: (operationId: string) => Effect.Effect<void, Failure, Requirements>;
  readonly reconnect: Effect.Effect<Snapshot, Failure, Requirements>;
  readonly uiReady: Effect.Effect<void, Failure, Requirements>;
  readonly uiRendered: Effect.Effect<void, Failure, Requirements>;
  readonly events: Stream.Stream<HostEvent, Failure, Requirements>;
}

export interface EffectRunner<Requirements> {
  run<A, E>(program: Effect.Effect<A, E, Requirements>): Promise<A>;
  runExit<A, E>(program: Effect.Effect<A, E, Requirements>): Promise<Exit.Exit<A, E>>;
  fork<A, E>(program: Effect.Effect<A, E, Requirements>): Fiber.RuntimeFiber<A, E>;
  await<A, E>(fiber: Fiber.RuntimeFiber<A, E>): Promise<Exit.Exit<A, E>>;
  interrupt<A, E>(fiber: Fiber.RuntimeFiber<A, E>): Promise<Exit.Exit<A, E>>;
}

export interface EffectApplicationBridgeController<
  Command,
  Receipt,
  HostEvent,
  Snapshot,
  Failure,
  Requirements,
> extends ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot, Failure>, EffectRunner<Requirements> {
  readonly effects: ApplicationBridgeEffects<Command, Receipt, HostEvent, Snapshot, Failure, Requirements>;
}

export type ApplicationBridgeStatus =
  | "idle"
  | "connecting"
  | "connected"
  | "reconnecting"
  | "error"
  | "disposed";

export type EffectActionStatus =
  | "idle"
  | "running"
  | "success"
  | "failure"
  | "interrupted"
  | "disposed";

export interface ApplicationBridgeObserver {
  state(state: Readonly<Record<string, unknown>>): void;
  trace(entry: Readonly<{
    kind: "command" | "receipt" | "event" | "operation" | "connection" | "error";
    label: string;
    detail?: Readonly<Record<string, unknown>>;
  }>): void;
}

export interface SvelteApplicationBridgeOptions<HostEvent, Snapshot> {
  readonly reduce?: (snapshot: Snapshot | undefined, event: HostEvent) => Snapshot | undefined;
  readonly observer?: ApplicationBridgeObserver;
  readonly inspectSnapshot?: (snapshot: Snapshot) => Readonly<{
    sessionId?: string;
    revision?: number;
    sequence?: number;
  }>;
}
