import { Context, Effect, Stream } from "effect";
import type { BridgeError } from "./errors.js";

export interface ApplicationBridgeService<Command = unknown, Receipt = unknown, HostEvent = unknown, Snapshot = unknown, Failure = BridgeError> {
  readonly initialize: Effect.Effect<Snapshot, Failure>;
  readonly dispatch: (command: Command) => Effect.Effect<Receipt, Failure>;
  readonly cancel: (operationId: string) => Effect.Effect<void, Failure>;
  readonly reconnect: Effect.Effect<Snapshot, Failure>;
  readonly uiReady: Effect.Effect<void, Failure>;
  readonly uiRendered: Effect.Effect<void, Failure>;
  readonly events: Stream.Stream<HostEvent, Failure>;
}

export const ApplicationBridge = Context.GenericTag<ApplicationBridgeService>(
  "@runic-artifex/application-bridge/ApplicationBridge",
);
