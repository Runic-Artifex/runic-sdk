import type { FrameChannelEvent, FrameChannelState, ReconnectableFrameChannel } from "./transport.js";

/** The public CS-WebUI browser API; injected for testing or supplied by /webui.js. */
export interface CsWebUiClient {
  isConnected(): boolean;
  call(binding: string, ...arguments_: Array<string | Uint8Array>): Promise<unknown>;
}

export interface CsWebUiFrameChannelOptions {
  readonly client?: CsWebUiClient;
  readonly credential?: string;
  readonly maxFrameBytes?: number;
  readonly connectionTimeoutMs?: number;
}

/** Connects generated Runic contracts through native CS-WebUI without the Desktop npm package. */
export function createCsWebUiFrameChannel(options: CsWebUiFrameChannelOptions = {}): ReconnectableFrameChannel {
  const scope = globalThis as typeof globalThis & {
    webui?: CsWebUiClient;
    runicCsWebUi?: { credential: string; maxFrameBytes: number };
  };
  // Native WebUI installs its browser object after parsing the entry document.
  const client = options.client ?? {
    isConnected: () => scope.webui?.isConnected() ?? false,
    call: (binding: string, ...arguments_: Array<string | Uint8Array>) => {
      if (scope.webui === undefined) return Promise.reject(new Error("CS-WebUI browser API is unavailable."));
      return scope.webui.call(binding, ...arguments_);
    },
  };
  const credential = options.credential ?? scope.runicCsWebUi?.credential;
  const max = options.maxFrameBytes ?? scope.runicCsWebUi?.maxFrameBytes ?? 262_144;
  const timeout = options.connectionTimeoutMs ?? 10_000;
  if (credential === undefined || !/^[a-fA-F0-9]{64}$/.test(credential))
    throw new Error("Load the CS-WebUI application host bootstrap before creating its frame channel.");
  if (!Number.isSafeInteger(max) || max < 1024 || max > 16_777_216 || !Number.isFinite(timeout) || timeout <= 0)
    throw new Error("Invalid CS-WebUI channel limits.");
  return new CsWebUiFrameChannel(client, credential, max, timeout);
}

class CsWebUiFrameChannel implements ReconnectableFrameChannel {
  private current: FrameChannelState = "disconnected";
  private readonly listeners = new Set<(event: FrameChannelEvent) => void>();
  private tail: Promise<unknown> = Promise.resolve();
  private generation = 0;
  private initialized = false;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private connecting: Promise<void> | undefined;

  constructor(private readonly client: CsWebUiClient, private readonly credential: string,
    private readonly max: number, private readonly timeout: number) {}
  get state(): FrameChannelState { return this.current; }

  reconnect(): Promise<void> {
    if (this.current === "closed") return Promise.reject(new Error("The CS-WebUI channel is closed."));
    if (this.connecting !== undefined) return this.connecting;
    clearTimeout(this.timer); this.timer = undefined;
    this.initialized = false;
    const generation = ++this.generation;
    const attempt = (async () => {
      const deadline = Date.now() + this.timeout;
      while (!this.client.isConnected()) {
        if (generation !== this.generation) throw new Error("CS-WebUI connection superseded.");
        if (Date.now() >= deadline) throw new Error("CS-WebUI connection timed out.");
        await new Promise(resolve => setTimeout(resolve, 20));
      }
      if (generation !== this.generation) throw new Error("CS-WebUI connection superseded.");
      this.change("connected");
    })();
    this.connecting = attempt;
    void attempt.finally(() => { if (this.connecting === attempt) this.connecting = undefined; }).catch(() => {});
    return attempt;
  }

  async send(bytes: Uint8Array): Promise<void> {
    if (bytes.byteLength === 0 || bytes.byteLength > this.max) throw new Error("CS-WebUI frame exceeds its limit.");
    if (this.current !== "connected") throw new Error("CS-WebUI is disconnected.");
    await this.call("__runicApplicationFrame", new Uint8Array(bytes));
    this.schedulePoll();
  }

  subscribe(listener: (event: FrameChannelEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  async close(_reason: string): Promise<void> {
    ++this.generation;
    clearTimeout(this.timer); this.timer = undefined;
    this.initialized = false;
    this.change("closed");
    this.listeners.clear();
  }

  private async call(binding: string, bytes?: Uint8Array): Promise<void> {
    const generation = this.generation;
    const operation = this.tail.then(async () => {
      if (generation !== this.generation || this.current !== "connected") return;
      if (!this.client.isConnected()) throw new Error("CS-WebUI disconnected.");
      let timeout: ReturnType<typeof setTimeout> | undefined;
      let result: unknown;
      try {
        result = await Promise.race([
          this.client.call(binding, this.credential, ...(bytes === undefined ? [] : [bytes])),
          new Promise<never>((_, reject) => { timeout = setTimeout(() => reject(new Error("CS-WebUI callback timed out.")), this.timeout); }),
        ]);
      } finally { clearTimeout(timeout); }
      if (generation !== this.generation) return;
      if (typeof result !== "string" || result.length > 2_097_152) throw new Error("Invalid CS-WebUI response.");
      const frames: unknown = JSON.parse(result);
      if (!Array.isArray(frames) || frames.length > 64) throw new Error("Invalid CS-WebUI response batch.");
      for (const frame of frames) {
        const encoded = new TextEncoder().encode(JSON.stringify(frame));
        if (encoded.length > this.max) throw new Error("CS-WebUI response exceeds frame limit.");
        if (typeof frame === "object" && frame !== null && frame.kind === "snapshot") this.initialized = true;
        this.publish({ _tag: "Frame", bytes: encoded });
      }
    });
    // Serialize polls and commands, including delivery, so native callbacks cannot reorder frames.
    this.tail = operation.catch(() => {});
    try { await operation; }
    catch (error) {
      if (generation === this.generation) { clearTimeout(this.timer); this.timer = undefined; this.initialized = false; this.change("disconnected"); }
      throw error;
    }
  }

  private schedulePoll(): void {
    if (!this.initialized || this.current !== "connected" || this.timer !== undefined) return;
    this.timer = setTimeout(() => {
      this.timer = undefined;
      void this.call("__runicApplicationPoll").then(() => this.schedulePoll()).catch(() => {});
    }, 10);
  }

  private change(state: FrameChannelState): void { this.current = state; this.publish({ _tag: "State", state }); }
  private publish(event: FrameChannelEvent): void { for (const listener of this.listeners) listener(event); }
}
