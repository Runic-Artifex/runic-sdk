export type FrameChannelState = "connected" | "disconnected" | "closed";

export type FrameChannelEvent =
  /** The channel owns a stable frame buffer and will not mutate it after publication. */
  | { readonly _tag: "Frame"; readonly bytes: Uint8Array }
  | { readonly _tag: "State"; readonly state: FrameChannelState };

export interface FrameChannel {
  readonly state: FrameChannelState;
  send(bytes: Uint8Array): Promise<void>;
  subscribe(listener: (event: FrameChannelEvent) => void): () => void;
  close(reason: string): Promise<void>;
}

/** A frame channel that can replace its physical connection while retaining its consumer. */
export interface ReconnectableFrameChannel extends FrameChannel {
  /** Establishes a fresh physical connection and resolves once it can send frames. */
  reconnect(): Promise<void>;
}

/** Structural WebSocket surface shared by browser and standards-compliant Node runtimes. */
export interface WebSocketFrameSocket {
  readonly readyState: number;
  binaryType: BinaryType;
  send(data: ArrayBufferView): void;
  close(code?: number, reason?: string): void;
  addEventListener(type: "open" | "error" | "close", listener: EventListener): void;
  addEventListener(type: "message", listener: (event: MessageEvent<unknown>) => void): void;
  removeEventListener(type: "open" | "error" | "close", listener: EventListener): void;
  removeEventListener(type: "message", listener: (event: MessageEvent<unknown>) => void): void;
}

/** Creates one physical WebSocket connection on demand. */
export type WebSocketFrameSocketFactory = () => WebSocketFrameSocket;

/** Creates a reconnectable binary frame channel over injected standard WebSockets. */
export function createWebSocketFrameChannel(factory: WebSocketFrameSocketFactory): ReconnectableFrameChannel {
  return new WebSocketFrameChannel(factory);
}

/** A paired, transport-neutral channel for conformance fixtures and adapter tests. */
export interface InMemoryFrameChannelPair {
  readonly client: FrameChannel;
  readonly host: FrameChannel;
}

/**
 * Creates a deterministic duplex frame transport. Frames are copied before
 * delivery, so a test adapter has the same ownership boundary as a native one.
 */
export function createInMemoryFrameChannelPair(): InMemoryFrameChannelPair {
  const client = new InMemoryFrameChannel();
  const host = new InMemoryFrameChannel();
  client.attach(host);
  host.attach(client);
  return { client, host };
}

class InMemoryFrameChannel implements FrameChannel {
  private readonly listeners = new Set<(event: FrameChannelEvent) => void>();
  private peer: InMemoryFrameChannel | undefined;
  private currentState: FrameChannelState = "connected";

  public get state(): FrameChannelState { return this.currentState; }

  public attach(peer: InMemoryFrameChannel): void { this.peer = peer; }

  public async send(bytes: Uint8Array): Promise<void> {
    if (this.currentState !== "connected" || this.peer?.currentState !== "connected") {
      throw new Error("The Application Bridge channel is not connected.");
    }
    const owned = new Uint8Array(bytes);
    queueMicrotask(() => this.peer?.publish({ _tag: "Frame", bytes: owned }));
  }

