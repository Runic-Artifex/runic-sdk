import type {
  ApplicationBridgeController,
  ApplicationBridgeObserver,
  ApplicationBridgeStatus,
  SvelteApplicationBridgeOptions,
} from "./types.js";

export class SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot, Failure = unknown> {
  snapshot = $state.raw<Snapshot | undefined>(undefined);
  lastEvent = $state.raw<HostEvent | undefined>(undefined);
  error = $state.raw<Failure | undefined>(undefined);
  status = $state<ApplicationBridgeStatus>("idle");

  readonly #controller: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot, Failure>;
  readonly #reduce: ((snapshot: Snapshot | undefined, event: HostEvent) => Snapshot | undefined) | undefined;
  readonly #observer: ApplicationBridgeObserver | undefined;
  readonly #inspectSnapshot: SvelteApplicationBridgeOptions<HostEvent, Snapshot>["inspectSnapshot"];
  #unsubscribe: (() => void) | undefined;
  #start: Promise<Snapshot> | undefined;

  constructor(
    controller: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot, Failure>,
    options: SvelteApplicationBridgeOptions<HostEvent, Snapshot> = {},
  ) {
    this.#controller = controller;
    this.#reduce = options.reduce;
    this.#observer = options.observer;
    this.#inspectSnapshot = options.inspectSnapshot;
  }

  start(): Promise<Snapshot> {
    if (this.status === "disposed") return Promise.reject(new Error("The Svelte Application Bridge is disposed."));
    if (this.#start) return this.#start;
    this.status = "connecting";
    this.error = undefined;
    this.#reportConnection("connecting");
    this.#unsubscribe ??= this.#controller.subscribe(
      (event) => {
        this.lastEvent = event;
        this.snapshot = this.#reduce?.(this.snapshot, event) ?? this.snapshot;
        this.#observer?.trace({ kind: "event", label: tagOf(event) });
        this.#reportConnection("connected");
      },
      (failure) => this.#fail(failure),
    );
    this.#start = this.#controller.initialize()
      .then(async (snapshot) => {
        this.snapshot = snapshot;
        this.status = "connected";
        this.#reportSnapshot(snapshot, "connected");
        await this.#controller.uiReady();
        await this.#controller.uiRendered();
        this.#observer?.trace({ kind: "connection", label: "ui-rendered" });
        return snapshot;
      })
      .catch((failure: Failure) => {
        this.#start = undefined;
        this.#fail(failure);
        throw failure;
      });
    return this.#start;
  }

  async dispatch(command: Command): Promise<Receipt> {
    this.#observer?.trace({ kind: "command", label: tagOf(command) });
    try {
      const receipt = await this.#controller.dispatch(command);
      this.#observer?.trace({ kind: "receipt", label: tagOf(receipt) });
      return receipt;
    } catch (failure) {
      this.#failUnknown(failure);
      throw failure;
    }
  }

  async cancel(operationId: string): Promise<void> {
    this.#observer?.trace({ kind: "operation", label: "CancelOperation", detail: { operationId } });
    try {
      await this.#controller.cancel(operationId);
    } catch (failure) {
      this.#failUnknown(failure);
      throw failure;
    }
  }

  async reconnect(): Promise<Snapshot> {
    this.status = "reconnecting";
    this.error = undefined;
    this.#reportConnection("connecting");
    try {
      const snapshot = await this.#controller.reconnect();
      this.snapshot = snapshot;
      this.status = "connected";
      this.#reportSnapshot(snapshot, "connected");
      return snapshot;
    } catch (failure) {
      this.#failUnknown(failure);
      throw failure;
    }
  }

  uiReady(): Promise<void> {
    return this.#controller.uiReady();
  }

  uiRendered(): Promise<void> {
    return this.#controller.uiRendered();
  }

  clearError(): void {
    this.error = undefined;
    if (this.status === "error") this.status = this.snapshot === undefined ? "idle" : "connected";
  }

  async dispose(): Promise<void> {
    if (this.status === "disposed") return;
    this.#unsubscribe?.();
    this.#unsubscribe = undefined;
    await this.#controller.dispose();
    this.status = "disposed";
    this.#reportConnection("closed");
  }

  #fail(failure: Failure): void {
    this.error = failure;
    this.status = "error";
    this.#observer?.trace({ kind: "error", label: errorLabel(failure) });
    this.#reportConnection("disconnected");
  }

  #failUnknown(failure: unknown): void {
    // Promise does not encode its rejection type; the controller contract does.
    this.#fail(failure as Failure);
  }

  #reportSnapshot(snapshot: Snapshot, state: string): void {
    const inspected = this.#inspectSnapshot?.(snapshot) ?? {};
    this.#observer?.state({
      connection: {
        state,
        transport: "application-bridge",
        ...inspected,
      },
    });
  }

  #reportConnection(state: string): void {
    this.#observer?.state({ connection: { state, transport: "application-bridge" } });
    this.#observer?.trace({ kind: "connection", label: state });
  }
}

export function createSvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot, Failure = unknown>(
  controller: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot, Failure>,
  options: SvelteApplicationBridgeOptions<HostEvent, Snapshot> = {},
): SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot, Failure> {
  return new SvelteApplicationBridge(controller, options);
}

function tagOf(value: unknown): string {
  if (typeof value === "object" && value !== null && "_tag" in value) {
    const tag = (value as { _tag?: unknown })._tag;
    if (typeof tag === "string" && tag.length > 0) return tag.slice(0, 128);
  }
  return "Unknown";
}

function errorLabel(failure: unknown): string {
  if (typeof failure === "object" && failure !== null && "_tag" in failure) return tagOf(failure);
  return failure instanceof Error ? failure.name.slice(0, 128) : "ApplicationBridgeError";
}
