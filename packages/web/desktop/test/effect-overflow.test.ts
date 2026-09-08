import assert from "node:assert/strict";
import test from "node:test";
import { Deferred, Effect, ManagedRuntime, Stream } from "effect";
import {
  DesktopTransport,
  DesktopTransportLive,
  applicationBridgeCapability,
  applicationBridgeReceiver,
  wireProfile,
  type DesktopBootstrap,
  type WebSocketLike,
} from "../dist/esm/index.js";

const bootstrap: DesktopBootstrap = {
  product: "Runic Desktop", profile: wireProfile,
  endpoint: "ws://127.0.0.1:4123/surface/_webui_ws_connect",
  token: 0x01020304, sessionCredential: "credential",
};

test("paused Effect consumer overflow fails terminally and closes the physical socket", async () => {
  const socket = new Socket();
  const runtime = ManagedRuntime.make(DesktopTransportLive({ bootstrap, webSocketFactory: () => socket }));
  try {
    const service = await runtime.runPromise(DesktopTransport);
    const reconnect = runtime.runPromise(service.reconnect);
    socket.open(); socket.authenticate(); await reconnect;
    const entered = Effect.runSync(Deferred.make<void>());
    const resume = Effect.runSync(Deferred.make<void>());
    const result = runtime.runPromise(Effect.result(Stream.runForEach(service.frames, () =>
      Effect.gen(function*() {
        yield* Deferred.succeed(entered, undefined);
        yield* Deferred.await(resume);
      }))));
    await Promise.resolve();
    socket.frame(1);
    await runtime.runPromise(Deferred.await(entered).pipe(Effect.timeout("2 seconds")));
    // The real Effect subscriber is blocked; no mock PubSub/stream is involved.
    for (let index = 0; index < 300; index++) socket.frame(index % 256);
    assert.equal(service.channel.state, "closed");
    assert.equal(socket.readyState, 3);
    assert.equal(socket.closeCalls, 1);
    assert.equal(socket.listenerCount, 0);
    Effect.runSync(Deferred.succeed(resume, undefined));
    const outcome = await result;
    assert.equal(outcome._tag, "Failure");
    if (outcome._tag !== "Failure") throw new Error("Overflow did not fail the frame stream");
    assert.equal(outcome.failure._tag, "LimitExceeded");
    assert.equal(outcome.failure.code, "frame-buffer-overflow");
    assert.equal(outcome.failure.retryable, false);
    // A later subscriber cannot accidentally resume a truncated conversation.
    const later = await runtime.runPromise(Effect.result(Stream.runDrain(service.frames)));
    assert.equal(later._tag, "Failure");
    if (later._tag === "Failure") assert.equal(later.failure, outcome.failure);
    await assert.rejects(service.channel.reconnect(), (error: unknown) =>
      typeof error === "object" && error !== null && "_tag" in error && error._tag === "TransportClosed");
  } finally { await runtime.dispose(); }
  assert.equal(socket.closeCalls, 1);
});

test("Effect frame stream keeps ordinary frame order and content", async () => {
  const socket = new Socket();
  const runtime = ManagedRuntime.make(DesktopTransportLive({ bootstrap, webSocketFactory: () => socket }));
  try {
    const service = await runtime.runPromise(DesktopTransport);
    const reconnect = runtime.runPromise(service.reconnect);
    socket.open(); socket.authenticate(); await reconnect;
    const collected = runtime.runPromise(Stream.runCollect(Stream.take(service.frames, 3)));
    await Promise.resolve();
    socket.frame(9); socket.frame(0); socket.frame(8);
    assert.deepEqual((await collected).map(bytes => [...bytes]), [[9], [0], [8]]);
    assert.equal(service.channel.state, "connected");
    assert.equal(socket.closeCalls, 0);
  } finally { await runtime.dispose(); }
  assert.equal(socket.closeCalls, 1);
  assert.equal(socket.listenerCount, 0);
});

class Socket implements WebSocketLike {
  readyState = 0;
  binaryType: BinaryType = "blob";
  closeCalls = 0;
  private readonly listeners = new Map<string, Set<(event: Event | MessageEvent<unknown>) => void>>();
  get listenerCount(): number { return [...this.listeners.values()].reduce((count, entries) => count + entries.size, 0); }
  send(_data: ArrayBufferView): void {}
  close(): void { this.closeCalls++; this.readyState = 3; }
  addEventListener(type: "open" | "error" | "close" | "message", listener: never): void {
    let entries = this.listeners.get(type);
    if (entries === undefined) { entries = new Set(); this.listeners.set(type, entries); }
    entries.add(listener);
  }
  removeEventListener(type: "open" | "error" | "close" | "message", listener: never): void { this.listeners.get(type)?.delete(listener); }
  open(): void { this.readyState = 1; for (const listener of this.listeners.get("open") ?? []) listener(new Event("open")); }
  authenticate(): void { this.receive(0xf5, Uint8Array.of(1, ...new TextEncoder().encode(`${applicationBridgeCapability},`))); }
  frame(value: number): void { this.receive(0xf8, Uint8Array.of(...new TextEncoder().encode(applicationBridgeReceiver), 0, value)); }
  private receive(command: number, payload: Uint8Array): void {
    const packet = new Uint8Array(8 + payload.length + 1);
    packet[0] = 0xdd; packet[7] = command; packet.set(payload, 8);
    for (const listener of this.listeners.get("message") ?? []) listener(new MessageEvent("message", { data: packet.buffer }));
  }
}
