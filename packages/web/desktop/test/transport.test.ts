import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { Fiber, ManagedRuntime, Stream } from "effect";
import {
  DesktopTransport,
  DesktopTransportLive,
  applicationBridgeCapability,
  applicationBridgeReceiver,
  createDesktopFrameChannel,
  wireProfile,
  type DesktopBootstrap,
  type FrameChannelEvent,
  type WebSocketLike,
} from "../dist/esm/index.js";

const bootstrap: DesktopBootstrap = {
  product: "Runic Desktop",
  profile: wireProfile,
  endpoint: "ws://127.0.0.1:4123/surface/_webui_ws_connect",
  token: 0x01020304,
  sessionCredential: "credential",
};

test("authenticates, negotiates the capability, and matches the portable check-token vector", async () => {
  const sockets: FakeSocket[] = [];
  const channel = createDesktopFrameChannel({
    bootstrap,
    webSocketFactory: (url, protocols) => {
      const socket = new FakeSocket(url, protocols);
      sockets.push(socket);
      return socket;
    },
  });
  const states: string[] = [];
  channel.subscribe((event) => { if (event._tag === "State") states.push(event.state); });

  const connecting = channel.reconnect();
  const socket = sockets[0]!;
  assert.equal(socket.url, bootstrap.endpoint);
  assert.equal(socket.protocols, `runic-desktop.${bootstrap.sessionCredential}`);
  socket.open();

  const vector = JSON.parse(await readFile(
    new URL("../../../../contract/conformance/vectors/protocol.webui-compat-check-token.json", import.meta.url),
    "utf8",
  )) as { input: { value: string } };
  assert.deepEqual(socket.sent[0], Uint8Array.from(Buffer.from(vector.input.value, "base64")));

  socket.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))));
  await connecting;
  assert.equal(channel.state, "connected");
  assert.deepEqual(states, ["connected"]);
  await channel.close("test complete");
});

test("publishes owned host frames and encodes bounded client frames", async () => {
  const socket = new FakeSocket(bootstrap.endpoint, "protocol");
  const channel = createDesktopFrameChannel({ bootstrap, webSocketFactory: () => socket });
  const frames: Uint8Array[] = [];
  channel.subscribe((event: FrameChannelEvent) => { if (event._tag === "Frame") frames.push(event.bytes); });
  const connecting = channel.reconnect();
  socket.open();
  socket.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))));
  await connecting;

  await channel.send(Uint8Array.of(1, 0, 2));
  const call = socket.sent.at(-1)!;
  assert.equal(call[0], 0xdd);
  assert.equal(call[7], 0xf9);
  assert.equal(readZeroTerminated(call, 8), applicationBridgeCapability);
  assert.deepEqual(call.slice(call.length - 4, call.length - 1), Uint8Array.of(1, 0, 2));

  const hostFrame = Uint8Array.of(9, 0, 8);
  const receiver = new TextEncoder().encode(applicationBridgeReceiver);
  socket.receive(packet(0, 0xf8, Uint8Array.of(...receiver, 0, ...hostFrame)));
  hostFrame.fill(7);
  assert.deepEqual(frames, [Uint8Array.of(9, 0, 8)]);
  await channel.close("test complete");
});

test("classifies security, capability, size, reconnect, and teardown failures", async () => {
  const sockets: FakeSocket[] = [];
  const channel = createDesktopFrameChannel({
    bootstrap,
    maxFrameBytes: 3,
    webSocketFactory: () => {
      const socket = new FakeSocket(bootstrap.endpoint, "protocol");
      sockets.push(socket);
      return socket;
    },
  });

  let connecting = channel.reconnect();
  sockets[0]!.open();
  sockets[0]!.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode("other.capability,"))));
  await assert.rejects(connecting, (error: unknown) => tagged(error, "CapabilityDenied", "capabilityDenied"));

  connecting = channel.reconnect();
  sockets[1]!.open();
  sockets[1]!.receive(packet(0, 0xf5, Uint8Array.of(0)));
  await assert.rejects(connecting, (error: unknown) => tagged(error, "AuthenticationDenied", "authenticationDenied"));

  connecting = channel.reconnect();
  sockets[2]!.open();
  sockets[2]!.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))));
  await connecting;
  await assert.rejects(channel.send(Uint8Array.of(1, 2, 3, 4)), (error: unknown) => tagged(error, "LimitExceeded", "limitExceeded"));
  await channel.close("controlled teardown");
  assert.equal(channel.state, "closed");
  assert.equal(sockets[2]!.closeCode, 1000);
  await assert.rejects(channel.reconnect(), (error: unknown) => tagged(error, "TransportClosed", "transportClosed"));
});

