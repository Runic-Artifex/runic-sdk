export type FrameChannelState = "connected" | "disconnected" | "closed";

export type FrameChannelEvent =
  | { readonly _tag: "Frame"; readonly bytes: Uint8Array }
  | { readonly _tag: "State"; readonly state: FrameChannelState };

export interface FrameChannel {
  readonly state: FrameChannelState;
  send(bytes: Uint8Array): Promise<void>;
  subscribe(listener: (event: FrameChannelEvent) => void): () => void;
  close(reason: string): Promise<void>;
}

export interface CsWebUiGlobal {
  readonly __runicToolkit_applicationBridge_send?: (frame: Uint8Array) => Promise<void>;
  __runicToolkit_applicationBridge_receiveHostEvent?: (frame: Uint8Array) => void;
}

export function createCsWebUiFrameChannel(target: CsWebUiGlobal = globalThis as CsWebUiGlobal): FrameChannel {
  const sender = target.__runicToolkit_applicationBridge_send;
  if (sender === undefined) {
    throw new Error("The RunicToolkit Application Bridge native binding is unavailable.");
  }
  const listeners = new Set<(event: FrameChannelEvent) => void>();
  let state: FrameChannelState = "connected";
  target.__runicToolkit_applicationBridge_receiveHostEvent = (bytes) => {
    const frame = new Uint8Array(bytes);
    for (const listener of listeners) listener({ _tag: "Frame", bytes: frame });
  };
  return {
    get state() { return state; },
    async send(bytes) {
      if (state !== "connected") throw new Error("The Application Bridge channel is not connected.");
      await sender(new Uint8Array(bytes));
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
