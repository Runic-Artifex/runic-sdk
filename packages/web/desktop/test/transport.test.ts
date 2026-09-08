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
    new URL("../../../../specs/desktop/conformance/vectors/protocol.webui-compat-check-token.json", import.meta.url),
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

test("host scripts correlate async, binary, and error results without blocking application frames", async () => {
  const socket = new FakeSocket(bootstrap.endpoint);
  const channel = createDesktopFrameChannel({ bootstrap, webSocketFactory: () => socket });
  const connecting = channel.reconnect(); socket.open();
  socket.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))));
  await connecting;
  const global = globalThis as unknown as Record<string, unknown>;
  const frames: Uint8Array[] = [];
  channel.subscribe(event => { if (event._tag === "Frame") frames.push(event.bytes); });
  try {
    socket.receive(packet(41, 0xfe, new TextEncoder().encode("return await new Promise(resolve => { globalThis.__runicCloseTestResolve = resolve; });")));
    const receiver = new TextEncoder().encode(applicationBridgeReceiver);
    socket.receive(packet(0, 0xf8, Uint8Array.of(...receiver, 0, 42)));
    assert.deepEqual(frames, [Uint8Array.of(42)], "pending confirmation must not stall bridge frames");
    (global.__runicCloseTestResolve as (value: boolean) => void)(true);
    await new Promise(resolve => setImmediate(resolve));
    let response = socket.sent.at(-1)!;
    assert.equal(new DataView(response.buffer).getUint16(5, true), 41);
    assert.equal(response[7], 0xfe); assert.equal(response[8], 0);
    assert.equal(readZeroTerminated(response, 9), "true");
    socket.receive(packet(42, 0xfe, new TextEncoder().encode("return new Uint8Array([1, 0, 2]);")));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(socket.sent.at(-1)!.slice(8), Uint8Array.of(0, 1, 0, 2, 0));
    socket.receive(packet(43, 0xfe, new TextEncoder().encode("throw new Error('confirmation failed');")));
    await new Promise(resolve => setImmediate(resolve));
    response = socket.sent.at(-1)!;
    assert.equal(new DataView(response.buffer).getUint16(5, true), 43);
    assert.equal(response[8], 1);
    assert.equal(readZeroTerminated(response, 9), "confirmation failed");
  } finally { delete global.__runicCloseTestResolve; await channel.close("test complete"); }
});

test("quick scripts have no reply and scripts require an authenticated connection and host header", async () => {
  const socket = new FakeSocket(bootstrap.endpoint);
  const channel = createDesktopFrameChannel({ bootstrap, webSocketFactory: () => socket });
  const global = globalThis as unknown as Record<string, unknown>;
  const script = new TextEncoder().encode("globalThis.__runicQuickTest = true;");
  try {
    const connecting = channel.reconnect(); socket.open();
    socket.receive(packet(10, 0xfe, script));
    assert.equal(global.__runicQuickTest, undefined);
    socket.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))));
    await connecting;
    const count = socket.sent.length;
    socket.receive(packet(0, 0xfd, script));
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(global.__runicQuickTest, true); assert.equal(socket.sent.length, count);
    socket.receive(packet(0, 0xfd, new TextEncoder().encode("throw new Error('quick failure');")));
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(socket.sent.length, count); assert.equal(channel.state, "connected");
    delete global.__runicQuickTest;
    const forged = packet(11, 0xfe, script);
    new DataView(forged.buffer).setUint32(1, bootstrap.token + 1, true);
    socket.receive(forged);
    assert.equal(global.__runicQuickTest, undefined); assert.equal(channel.state, "disconnected");
  } finally { delete global.__runicQuickTest; await channel.close("test complete"); }
});

test("late script results never cross a reconnect generation", async () => {
  const sockets: FakeSocket[] = [];
  const channel = createDesktopFrameChannel({ bootstrap, webSocketFactory: () => {
    const socket = new FakeSocket(bootstrap.endpoint); sockets.push(socket); return socket;
  } });
  const global = globalThis as unknown as Record<string, unknown>;
  const authenticate = async () => {
    const connecting = channel.reconnect(); const socket = sockets.at(-1)!; socket.open();
    socket.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))));
    await connecting; return socket;
  };
  try {
    const old = await authenticate();
    old.receive(packet(55, 0xfe, new TextEncoder().encode("return await new Promise(resolve => { globalThis.__runicLateTestResolve = resolve; });")));
    old.disconnect();
    const current = await authenticate(); const count = current.sent.length;
    (global.__runicLateTestResolve as (value: boolean) => void)(true);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(current.sent.length, count); assert.equal(old.sent.length, 1);
  } finally { delete global.__runicLateTestResolve; await channel.close("test complete"); }
});

test("host close terminates the physical session and prohibits reconnection", async () => {
  const socket = new FakeSocket(bootstrap.endpoint);
  const channel = createDesktopFrameChannel({ bootstrap, webSocketFactory: () => socket });
  const connecting = channel.reconnect(); socket.open();
  socket.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))));
  await connecting;
  socket.receive(packet(0, 0xfa, new Uint8Array()));
  assert.equal(socket.closeCode, 1000);
  assert.equal(channel.state, "closed");
  await assert.rejects(channel.reconnect());
});

test("host navigation closes the old session before replacing the document", async () => {
  const original = Object.getOwnPropertyDescriptor(globalThis, "location");
  const socket = new FakeSocket(bootstrap.endpoint);
  const channel = createDesktopFrameChannel({ bootstrap, webSocketFactory: () => socket });
  const destinations: string[] = [];
  Object.defineProperty(globalThis, "location", { configurable: true, value: { replace(url: string) {
    assert.equal(channel.state, "closed"); assert.equal(socket.closeCode, 1000); destinations.push(url);
  } } });
  try {
    const connecting = channel.reconnect(); socket.open();
    socket.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))));
    await connecting;
    socket.receive(packet(0, 0xfb, new TextEncoder().encode("../next?document=2")));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(destinations, ["../next?document=2"]);
    await assert.rejects(channel.reconnect());
  } finally {
    await channel.close("test complete");
    if (original) Object.defineProperty(globalThis, "location", original);
    else Reflect.deleteProperty(globalThis, "location");
  }
});

test("Unicode close reasons stay within the WebSocket byte limit and finish teardown", async () => {
  const socket = new FakeSocket(bootstrap.endpoint);
  const channel = createDesktopFrameChannel({ bootstrap, webSocketFactory: () => socket });
  const connecting = channel.reconnect(); socket.open();
  socket.receive(packet(0, 0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))));
  await connecting;
  const states: string[] = [];
  channel.subscribe(event => { if (event._tag === "State") states.push(event.state); });
  await channel.close("x" + "😀".repeat(31));
  assert.equal(socket.closeReason, "x" + "😀".repeat(30));
  assert.equal(socket.readyState, 3); assert.deepEqual(states, ["closed"]);
});

class FakeSocket implements WebSocketLike {
  public readyState = 0;
  public binaryType: BinaryType = "blob";
  public readonly sent: Uint8Array[] = [];
  public closeCode: number | undefined;
  public closeReason: string | undefined;
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

  public close(code?: number, reason?: string): void {
    if (new TextEncoder().encode(reason).length > 123) throw new SyntaxError("WebSocket close reason exceeds 123 bytes");
    this.closeCode = code;
    this.closeReason = reason;
    this.readyState = 3;
  }

  public disconnect(): void { this.close(1000); this.emit("close", new Event("close")); }

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
  new DataView(result.buffer).setUint32(1, 0xffffffff, true);
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
