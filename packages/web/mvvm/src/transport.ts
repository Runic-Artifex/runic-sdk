import { parseClientMessage, parseHostMessage, validateClientMessage } from "./validation.js";
import { PROTOCOL_LIMITS, type ClientMessage, type HostMessage } from "./protocol.js";


/** A runtime-supplied binary channel. This deliberately does not depend on WebSocket or the DOM. */
export interface FrameChannel {
  send(frame: Uint8Array): void | Promise<void>;
  close(reason?: string): void | Promise<void>;
  subscribe(observer: FrameChannelObserver): () => void;
}

export interface FrameChannelObserver {
  frame(frame: Uint8Array): void;
  close(cause?: unknown): void;
  error(cause: unknown): void;
}

export type TransportState = "connected" | "disconnected" | "faulted" | "closed";

export type TransportEvent =
  | { readonly type: "send"; readonly message: ClientMessage; readonly rawFrame: Uint8Array }
  | { readonly type: "message"; readonly message: HostMessage; readonly rawFrame: Uint8Array }
  | { readonly type: "state"; readonly previous: TransportState; readonly current: TransportState }
  | { readonly type: "protocolError"; readonly error: ProtocolTransportError };

export type TransportListener = (event: TransportEvent) => void;

export interface ProtocolTransportLimits {
  readonly maxFrameBytes: number;
  readonly maxJsonDepth: number;
}

export type ProtocolTransportErrorCode =
  | "frame-too-large"
  | "invalid-utf8"
  | "invalid-host-message"
  | "invalid-client-message"
  | "channel-failure"
  | "transport-closed";

/** A bounded transport error which never includes the received frame or a capability token. */
export class ProtocolTransportError extends Error {
  public readonly code: ProtocolTransportErrorCode;
  public override readonly cause: unknown;

  public constructor(code: ProtocolTransportErrorCode, message: string, cause?: unknown) {
    super(message);
    this.name = "ProtocolTransportError";
    this.code = code;
    this.cause = cause;
  }
}

/**
 * Validates and encodes client envelopes and validates every host frame before dispatch.
 * Replacing the channel is explicit so reconnect policy remains an application concern.
 */
export class ProtocolTransport {
  private channel: FrameChannel;
  private unsubscribeChannel: (() => void) | undefined;
  private readonly listeners = new Set<TransportListener>();
  private _state: TransportState = "connected";
  private limits: ProtocolTransportLimits = {
    maxFrameBytes: PROTOCOL_LIMITS.maxFrameBytes,
    maxJsonDepth: PROTOCOL_LIMITS.maxJsonDepth,
  };

  public constructor(channel: FrameChannel) {
    this.channel = channel;
    this.subscribeToChannel();
  }

  public get state(): TransportState {
    return this._state;
  }

