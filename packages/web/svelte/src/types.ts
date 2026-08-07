export interface ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot> {
  initialize(): Promise<Snapshot>;
  dispatch(command: Command): Promise<Receipt>;
  cancel(operationId: string): Promise<void>;
  reconnect(): Promise<Snapshot>;
  uiReady(): Promise<void>;
  uiRendered(): Promise<void>;
  subscribe(
    onEvent: (event: HostEvent) => void,
    onError?: (error: unknown) => void,
  ): () => void;
  dispose(): Promise<void>;
}

export type ApplicationBridgeStatus =
  | "idle"
  | "connecting"
  | "connected"
  | "reconnecting"
  | "error"
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

