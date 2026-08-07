import { Context, Effect, Stream } from "effect";
import type { BridgeError } from "./errors.js";

export interface ApplicationBridgeService<Command = unknown, Receipt = unknown, HostEvent = unknown, Snapshot = unknown> {
  readonly initialize: Effect.Effect<Snapshot, BridgeError>;
  readonly dispatch: (command: Command) => Effect.Effect<Receipt, BridgeError>;
  readonly cancel: (operationId: string) => Effect.Effect<void, BridgeError>;
  readonly reconnect: Effect.Effect<Snapshot, BridgeError>;
  readonly uiReady: Effect.Effect<void, BridgeError>;
  readonly uiRendered: Effect.Effect<void, BridgeError>;
  readonly events: Stream.Stream<HostEvent, BridgeError>;
}

export const ApplicationBridge = Context.GenericTag<ApplicationBridgeService>(
  "@runic-artifex/application-bridge/ApplicationBridge",
);