  public subscribe(listener: (event: FrameChannelEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  public async close(_reason: string): Promise<void> {
    if (this.currentState === "closed") return;
    this.currentState = "closed";
    this.publish({ _tag: "State", state: "closed" });
    this.peer?.remoteClosed();
    this.listeners.clear();
  }

  private remoteClosed(): void {
    if (this.currentState === "closed") return;
    this.currentState = "disconnected";
    this.publish({ _tag: "State", state: "disconnected" });
  }

  private publish(event: FrameChannelEvent): void {
    for (const listener of this.listeners) listener(event);
  }
}

class WebSocketFrameChannel implements ReconnectableFrameChannel {
  private readonly listeners = new Set<(event: FrameChannelEvent) => void>();
  private currentState: FrameChannelState = "disconnected";
  private socket: WebSocketFrameSocket | undefined;
  private detach: (() => void) | undefined;
  private reconnecting: Promise<void> | undefined;
  private rejectReconnect: ((reason: unknown) => void) | undefined;
  private generation = 0;

  public constructor(private readonly factory: WebSocketFrameSocketFactory) {}

  public get state(): FrameChannelState { return this.currentState; }

  public async send(bytes: Uint8Array): Promise<void> {
    if (this.currentState !== "connected" || this.socket === undefined) {
      throw new Error("The Application Bridge channel is not connected.");
    }
    try {
      this.socket.send(new Uint8Array(bytes));
    } catch (error) {
      this.disconnect();
      throw error;
    }
  }

  public subscribe(listener: (event: FrameChannelEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  public reconnect(): Promise<void> {
    if (this.currentState === "closed") return Promise.reject(new Error("The Application Bridge channel is closed."));
    if (this.reconnecting !== undefined) return this.reconnecting;
    this.detach?.();
    this.detach = undefined;
    this.socket?.close(1000, "Application Bridge reconnect");
    this.socket = undefined;
    this.currentState = "disconnected";
    const generation = ++this.generation;
    let resolveReconnect!: () => void;
    let rejectReconnect!: (reason: unknown) => void;
    const attempt = new Promise<void>((resolve, reject) => { resolveReconnect = resolve; rejectReconnect = reject; });
    this.reconnecting = attempt;
    this.rejectReconnect = rejectReconnect;
    const settled = (error?: unknown): void => {
      if (generation !== this.generation || this.reconnecting !== attempt) return;
      this.reconnecting = undefined;
      this.rejectReconnect = undefined;
      if (error === undefined) resolveReconnect();
      else rejectReconnect(error);
    };
    let socket: WebSocketFrameSocket;
    try {
      socket = this.factory();
      socket.binaryType = "arraybuffer";
    } catch (error) {
      settled(error);
      return attempt;
    }
    const opened: EventListener = () => {
      if (generation !== this.generation || this.currentState === "closed") return;
      this.currentState = "connected";
      this.publish({ _tag: "State", state: "connected" });
      settled();
    };
    const failed: EventListener = () => {
      if (generation !== this.generation) return;
      this.disconnect();
      settled(new Error("The Application Bridge WebSocket connection failed."));
    };
    const closed: EventListener = () => {
      if (generation !== this.generation) return;
      this.disconnect();
      settled(new Error("The Application Bridge WebSocket connection closed."));
    };
    const message = (event: MessageEvent<unknown>): void => {
      if (generation !== this.generation || this.currentState !== "connected") return;
      const bytes = ownedBinaryFrame(event.data);
      if (bytes === undefined) {
        socket.close(1003, "Application Bridge requires binary frames");
        this.disconnect();
        return;
      }
      this.publish({ _tag: "Frame", bytes });
    };
    socket.addEventListener("open", opened);
    socket.addEventListener("error", failed);
    socket.addEventListener("close", closed);
    socket.addEventListener("message", message);
    this.socket = socket;
    this.detach = () => {
      socket.removeEventListener("open", opened);
      socket.removeEventListener("error", failed);
      socket.removeEventListener("close", closed);
      socket.removeEventListener("message", message);
    };
    if (socket.readyState === 1) queueMicrotask(() => opened(new Event("open")));
    else if (socket.readyState !== 0) queueMicrotask(() => failed(new Event("error")));
    return attempt;
  }

  public async close(reason: string): Promise<void> {
    if (this.currentState === "closed") return;
    this.currentState = "closed";
    this.generation++;
    this.rejectReconnect?.(new Error("The Application Bridge channel is closed."));
    this.rejectReconnect = undefined;
    this.reconnecting = undefined;
    this.detach?.();
    this.detach = undefined;
    this.socket?.close(1000, boundedCloseReason(reason));
    this.socket = undefined;
    this.publish({ _tag: "State", state: "closed" });
    this.listeners.clear();
  }

  private disconnect(): void {
    if (this.currentState === "closed" || this.currentState === "disconnected") return;
    this.currentState = "disconnected";
    this.publish({ _tag: "State", state: "disconnected" });
  }

  private publish(event: FrameChannelEvent): void {
    for (const listener of this.listeners) listener(event);
  }
}

function ownedBinaryFrame(value: unknown): Uint8Array | undefined {
  if (value instanceof ArrayBuffer) return new Uint8Array(value.slice(0));
  if (ArrayBuffer.isView(value)) {
    return new Uint8Array(value.buffer.slice(value.byteOffset, value.byteOffset + value.byteLength));
  }
  return undefined;
}

function boundedCloseReason(reason: string): string {
  return new TextDecoder().decode(new TextEncoder().encode(reason).slice(0, 123));
}
