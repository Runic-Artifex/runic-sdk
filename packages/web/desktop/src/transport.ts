import { Schema } from "effect";
import {
  applicationBridgeCapability,
  applicationBridgeReceiver,
  DesktopBootstrapSchema,
  type DesktopBootstrap,
} from "./contract.js";
import { transportError, type DesktopTransportError } from "./errors.js";

export type FrameChannelState = "connected" | "disconnected" | "closed";

export type FrameChannelEvent =
  | { readonly _tag: "Frame"; readonly bytes: Uint8Array }
  | { readonly _tag: "State"; readonly state: FrameChannelState };

/** Structural match for @runic-artifex/application-bridge's transport boundary. */
export interface FrameChannel {
  readonly state: FrameChannelState;
  send(bytes: Uint8Array): Promise<void>;
  subscribe(listener: (event: FrameChannelEvent) => void): () => void;
  close(reason: string): Promise<void>;
}

export interface ReconnectableFrameChannel extends FrameChannel {
  reconnect(): Promise<void>;
}

export interface WebSocketLike {
  readonly readyState: number;
  binaryType: BinaryType;
  send(data: ArrayBufferView): void;
  close(code?: number, reason?: string): void;
  addEventListener(type: "open" | "error" | "close", listener: EventListener): void;
  addEventListener(type: "message", listener: (event: MessageEvent<unknown>) => void): void;
  removeEventListener(type: "open" | "error" | "close", listener: EventListener): void;
  removeEventListener(type: "message", listener: (event: MessageEvent<unknown>) => void): void;
}

export interface DesktopFrameChannelOptions {
  readonly bootstrap?: DesktopBootstrap;
  readonly webSocketFactory?: (url: string, protocols?: string | string[]) => WebSocketLike;
  readonly maxFrameBytes?: number;
}

const signature = 0xdd;
const callFunction = 0xf9;
const sendRaw = 0xf8;
const addBinding = 0xf7;
const multi = 0xf6;
const checkToken = 0xf5;
const headerSize = 8;
const multiChunkSize = 65_500;
const defaultMaxFrameBytes = 16 * 1024 * 1024;
const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });

export function createDesktopFrameChannel(options: DesktopFrameChannelOptions = {}): ReconnectableFrameChannel {
  const candidate = options.bootstrap ?? globalThis.runicDesktop;
  if (!Schema.is(DesktopBootstrapSchema)(candidate)) {
    throw transportError(
      "ConfigurationInvalid",
      "bootstrap-invalid",
      "Load /runic-desktop.js before creating the Runic Desktop transport.",
    );
  }
  const maxFrameBytes = options.maxFrameBytes ?? defaultMaxFrameBytes;
  if (!Number.isSafeInteger(maxFrameBytes) || maxFrameBytes <= 0) {
    throw transportError("ConfigurationInvalid", "frame-limit-invalid", "maxFrameBytes must be a positive safe integer.");
  }
  const factory = options.webSocketFactory ?? ((url, protocols) => new WebSocket(url, protocols) as WebSocketLike);
  return new DesktopFrameChannel(candidate, factory, maxFrameBytes);
}

class DesktopFrameChannel implements ReconnectableFrameChannel {
  private readonly listeners = new Set<(event: FrameChannelEvent) => void>();
  private currentState: FrameChannelState = "disconnected";
  private socket: WebSocketLike | undefined;
  private detach: (() => void) | undefined;
  private connecting: Promise<void> | undefined;
  private rejectConnecting: ((reason: DesktopTransportError) => void) | undefined;
  private generation = 0;
  private nextCallId = 0;
  private expectedMultiLength: number | undefined;
  private multiChunks: Uint8Array[] = [];
  private multiBytes = 0;

  public constructor(
    private readonly bootstrap: DesktopBootstrap,
    private readonly factory: (url: string, protocols?: string | string[]) => WebSocketLike,
    private readonly maxFrameBytes: number,
  ) {}

  public get state(): FrameChannelState { return this.currentState; }

