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

export interface CsWebUiGlobal {
  readonly __runicToolkit_applicationBridge_send?: (frame: Uint8Array) => Promise<unknown>;
  __runicToolkit_applicationBridge_receiveHostEvent?: (frame: Uint8Array) => void;
}

export interface CsWebUiFrameChannelOptions {
  /** Maximum time to wait for CsWebUi to install its asynchronous native binding. */
  readonly bindingTimeoutMs?: number;
  /** How frequently to check for the binding while the page is booting. */
  readonly bindingPollIntervalMs?: number;
  /** One-time grace period after the binding appears while CsWebUi finishes its response channel. */
  readonly bindingSettleDelayMs?: number;
}

const defaultBindingTimeoutMs = 10_000;
const defaultBindingPollIntervalMs = 10;
const defaultBindingSettleDelayMs = 25;
const encoder = new TextEncoder();

export function createCsWebUiFrameChannel(
  target: CsWebUiGlobal = globalThis as CsWebUiGlobal,
  options: CsWebUiFrameChannelOptions = {},
): FrameChannel {
  const bindingTimeoutMs = positiveNumber(options.bindingTimeoutMs, defaultBindingTimeoutMs, "bindingTimeoutMs");
  const bindingPollIntervalMs = positiveNumber(
    options.bindingPollIntervalMs,
    defaultBindingPollIntervalMs,
    "bindingPollIntervalMs",
  );
  const bindingSettleDelayMs = nonNegativeNumber(
    options.bindingSettleDelayMs,
    defaultBindingSettleDelayMs,
    "bindingSettleDelayMs",
  );
  const listeners = new Set<(event: FrameChannelEvent) => void>();
  let state: FrameChannelState = "connected";
  let senderPromise: Promise<(frame: Uint8Array) => Promise<unknown>> | undefined;

  const publishOwnedFrame = (bytes: Uint8Array): void => {
    for (const listener of listeners) listener({ _tag: "Frame", bytes });
  };
  const receiveHostEvent = (bytes: Uint8Array): void => {
    publishOwnedFrame(new Uint8Array(bytes));
  };
  const receiveBindingResponse = (response: string | Uint8Array): void => {
    // Forward the native result as one owned frame. The Effect runtime recognizes
    // correlated batches and validates every envelope without a parse/stringify pass here.
    if (typeof response === "string") publishOwnedFrame(encoder.encode(response));
    else receiveHostEvent(response);
  };
  const installReceiver = (): void => {
    if (state === "connected") {
      target.__runicToolkit_applicationBridge_receiveHostEvent = receiveHostEvent;
    }
  };

  const resolveSender = (): Promise<(frame: Uint8Array) => Promise<unknown>> => {
    senderPromise ??= waitForSender(
      target,
      () => state,
      bindingTimeoutMs,
      bindingPollIntervalMs,
    ).then(async () => {
      if (bindingSettleDelayMs > 0) await delay(bindingSettleDelayMs);
      installReceiver();
      return waitForSender(
        target,
        () => state,
        bindingTimeoutMs,
        bindingPollIntervalMs,
      );
    }).catch((error: unknown) => {
      senderPromise = undefined;
      throw error;
    });
    return senderPromise;
  };

  installReceiver();
  return {
    get state() { return state; },
    async send(bytes) {
      if (state !== "connected") throw new Error("The Application Bridge channel is not connected.");
      const sender = await resolveSender();
      if (state !== "connected") throw new Error("The Application Bridge channel is not connected.");
      const result = sender(new Uint8Array(bytes));
      installReceiver();
      const response = await result;
      installReceiver();
      if (typeof response === "string" && response.length > 0) {
        receiveBindingResponse(response);
      } else if (response instanceof Uint8Array && response.byteLength > 0) {
        receiveBindingResponse(response);
      }
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    async close() {
      if (state === "closed") return;
      state = "closed";
      delete target.__runicToolkit_applicationBridge_receiveHostEvent;
      for (const listener of listeners) listener({ _tag: "State", state });
      listeners.clear();
    },
  };
}

async function waitForSender(
  target: CsWebUiGlobal,
  currentState: () => FrameChannelState,
  bindingTimeoutMs: number,
  bindingPollIntervalMs: number,
): Promise<(frame: Uint8Array) => Promise<unknown>> {
  const deadline = Date.now() + bindingTimeoutMs;
  while (currentState() === "connected") {
    const sender = target.__runicToolkit_applicationBridge_send;
    if (sender !== undefined) return sender;
    if (Date.now() >= deadline) {
      throw new Error(
        `The RunicToolkit Application Bridge native binding was unavailable after ${bindingTimeoutMs}ms.`,
      );
    }
    await delay(Math.min(bindingPollIntervalMs, Math.max(1, deadline - Date.now())));
  }
  throw new Error("The Application Bridge channel is not connected.");
}

function positiveNumber(value: number | undefined, fallback: number, name: string): number {
  const resolved = value ?? fallback;
  if (!Number.isFinite(resolved) || resolved <= 0) {
    throw new RangeError(`${name} must be a positive finite number.`);
  }
  return resolved;
}

function nonNegativeNumber(value: number | undefined, fallback: number, name: string): number {
  const resolved = value ?? fallback;
  if (!Number.isFinite(resolved) || resolved < 0) {
    throw new RangeError(`${name} must be a non-negative finite number.`);
  }
  return resolved;
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
