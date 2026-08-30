import type {
  ApplicationBridgeController,
  ApplicationBridgeEffects,
  ApplicationBridgeService,
  BridgeError,
} from "@runic-artifex/application-bridge";

/** Public Application Bridge contracts, re-exported without a renderer-owned copy. */
export type {
  ApplicationBridgeController,
  ApplicationBridgeEffects,
  ApplicationBridgeService,
  BridgeError,
};

/** @deprecated Application Bridge controllers already expose the Effect surface. */
export type EffectApplicationBridgeController<Command, Receipt, HostEvent, Snapshot> =
  ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot>;

/** @deprecated Use ApplicationBridgeController directly. */
export type EffectRunner = Pick<
  ApplicationBridgeController<unknown, unknown, unknown, unknown>,
  "run" | "runExit" | "fork" | "await" | "interrupt"
>;

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

/**
 * Optional callbacks emitted by the Svelte projection. This deliberately stays
 * independent of any Vite implementation; use the `/vite` entry point to
 * connect it to Runic Toolkit DevTools.
 */
export interface ApplicationBridgeObserver {
  state(state: Readonly<{
    connection?: Readonly<{
      state?: "connecting" | "connected" | "disconnected" | "closed";
      transport?: string;
      sessionId?: string;
      revision?: number;
      sequence?: number;
    }>;
  }>): void;
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