  public async reconnect(): Promise<void> {
    if (this.currentState === "closed") {
      throw transportError("TransportClosed", "transport-closed", "The Runic Desktop transport is closed.");
    }
    if (this.currentState === "connected") return;
    if (this.connecting !== undefined) return this.connecting;

    this.replaceSocket();
    const generation = ++this.generation;
    let resolve!: () => void;
    let reject!: (reason: DesktopTransportError) => void;
    const attempt = new Promise<void>((succeed, fail) => { resolve = succeed; reject = fail; });
    this.connecting = attempt;
    this.rejectConnecting = reject;
    const settle = (error?: DesktopTransportError): void => {
      if (generation !== this.generation || this.connecting !== attempt) return;
      this.connecting = undefined;
      this.rejectConnecting = undefined;
      if (error === undefined) resolve();
      else reject(error);
    };

    let socket: WebSocketLike;
    try {
      const protocol = this.bootstrap.sessionCredential.length === 0
        ? undefined
        : `runic-desktop.${this.bootstrap.sessionCredential}`;
      socket = this.factory(this.bootstrap.endpoint, protocol);
      socket.binaryType = "arraybuffer";
    } catch {
      const error = transportError(
        "TransportUnavailable",
        "connection-failed",
        "The Runic Desktop connection could not be created.",
        true,
      );
      settle(error);
      return attempt;
    }

    const opened: EventListener = () => {
      if (generation !== this.generation || this.currentState === "closed") return;
      try {
        socket.send(createPacket(this.bootstrap.token, 7, checkToken, Uint8Array.of(0)));
      } catch {
        failConnection(settle, "authentication-send-failed");
      }
    };
    const failed: EventListener = () => failConnection(settle, "connection-failed");
    const closed: EventListener = () => {
      if (generation !== this.generation || this.currentState === "closed") return;
      this.setState("disconnected");
      settle(transportError(
        "TransportUnavailable",
        "connection-closed",
        "The Runic Desktop connection closed before authentication completed.",
        true,
      ));
    };
    const message = (event: MessageEvent<unknown>): void => {
      if (generation !== this.generation || this.currentState === "closed") return;
      const bytes = ownedBytes(event.data);
      if (bytes === undefined) {
        socket.close(1003, "Runic Desktop requires binary frames");
        this.setState("disconnected");
        settle(transportError("InvalidFrame", "binary-frame-required", "The host sent a non-binary frame."));
        return;
      }
      try {
        const packet = this.reassemble(bytes);
        if (packet !== undefined) this.receivePacket(packet, settle);
      } catch (error) {
        const typed = isDesktopTransportError(error)
          ? error
          : transportError("InvalidFrame", "frame-invalid", "The host sent an invalid Runic Desktop frame.");
        socket.close(1007, "Invalid Runic Desktop frame");
        this.setState("disconnected");
        settle(typed);
      }
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

    function failConnection(
      complete: (error?: DesktopTransportError) => void,
      code: string,
    ): void {
      complete(transportError(
        "TransportUnavailable",
        code,
        "The Runic Desktop connection failed.",
        true,
      ));
    }
  }

  public async send(bytes: Uint8Array): Promise<void> {
    if (this.currentState !== "connected" || this.socket === undefined) {
      throw transportError("TransportUnavailable", "transport-unavailable", "The Runic Desktop transport is unavailable.", true);
    }
    if (bytes.byteLength > this.maxFrameBytes) {
      throw transportError("LimitExceeded", "frame-limit-exceeded", "The Application Bridge frame exceeds the configured limit.");
    }
    const id = this.nextCallId = (this.nextCallId - 1) & 0xffff;
    const name = encoder.encode(applicationBridgeCapability);
    const lengths = encoder.encode(String(bytes.byteLength));
    const payload = new Uint8Array(name.length + 1 + lengths.length + 1 + bytes.byteLength + 1);
    let offset = 0;
    payload.set(name, offset);
    offset += name.length + 1;
    payload.set(lengths, offset);
    offset += lengths.length + 1;
    payload.set(bytes, offset);
    try {
      this.sendPacket(createPacket(this.bootstrap.token, id, callFunction, payload));
    } catch {
      this.setState("disconnected");
      throw transportError("TransportUnavailable", "send-failed", "The Application Bridge frame could not be sent.", true);
    }
  }

  public subscribe(listener: (event: FrameChannelEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  public async close(reason: string): Promise<void> {
    if (this.currentState === "closed") return;
    this.currentState = "closed";
    this.generation++;
    this.rejectConnecting?.(transportError("TransportClosed", "transport-closed", "The Runic Desktop transport was closed."));
    this.rejectConnecting = undefined;
    this.connecting = undefined;
    this.detach?.();
    this.detach = undefined;
    this.socket?.close(1000, boundedCloseReason(reason));
    this.socket = undefined;
    this.publish({ _tag: "State", state: "closed" });
    this.listeners.clear();
  }

  private receivePacket(packet: Uint8Array, settle: (error?: DesktopTransportError) => void): void {
    if (packet.length < headerSize || packet[0] !== signature) {
      throw transportError("InvalidFrame", "header-invalid", "The host frame header is invalid.");
    }
    const command = packet[7];
    if (command === checkToken) {
      if (packet.length < headerSize + 2 || packet[headerSize] !== 1) {
        throw transportError("AuthenticationDenied", "authentication-denied", "The Runic Desktop session credential was rejected.");
      }
      const capabilities = readText(packet, headerSize + 1).split(",").filter(Boolean);
      if (!capabilities.includes(applicationBridgeCapability)) {
        throw transportError("CapabilityDenied", "application-bridge-unavailable", "The host did not offer the Application Bridge capability.");
      }
      this.setState("connected");
      settle();
      return;
    }
    if (command === addBinding) return;
    if (this.currentState !== "connected" || command !== sendRaw) return;
    const functionName = readText(packet, headerSize);
    if (functionName !== applicationBridgeReceiver) return;
    const start = headerSize + encoder.encode(functionName).length + 1;
    const end = packet.length > start && packet[packet.length - 1] === 0 ? packet.length - 1 : packet.length;
    const frame = packet.slice(start, end);
    if (frame.byteLength > this.maxFrameBytes) {
      throw transportError("LimitExceeded", "frame-limit-exceeded", "The host Application Bridge frame exceeds the configured limit.");
    }
    this.publish({ _tag: "Frame", bytes: frame });
  }

  private reassemble(bytes: Uint8Array): Uint8Array | undefined {
    if (this.expectedMultiLength !== undefined) {
      this.multiChunks.push(bytes);
      this.multiBytes += bytes.length;
      if (this.multiBytes > this.expectedMultiLength) {
        throw transportError("InvalidFrame", "multi-length-invalid", "The host sent more bytes than its MULTI header declared.");
      }
      if (this.multiBytes !== this.expectedMultiLength) return undefined;
      const packet = new Uint8Array(this.multiBytes);
      let offset = 0;
      for (const chunk of this.multiChunks) {
        packet.set(chunk, offset);
        offset += chunk.length;
      }
      this.expectedMultiLength = undefined;
      this.multiChunks = [];
      this.multiBytes = 0;
      return packet;
    }
    if (bytes.length >= headerSize + 2 && bytes[0] === signature && bytes[7] === multi) {
      const length = Number.parseInt(readText(bytes, headerSize), 10);
      if (!Number.isSafeInteger(length) || length <= 0 || length > this.maxFrameBytes + 4096) {
        throw transportError("LimitExceeded", "multi-limit-exceeded", "The host MULTI frame exceeds the configured limit.");
      }
      this.expectedMultiLength = length;
      return undefined;
    }
    return bytes;
  }

  private sendPacket(packet: Uint8Array): void {
    if (this.socket === undefined) throw new Error("socket missing");
    if (packet.length < multiChunkSize) {
      this.socket.send(packet);
      return;
    }
    const length = encoder.encode(String(packet.length));
    const header = new Uint8Array(headerSize + length.length + 1);
    header[0] = signature;
    header[7] = multi;
    header.set(length, headerSize);
    this.socket.send(header);
    for (let offset = 0; offset < packet.length; offset += multiChunkSize) {
      this.socket.send(packet.subarray(offset, Math.min(offset + multiChunkSize, packet.length)));
    }
  }

  private replaceSocket(): void {
    this.detach?.();
    this.detach = undefined;
    this.socket?.close(1000, "Runic Desktop reconnect");
    this.socket = undefined;
    this.expectedMultiLength = undefined;
    this.multiChunks = [];
    this.multiBytes = 0;
    this.setState("disconnected");
  }

  private setState(state: FrameChannelState): void {
    if (this.currentState === state) return;
    this.currentState = state;
    this.publish({ _tag: "State", state });
  }

  private publish(event: FrameChannelEvent): void {
    for (const listener of this.listeners) listener(event);
  }
}

function createPacket(token: number, id: number, command: number, data: Uint8Array): Uint8Array {
  const packet = new Uint8Array(headerSize + data.length);
  const view = new DataView(packet.buffer);
  packet[0] = signature;
  view.setUint32(1, token, true);
  view.setUint16(5, id, true);
  packet[7] = command;
  packet.set(data, headerSize);
  return packet;
}

function readText(packet: Uint8Array, offset: number): string {
  let end = packet.indexOf(0, offset);
  if (end < 0) end = packet.length;
  try {
    return decoder.decode(packet.subarray(offset, end));
  } catch {
    throw transportError("InvalidFrame", "utf8-invalid", "The host frame contains invalid UTF-8.");
  }
}

function ownedBytes(value: unknown): Uint8Array | undefined {
  if (value instanceof ArrayBuffer) return new Uint8Array(value.slice(0));
  if (ArrayBuffer.isView(value)) {
    return new Uint8Array(value.buffer.slice(value.byteOffset, value.byteOffset + value.byteLength));
  }
  return undefined;
}

function boundedCloseReason(reason: string): string {
  return new TextDecoder().decode(encoder.encode(reason).slice(0, 123));
}

function isDesktopTransportError(value: unknown): value is DesktopTransportError {
  return typeof value === "object" && value !== null && "_tag" in value;
}