  public subscribe(listener: TransportListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Applies limits negotiated for the current physical connection. */
  public configureLimits(limits: ProtocolTransportLimits): void {
    const maxFrameBytes = limits?.maxFrameBytes;
    const maxJsonDepth = limits?.maxJsonDepth;
    if (
      !Number.isSafeInteger(maxFrameBytes) ||
      maxFrameBytes < 1_024 ||
      maxFrameBytes > PROTOCOL_LIMITS.maxFrameBytes ||
      !Number.isSafeInteger(maxJsonDepth) ||
      maxJsonDepth < 1 ||
      maxJsonDepth > PROTOCOL_LIMITS.maxJsonDepth
    ) {
      throw new RangeError("Negotiated MVVM transport limits are outside the protocol bounds.");
    }
    this.limits = { maxFrameBytes, maxJsonDepth };
  }

  /** Restores the hard v1 ceilings before a new negotiation. */
  public resetLimits(): void {
    this.limits = {
      maxFrameBytes: PROTOCOL_LIMITS.maxFrameBytes,
      maxJsonDepth: PROTOCOL_LIMITS.maxJsonDepth,
    };
  }

  public async send(message: ClientMessage): Promise<void> {
    if (this._state !== "connected") {
      throw new ProtocolTransportError("transport-closed", "The MVVM transport is not connected.");
    }

    try {
      validateClientMessage(message);
    } catch (cause) {
      throw new ProtocolTransportError(
        "invalid-client-message",
        "The client message does not conform to webuitoolkit.mvvm/1.",
        cause,
      );
    }

    const serialized = serializeJson(message);
    const frame = encodeUtf8(serialized);
    if (frame.byteLength > this.limits.maxFrameBytes) {
      throw new ProtocolTransportError("frame-too-large", "The encoded client frame exceeds the protocol limit.");
    }
    try {
      parseClientMessage(serialized, this.limits);
    } catch (cause) {
      throw new ProtocolTransportError(
        "invalid-client-message",
        "The client message does not conform to the negotiated webuitoolkit.mvvm/1 limits.",
        cause,
      );
    }

    try {
      this.emit({ type: "send", message, rawFrame: frame.slice() });
      await this.channel.send(frame);
    } catch (cause) {
      this.handleChannelFailure(cause);
      throw new ProtocolTransportError("channel-failure", "The MVVM channel could not send the frame.", cause);
    }
  }

  /** Rebinds this transport after the caller has established a new physical connection. */
  public replaceChannel(channel: FrameChannel): void {
    if (this._state === "closed") {
      throw new ProtocolTransportError("transport-closed", "A closed MVVM transport cannot reconnect.");
    }
    this.unsubscribeChannel?.();
    this.channel = channel;
    this.resetLimits();
    this.subscribeToChannel();
    this.transition("connected");
  }

  /** Allows small adapters and deterministic tests to inject an already-framed host message. */
  public receive(frame: Uint8Array): void {
    if (this._state !== "connected") return;
    if (!(frame instanceof Uint8Array) || frame.byteLength > this.limits.maxFrameBytes) {
      this.failProtocol("frame-too-large", "The received frame exceeds the protocol limit.");
      return;
    }

    const retainedFrame = frame.slice();
    let text: string;
    try {
      text = decodeUtf8(retainedFrame);
    } catch (cause) {
      this.failProtocol("invalid-utf8", "The received frame is not valid UTF-8.", cause);
      return;
    }

    try {
      const message = parseHostMessage(text, this.limits);
      this.emit({ type: "message", message, rawFrame: retainedFrame });
    } catch (cause) {
      this.failProtocol(
        "invalid-host-message",
        "The received frame does not conform to webuitoolkit.mvvm/1.",
        cause,
      );
    }
  }

  public disconnect(cause?: unknown): void {
    if (this._state !== "connected") return;
    this.transition("disconnected");
    if (cause !== undefined) {
      this.emit({
        type: "protocolError",
        error: new ProtocolTransportError("channel-failure", "The MVVM channel disconnected.", cause),
      });
    }
  }

  public async close(reason?: string): Promise<void> {
    if (this._state === "closed") return;
    this.unsubscribeChannel?.();
    this.unsubscribeChannel = undefined;
    this.transition("closed");
    await this.channel.close(reason);
  }

  private subscribeToChannel(): void {
    this.unsubscribeChannel = this.channel.subscribe({
      frame: (frame) => this.receive(frame),
      close: (cause) => this.disconnect(cause),
      error: (cause) => this.handleChannelFailure(cause),
    });
  }

  private handleChannelFailure(cause: unknown): void {
    if (this._state !== "connected") return;
    this.transition("disconnected");
    this.emit({
      type: "protocolError",
      error: new ProtocolTransportError("channel-failure", "The MVVM channel failed.", cause),
    });
  }

  private failProtocol(code: ProtocolTransportErrorCode, message: string, cause?: unknown): void {
    const error = new ProtocolTransportError(code, message, cause);
    this.transition("faulted");
    this.emit({ type: "protocolError", error });
    void this.channel.close("protocol error");
  }

  private transition(current: TransportState): void {
    if (current === this._state) return;
    const previous = this._state;
    this._state = current;
    this.emit({ type: "state", previous, current });
  }

  private emit(event: TransportEvent): void {
    for (const listener of [...this.listeners]) {
      try {
        listener(event);
      } catch {
        // Consumer callbacks cannot prevent delivery to later listeners.
      }
    }
  }
}

/** Losslessly serializes protocol revisions represented by bigint as bare JSON integers. */
export function serializeJson(value: unknown): string {
  if (value === null) return "null";
  switch (typeof value) {
    case "string":
      return JSON.stringify(value);
    case "boolean":
      return value ? "true" : "false";
    case "number":
      if (!Number.isFinite(value)) throw new TypeError("JSON numbers must be finite.");
      return JSON.stringify(value);
    case "bigint":
      return value.toString(10);
    case "object":
      if (Array.isArray(value)) return `[${value.map(serializeJson).join(",")}]`;
      return `{${Object.keys(value)
        .map((key) => `${JSON.stringify(key)}:${serializeJson((value as Record<string, unknown>)[key])}`)
        .join(",")}}`;
    default:
      throw new TypeError("The value cannot be represented in JSON.");
  }
}

export function encodeUtf8(text: string): Uint8Array {
  const bytes: number[] = [];
  for (let index = 0; index < text.length; index++) {
    let codePoint = text.charCodeAt(index);
    if (codePoint >= 0xd800 && codePoint <= 0xdbff) {
      if (index + 1 >= text.length) throw new TypeError("A JSON string contains an unpaired surrogate.");
      const low = text.charCodeAt(++index);
      if (low < 0xdc00 || low > 0xdfff) throw new TypeError("A JSON string contains an unpaired surrogate.");
      codePoint = 0x10000 + ((codePoint - 0xd800) << 10) + (low - 0xdc00);
    } else if (codePoint >= 0xdc00 && codePoint <= 0xdfff) {
      throw new TypeError("A JSON string contains an unpaired surrogate.");
    }
    if (codePoint <= 0x7f) bytes.push(codePoint);
    else if (codePoint <= 0x7ff) bytes.push(0xc0 | (codePoint >> 6), 0x80 | (codePoint & 0x3f));
    else if (codePoint <= 0xffff)
      bytes.push(0xe0 | (codePoint >> 12), 0x80 | ((codePoint >> 6) & 0x3f), 0x80 | (codePoint & 0x3f));
    else
      bytes.push(
        0xf0 | (codePoint >> 18),
        0x80 | ((codePoint >> 12) & 0x3f),
        0x80 | ((codePoint >> 6) & 0x3f),
        0x80 | (codePoint & 0x3f),
      );
  }
  return Uint8Array.from(bytes);
}

export function decodeUtf8(bytes: Uint8Array): string {
  if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    throw new TypeError("A byte-order mark is not allowed.");
  }
  let result = "";
  for (let index = 0; index < bytes.length; ) {
    const first = bytes[index++]!;
    let codePoint: number;
    let continuationCount: number;
    if (first <= 0x7f) {
      codePoint = first;
      continuationCount = 0;
    } else if (first >= 0xc2 && first <= 0xdf) {
      codePoint = first & 0x1f;
      continuationCount = 1;
    } else if (first >= 0xe0 && first <= 0xef) {
      codePoint = first & 0x0f;
      continuationCount = 2;
    } else if (first >= 0xf0 && first <= 0xf4) {
      codePoint = first & 0x07;
      continuationCount = 3;
    } else throw new TypeError("Invalid UTF-8 leading byte.");

    for (let part = 0; part < continuationCount; part++) {
      if (index >= bytes.length) throw new TypeError("Truncated UTF-8 sequence.");
      const continuation = bytes[index++]!;
      if ((continuation & 0xc0) !== 0x80) throw new TypeError("Invalid UTF-8 continuation byte.");
      codePoint = (codePoint << 6) | (continuation & 0x3f);
    }
    if (
      (continuationCount === 1 && codePoint < 0x80) ||
      (continuationCount === 2 && codePoint < 0x800) ||
      (continuationCount === 3 && codePoint < 0x10000) ||
      codePoint > 0x10ffff ||
      (codePoint >= 0xd800 && codePoint <= 0xdfff)
    ) {
      throw new TypeError("Non-canonical UTF-8 sequence.");
    }
    result += codePoint <= 0xffff ? String.fromCharCode(codePoint) : String.fromCodePoint(codePoint);
  }
  return result;
}
