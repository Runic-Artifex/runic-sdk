import type {
  ApplicationBridgeController,
  ApplicationBridgeObserver,
  ApplicationBridgeStatus,
  BridgeError,
  SvelteApplicationBridgeOptions,
} from "./types.js";

// Keep the Application Bridge peer optional for translations-only applications,
// while making bridge entry points fail with an actionable error when selected.
await import("@runic-artifex/application-bridge").catch(() => {
  throw new Error(
    "@runic-artifex/svelte bridge support requires @runic-artifex/application-bridge. "
      + "Install a compatible preview with `npm install @runic-artifex/application-bridge@preview`.",
  );
});

export class SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot> {
  snapshot = $state.raw<Snapshot | undefined>(undefined);
  lastEvent = $state.raw<HostEvent | undefined>(undefined);
  error = $state.raw<BridgeError | undefined>(undefined);
  status = $state<ApplicationBridgeStatus>("idle");

  readonly #controller: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot>;
  readonly #reduce: ((snapshot: Snapshot | undefined, event: HostEvent) => Snapshot | undefined) | undefined;
  readonly #observer: ApplicationBridgeObserver | undefined;
  readonly #inspectSnapshot: SvelteApplicationBridgeOptions<HostEvent, Snapshot>["inspectSnapshot"];
  #unsubscribe: (() => void) | undefined;
  #start: Promise<Snapshot> | undefined;
  #dispose: Promise<void> | undefined;
  #disposed = false;

  constructor(
    controller: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot>,
    options: SvelteApplicationBridgeOptions<HostEvent, Snapshot> = {},
  ) {
    this.#controller = controller;
    this.#reduce = options.reduce;
    this.#observer = options.observer;
    this.#inspectSnapshot = options.inspectSnapshot;
  }

  start(): Promise<Snapshot> {
    if (this.#disposed) return Promise.reject(disposedError());
    if (this.#start) return this.#start;
    this.status = "connecting";
    this.error = undefined;
    this.#reportConnection("connecting");
    this.#unsubscribe ??= this.#controller.subscribe(
      (event) => {
        if (this.#disposed) return;
        this.lastEvent = event;
        this.snapshot = this.#reduce?.(this.snapshot, event) ?? this.snapshot;
        this.#observer?.trace({ kind: "event", label: tagOf(event) });
        this.#reportConnection("connected");
      },
      (failure) => {
        if (!this.#disposed) this.#fail(failure);
      },
    );
    this.#start = this.#startController();
    return this.#start;
  }

  async dispatch(command: Command): Promise<Receipt> {
    this.#assertActive();
    this.#observer?.trace({ kind: "command", label: tagOf(command) });
    try {
      const receipt = await this.#controller.dispatch(command);
      this.#assertActive();
      this.#observer?.trace({ kind: "receipt", label: tagOf(receipt) });
      return receipt;
    } catch (failure) {
      if (this.#disposed) throw disposedError();
      this.#failUnknown(failure);
      throw failure;
    }
  }

  async cancel(operationId: string): Promise<void> {
    this.#assertActive();
    this.#observer?.trace({ kind: "operation", label: "CancelOperation", detail: { operationId } });
    try {
      await this.#controller.cancel(operationId);
      this.#assertActive();
    } catch (failure) {
      if (this.#disposed) throw disposedError();
      this.#failUnknown(failure);
      throw failure;
    }
  }

  async reconnect(): Promise<Snapshot> {
    this.#assertActive();
    this.status = "reconnecting";
    this.error = undefined;
    this.#reportConnection("connecting");
    try {
      const snapshot = await this.#controller.reconnect();
      this.#assertActive();
      this.snapshot = snapshot;
      this.status = "connected";
      this.#reportSnapshot(snapshot, "connected");
      return snapshot;
    } catch (failure) {
      if (this.#disposed) throw disposedError();
      this.#failUnknown(failure);
      throw failure;
    }
  }

  async uiReady(): Promise<void> {
    this.#assertActive();
    try {
      await this.#controller.uiReady();
      this.#assertActive();
    } catch (failure) {
      if (this.#disposed) throw disposedError();
      this.#failUnknown(failure);
      throw failure;
    }
  }

  async uiRendered(): Promise<void> {
    this.#assertActive();
    try {
      await this.#controller.uiRendered();
      this.#assertActive();
    } catch (failure) {
      if (this.#disposed) throw disposedError();
      this.#failUnknown(failure);
      throw failure;
    }
  }

  clearError(): void {
    if (this.#disposed) return;
    this.error = undefined;
    if (this.status === "error") this.status = this.snapshot === undefined ? "idle" : "connected";
  }

  async dispose(): Promise<void> {
    if (this.#dispose) return this.#dispose;
    this.#disposed = true;
    this.status = "disposed";
    this.#unsubscribe?.();
    this.#unsubscribe = undefined;
    this.#dispose = Promise.resolve()
      .finally(() => this.#reportConnection("closed"));
    return this.#dispose;
  }

  #fail(failure: BridgeError): void {
    if (this.#disposed) return;
    this.error = failure;
    this.status = "error";
    this.#observer?.trace({ kind: "error", label: errorLabel(failure) });
    this.#reportConnection("disconnected");
  }

  #failUnknown(failure: unknown): void {
    // Promise does not encode its rejection type; the controller contract does.
    this.#fail(failure as BridgeError);
  }

  async #startController(): Promise<Snapshot> {
    try {
      const snapshot = await this.#controller.initialize();
      this.#assertActive();
      this.snapshot = snapshot;
      this.status = "connected";
      this.#reportSnapshot(snapshot, "connected");
      await this.#controller.uiReady();
      this.#assertActive();
      await this.#controller.uiRendered();
      this.#assertActive();
      this.#observer?.trace({ kind: "connection", label: "ui-rendered" });
      return snapshot;
    } catch (failure) {
      if (this.#disposed) throw disposedError();
      this.#start = undefined;
      this.#failUnknown(failure);
      throw failure;
    }
  }

  #assertActive(): void {
    if (this.#disposed) throw disposedError();
  }

  #reportSnapshot(snapshot: Snapshot, state: "connected"): void {
    const inspected = this.#inspectSnapshot?.(snapshot) ?? {};
    this.#observer?.state({
      connection: {
        state,
        transport: "application-bridge",
        ...inspected,
      },
    });
  }

  #reportConnection(state: "connecting" | "connected" | "closed" | "disconnected"): void {
    this.#observer?.state({ connection: { state, transport: "application-bridge" } });
    this.#observer?.trace({ kind: "connection", label: state });
  }
}

export function createSvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot>(
  controller: ApplicationBridgeController<Command, Receipt, HostEvent, Snapshot>,
  options: SvelteApplicationBridgeOptions<HostEvent, Snapshot> = {},
): SvelteApplicationBridge<Command, Receipt, HostEvent, Snapshot> {
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

function disposedError(): Error {
  return new Error("The Svelte Application Bridge is disposed.");
}