test("Effect layer scopes the channel and exposes frame and state streams", async () => {
  const socket = new FakeSocket(bootstrap.endpoint, "protocol");
  const runtime = ManagedRuntime.make(DesktopTransportLive({ bootstrap, webSocketFactory: () => socket }));
  const service = await runtime.runPromise(DesktopTransport);
  const firstState = runtime.runPromise(Stream.runHead(service.states));
  await Promise.resolve();
  const reconnect = runtime.runPromise(service.reconnect);
  socket.open();
  socket.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))));
  await reconnect;
  assert.equal((await firstState)._tag, "Some");

  const firstFrame = runtime.runPromise(Stream.runHead(service.frames));
  await Promise.resolve();
  const receiver = new TextEncoder().encode(applicationBridgeReceiver);
  socket.receive(packet(0, 0xf8, Uint8Array.of(...receiver, 0, 4, 5, 6)));
  const frame = await firstFrame;
  assert.deepEqual(frame._tag === "Some" ? frame.value : undefined, Uint8Array.of(4, 5, 6));
  await runtime.dispose();
  assert.equal(service.channel.state, "closed");
});

test("Effect reconnect interruption closes the owned physical connection", async () => {
  const socket = new FakeSocket(bootstrap.endpoint, "protocol");
  const runtime = ManagedRuntime.make(DesktopTransportLive({ bootstrap, webSocketFactory: () => socket }));
  const service = await runtime.runPromise(DesktopTransport);
  const fiber = runtime.runFork(service.reconnect);
  await runtime.runPromise(Fiber.interrupt(fiber));
  assert.equal(service.channel.state, "closed");
  assert.equal(socket.closeCode, 1000);
  await runtime.dispose();
});

class FakeSocket implements WebSocketLike {
  public readyState = 0;
  public binaryType: BinaryType = "blob";
  public readonly sent: Uint8Array[] = [];
  public closeCode: number | undefined;
  public readonly url: string;
  public readonly protocols: string | string[] | undefined;
  private readonly listeners = new Map<string, Set<(event: Event | MessageEvent<unknown>) => void>>();

  public constructor(
    url: string,
    protocols?: string | string[],
  ) {
    this.url = url;
    this.protocols = protocols;
  }

  public send(data: ArrayBufferView): void {
    this.sent.push(new Uint8Array(data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength)));
  }

  public close(code?: number): void {
    this.closeCode = code;
    this.readyState = 3;
  }

  public addEventListener(type: "open" | "error" | "close" | "message", listener: never): void {
    let group = this.listeners.get(type);
    if (group === undefined) {
      group = new Set();
      this.listeners.set(type, group);
    }
    group.add(listener);
  }

  public removeEventListener(type: "open" | "error" | "close" | "message", listener: never): void {
    this.listeners.get(type)?.delete(listener);
  }

  public open(): void {
    this.readyState = 1;
    this.emit("open", new Event("open"));
  }

  public receive(bytes: Uint8Array): void {
    this.emit("message", new MessageEvent("message", { data: bytes.buffer.slice(0) }));
  }

  private emit(type: string, event: Event | MessageEvent<unknown>): void {
    for (const listener of this.listeners.get(type) ?? []) listener(event);
  }
}

function packet(id: number, command: number, payload: Uint8Array): Uint8Array {
  const result = new Uint8Array(8 + payload.length + 1);
  result[0] = 0xdd;
  new DataView(result.buffer).setUint16(5, id, true);
  result[7] = command;
  result.set(payload, 8);
  return result;
}

function readZeroTerminated(bytes: Uint8Array, offset: number): string {
  const end = bytes.indexOf(0, offset);
  return new TextDecoder().decode(bytes.subarray(offset, end));
}

function tagged(value: unknown, tag: string, category: string): boolean {
  return typeof value === "object" && value !== null &&
    "_tag" in value && value._tag === tag &&
    "category" in value && value.category === category &&
    "retryable" in value && typeof value.retryable === "boolean" &&
    "correlationId" in value && typeof value.correlationId === "string" && value.correlationId.length === 32;
}
