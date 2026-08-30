import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { Effect, Layer, Schema, Stream } from "effect";
import {
  ApplicationBridge,
  ApplicationBridgeLive,
  MockApplicationBridge,
  TestApplicationBridge,
  createApplicationBridgeRuntime,
  createApplicationBridgeController,
  createInMemoryFrameChannelPair,
  createWebSocketFrameChannel,
  ClientEnvelopeSchema,
  HostEnvelopeSchema,
  defineApplicationContract,
  type FrameChannel,
  type FrameChannelEvent,
  type WebSocketFrameSocket,
} from "../dist/esm/index.js";

test("transport-neutral conformance fixtures validate paired reconnect epochs", async () => {
  const decode = async (path: string) => JSON.parse(await readFile(`../../../protocol/application-bridge/conformance/${path}`, "utf8"));
  const initial = await decode("initialize.client.json");
  const resync = await decode("resynchronize.client.json");
  const snapshot = await decode("resynchronized.host.json");
  const oldAdmission = await decode("late-old-admission-error.host.json");
  const futureAdmission = await decode("future-admission-error.host.json");
  assert.equal((await Effect.runPromise(Schema.decodeUnknown(ClientEnvelopeSchema)(initial))).connectionEpoch, 0);
  assert.equal((await Effect.runPromise(Schema.decodeUnknown(ClientEnvelopeSchema)(resync))).connectionEpoch, 1);
  assert.equal((await Effect.runPromise(Schema.decodeUnknown(HostEnvelopeSchema)(snapshot))).connectionEpoch, 1);
  assert.equal((await Effect.runPromise(Schema.decodeUnknown(HostEnvelopeSchema)(oldAdmission))).sequence, 0);
  assert.equal((await Effect.runPromise(Schema.decodeUnknown(HostEnvelopeSchema)(futureAdmission))).connectionEpoch, 2);
  assert.equal(snapshot.sequence, 1);
});

test("the in-memory transport supplies owned frames and mirrored lifecycle state", async () => {
  const pair = createInMemoryFrameChannelPair();
  const received = new Promise<Uint8Array>((resolve) => {
    pair.host.subscribe((event) => {
      if (event._tag === "Frame") resolve(event.bytes);
    });
  });
  const source = Uint8Array.of(1, 2, 3);
  await pair.client.send(source);
  source[0] = 9;
  assert.deepEqual(await received, Uint8Array.of(1, 2, 3));
  await pair.client.close("test complete");
  assert.equal(pair.host.state, "disconnected");
});

test("the WebSocket channel owns binary frames and replaces physical sockets", async () => {
  const sockets: FakeWebSocket[] = [];
  const channel = createWebSocketFrameChannel(() => {
    const socket = new FakeWebSocket();
    sockets.push(socket);
    return socket;
  });
  const events: FrameChannelEvent[] = [];
  channel.subscribe((event) => events.push(event));
  await channel.reconnect();
  assert.equal(channel.state, "connected");
  assert.equal(sockets.length, 1);
  assert.equal(sockets[0]?.binaryType, "arraybuffer");
  const inbound = Uint8Array.of(1, 2, 3);
  sockets[0]?.message(inbound.buffer);
  inbound[0] = 9;
  assert.deepEqual(events.at(-1), { _tag: "Frame", bytes: Uint8Array.of(1, 2, 3) });
  const outbound = Uint8Array.of(4, 5, 6);
  await channel.send(outbound);
  outbound[0] = 9;
  assert.deepEqual(sockets[0]?.sent, [Uint8Array.of(4, 5, 6)]);
  await channel.reconnect();
  assert.equal(sockets.length, 2);
  assert.equal(sockets[0]?.closeCode, 1000);
  sockets[1]?.close();
  assert.equal(channel.state, "disconnected");
  await channel.close("test complete");
  assert.equal(channel.state, "closed");
});

test("the WebSocket channel settles failed and interrupted reconnect attempts", async () => {
  const openingSocket = new FakeWebSocket(0);
  const sockets = [new FakeWebSocket(3), openingSocket];
  const channel = createWebSocketFrameChannel(() => {
    const socket = sockets.shift();
    if (socket === undefined) throw new Error("unexpected connection");
    return socket;
  });
  await assert.rejects(channel.reconnect(), /connection failed/);
  const reconnect = channel.reconnect();
  openingSocket.open();
  await reconnect;

  const pendingSocket = new FakeWebSocket(0);
  const pendingChannel = createWebSocketFrameChannel(() => pendingSocket);
  const pending = pendingChannel.reconnect();
  await pendingChannel.close("test complete");
  await assert.rejects(pending, /channel is closed/);
});

const Snapshot = Schema.Struct({ revision: Schema.Int, view: Schema.String });
const Command = Schema.Union(
  Schema.TaggedStruct("InitializeApplication", {}),
  Schema.TaggedStruct("Navigate", { target: Schema.String }),
);
const Receipt = Schema.TaggedStruct("NavigationAccepted", { revision: Schema.Int });
const HostEvent = Schema.TaggedStruct("NavigationChanged", { revision: Schema.Int, view: Schema.String });
const contract = defineApplicationContract({
  identity: "runic.test",
  version: 1,
  fingerprint: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  command: Command,
  receipt: Receipt,
  event: HostEvent,
  snapshot: Snapshot,
  initialize: { _tag: "InitializeApplication" } as const,
});

test("mock and live layers expose the same semantic command sequence", async () => {
  const mock = MockApplicationBridge({
    initialize: () => Effect.succeed({ revision: 0, view: "Welcome" }),
    dispatch: (command, publish) => command._tag === "Navigate"
      ? publish({ _tag: "NavigationChanged", revision: 1, view: command.target }).pipe(
        Effect.as({ _tag: "NavigationAccepted", revision: 1 } as const),
      )
      : Effect.die("unexpected command"),
  });
  await semanticSuite(mock);
  await semanticSuite(ApplicationBridgeLive(contract, new LoopbackChannel()));
});

test("one ManagedRuntime owns and disposes the bridge layer", async () => {
  const channel = new LoopbackChannel();
  const runtime = createApplicationBridgeRuntime(ApplicationBridgeLive(contract, channel));
  const snapshot = await runtime.runPromise(ApplicationBridge.pipe(Effect.flatMap((bridge) => bridge.initialize)));
  assert.deepEqual(snapshot, { revision: 0, view: "Welcome" });
  await runtime.dispose();
  assert.equal(channel.state, "closed");
});

test("the neutral layer connects an initially disconnected structural channel", async () => {
  const channel = new InitiallyDisconnectedLoopbackChannel();
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel));
  try {
    assert.deepEqual(await controller.initialize(), { revision: 0, view: "Welcome" });
    assert.equal(channel.reconnectCalls, 1);
  } finally {
    await controller.dispose();
  }
  assert.equal(channel.state, "closed");
});

test("interrupting initial connection closes its scoped transport", async () => {
  const socket = new FakeWebSocket(0);
  const channel = createWebSocketFrameChannel(() => socket);
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel));
  try {
    const fiber = controller.fork(controller.effects.initialize);
    await Promise.resolve();
    await controller.interrupt(fiber);
    assert.equal(channel.state, "closed");
    assert.equal(socket.closeCode, 1000);
  } finally {
    await controller.dispose();
  }
});

test("the controller composes and forks Effect programs in its owned runtime", async () => {
  const channel = new LoopbackChannel();
  const controller = createApplicationBridgeController(
    contract,
    ApplicationBridgeLive(contract, channel),
  );
  try {
    const receipt = await controller.run(Effect.gen(function*() {
      const snapshot = yield* controller.effects.initialize;
      assert.equal(snapshot.view, "Welcome");
      return yield* controller.effects.dispatch({ _tag: "Navigate", target: "Complete" });
    }));
    assert.deepEqual(receipt, { _tag: "NavigationAccepted", revision: 1 });

    const fiber = controller.fork(Effect.never);
    const interrupted = await controller.interrupt(fiber);
    assert.equal(interrupted._tag, "Failure");
    assert.equal((await controller.await(fiber))._tag, "Failure");
  } finally {
    await controller.dispose();
  }
});

test("the Effect runtime validates every envelope in a returned host batch", async () => {
  const channel = new ReturnedBatchChannel();
  const controller = createApplicationBridgeController(
    contract,
    ApplicationBridgeLive(contract, channel),
  );
  const event = new Promise<{ _tag: string; revision: number; view: string }>((resolve, reject) => {
    controller.subscribe(resolve, reject);
  });
  try {
    assert.deepEqual(await controller.initialize(), { revision: 0, view: "Welcome" });
    assert.deepEqual(
      await controller.dispatch({ _tag: "Navigate", target: "Complete" }),
      { _tag: "NavigationAccepted", revision: 1 },
    );
    assert.deepEqual(await event, { _tag: "NavigationChanged", revision: 1, view: "Complete" });
  } finally {
    await controller.dispose();
  }
});

test("returned host batches enforce the browser item limit", async () => {
  const channel = new ReturnedBatchChannel();
  const controller = createApplicationBridgeController(
    contract,
    ApplicationBridgeLive(contract, channel, { maxBatchFrames: 1 }),
  );
  try {
    await controller.initialize();
    await assert.rejects(
      controller.dispatch({ _tag: "Navigate", target: "Complete" }),
      /batch exceeded the configured item limit/,
    );
  } finally {
    await controller.dispose();
  }
});

test("validated event overflow fails pending work and requires recovery", async () => {
  const channel = new ReturnedBatchChannel(4);
  const runtime = createApplicationBridgeRuntime(
    ApplicationBridgeLive(contract, channel, { maxBufferedEvents: 1 }),
  );
  runtime.runFork(ApplicationBridge.pipe(Effect.flatMap((bridge) =>
    Stream.runForEach(bridge.events, () => Effect.sleep("1 second")),
  )));
  try {
    await new Promise<void>((resolve) => setImmediate(resolve));
    await runtime.runPromise(ApplicationBridge.pipe(Effect.flatMap((bridge) => bridge.initialize)));
    await assert.rejects(
      runtime.runPromise(ApplicationBridge.pipe(Effect.flatMap((bridge) =>
        bridge.dispatch({ _tag: "Navigate", target: "Complete" }),
      ))),
      /validated host event buffer overflowed/,
    );
    await assert.rejects(
      runtime.runPromise(ApplicationBridge.pipe(Effect.flatMap((bridge) => bridge.initialize))),
      /requires authoritative recovery/,
    );
  } finally {
    await runtime.dispose();
  }
});

test("the browser retains command identifiers only while requests are pending", async () => {
  const channel = new LoopbackChannel();
  const controller = createApplicationBridgeController(
    contract,
    ApplicationBridgeLive(contract, channel, {
      commandIdFactory: () => "22222222-2222-4222-8222-222222222222",
      maxPendingCommands: 1,
    }),
  );
  try {
    await controller.initialize();
    await controller.dispatch({ _tag: "Navigate", target: "Complete" });
    await controller.dispatch({ _tag: "Navigate", target: "Welcome" });
    assert.equal(channel.sentFrames, 3);
  } finally {
    await controller.dispose();
  }
});

test("protocol lifecycle acknowledgements do not have to be application receipts", async () => {
  const channel = new LoopbackChannel();
  const runtime = createApplicationBridgeRuntime(ApplicationBridgeLive(contract, channel));
  try {
    await runtime.runPromise(ApplicationBridge.pipe(Effect.flatMap((bridge) => bridge.initialize)));
    await runtime.runPromise(ApplicationBridge.pipe(Effect.flatMap((bridge) => bridge.uiReady)));
    await runtime.runPromise(ApplicationBridge.pipe(Effect.flatMap((bridge) => bridge.uiRendered)));
  } finally {
    await runtime.dispose();
  }
});

test("fault injection remains a Layer and returns typed errors", async () => {
  const mock = MockApplicationBridge({
    initialize: () => Effect.succeed({ revision: 0, view: "Welcome" }),
    dispatch: () => Effect.succeed({ _tag: "NavigationAccepted", revision: 1 } as const),
  });
  const runtime = createApplicationBridgeRuntime(TestApplicationBridge(mock, {
    rejectCommandTags: new Set(["Navigate"]),
  }));
  const exit = await runtime.runPromise(Effect.exit(ApplicationBridge.pipe(
    Effect.flatMap((bridge) => bridge.dispatch({ _tag: "Navigate", target: "Complete" })),
  )));
  assert.equal(exit._tag, "Failure");
  await runtime.dispose();
});

test("a sequence gap rejects speculative state until authoritative reconnect", async () => {
  const channel = new LoopbackChannel();
  const diagnostics: string[] = [];
  const controller = createApplicationBridgeController(
    contract,
    ApplicationBridgeLive(contract, channel, {
      onDiagnostic: (diagnostic) => diagnostics.push(`${diagnostic.code}:${diagnostic.connectionEpoch}`),
    }),
  );
  try {
    assert.equal((await controller.initialize()).view, "Welcome");
    let resolveRecoveredEvent: ((event: { _tag: string; revision: number; view: string }) => void) | undefined;
    const recoveredEvent = new Promise<{ _tag: string; revision: number; view: string }>((resolve) => {
      resolveRecoveredEvent = resolve;
    });
    const failure = new Promise<string>((resolve) => {
      controller.subscribe(
        (event) => resolveRecoveredEvent?.(event),
        (error) => resolve(error._tag),
      );
    });
    await new Promise<void>((resolve) => setImmediate(resolve));
    channel.emitSequenceGap();
    assert.equal(await failure, "ProtocolDecodeError");
    assert.deepEqual(diagnostics, ["resynchronization-required:0"]);
    channel.resetPhysicalConnection();
    assert.equal((await controller.reconnect()).view, "Welcome");
    await controller.dispatch({ _tag: "Navigate", target: "Complete" });
    assert.equal((await recoveredEvent).view, "Complete");
  } finally {
    await controller.dispose();
  }
});

test("send failure disposes pending work and reconnect is single-flight", async () => {
  const channel = new LoopbackChannel();
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel));
  try {
    await controller.initialize();
    channel.failNextSend = true;
    await assert.rejects(controller.dispatch({ _tag: "Navigate", target: "Complete" }), /request could not be sent/);
    await assert.rejects(controller.dispatch({ _tag: "Navigate", target: "Complete" }), /requires authoritative recovery/);
    const sentBeforeReconnect = channel.sentFrames;
    const [first, second] = await Promise.all([controller.reconnect(), controller.reconnect()]);
    assert.deepEqual(first, second);
    assert.equal(channel.sentFrames, sentBeforeReconnect + 1);
    channel.emitLateResponse();
    await controller.dispatch({ _tag: "Navigate", target: "Complete" });
  } finally {
    await controller.dispose();
  }
});

test("runtime reconnect uses a reconnectable channel before its next epoch", async () => {
  const channel = new ReconnectableLoopbackChannel();
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel));
  try {
    await controller.initialize();
    channel.inner.failNextSend = true;
    await assert.rejects(controller.dispatch({ _tag: "Navigate", target: "Complete" }), /request could not be sent/);
    await controller.reconnect();
    assert.equal(channel.reconnectCalls, 1);
  } finally {
    await controller.dispose();
  }
});

test("runtime normalizes physical reconnect failures to typed bridge errors", async () => {
  const channel = new ReconnectableLoopbackChannel();
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel));
  try {
    await controller.initialize();
    channel.failReconnect = true;
    await assert.rejects(controller.reconnect(), /TransportUnavailable/);
  } finally {
    await controller.dispose();
  }
});

test("reconnect terminally disposes a prior-epoch pending command", async () => {
  const channel = new LoopbackChannel();
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel));
  try {
    await controller.initialize();
    channel.holdDispatch = true;
    const pending = controller.dispatch({ _tag: "Navigate", target: "Complete" });
    await new Promise<void>((resolve) => setImmediate(resolve));
    channel.holdDispatch = false;
    await controller.reconnect();
    await assert.rejects(pending, /reconnect replaced pending commands/);
  } finally {
    await controller.dispose();
  }
});

test("a receipt returned before queued staged events remains sequence-monotonic", async () => {
  const channel = new LoopbackChannel();
  channel.receiptBeforeEvent = true;
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel));
  const event = new Promise<{ _tag: string; revision: number; view: string }>((resolve, reject) => controller.subscribe(resolve, reject));
  try {
    await controller.initialize();
    assert.deepEqual(await controller.dispatch({ _tag: "Navigate", target: "Complete" }), { _tag: "NavigationAccepted", revision: 1 });
    assert.deepEqual(await event, { _tag: "NavigationChanged", revision: 1, view: "Complete" });
  } finally {
    await controller.dispose();
  }
});

test("a sequence-zero admission refusal rejects immediately without advancing browser state", async () => {
  const channel = new LoopbackChannel();
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel));
  try {
    await controller.initialize();
    channel.admissionRejectNextDispatch = true;
    await assert.rejects(controller.dispatch({ _tag: "Navigate", target: "Complete" }), /pending command limit/);
    assert.deepEqual(await controller.dispatch({ _tag: "Navigate", target: "Complete" }), { _tag: "NavigationAccepted", revision: 1 });
  } finally {
    await controller.dispose();
  }
});

test("late and unmatched sequence-zero errors cannot reject reused pending work", async () => {
  const id = "33333333-3333-4333-8333-333333333333";
  const channel = new LoopbackChannel();
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel, { commandIdFactory: () => id }));
  try {
    await controller.initialize();
    channel.resetPhysicalConnection();
    await controller.reconnect();
    channel.holdDispatch = true;
    const pending = controller.dispatch({ _tag: "Navigate", target: "Complete" });
    await new Promise<void>((resolve) => setImmediate(resolve));
    channel.emitAdmissionError(0, id);
    channel.emitAdmissionError(2, id);
    await new Promise<void>((resolve) => setImmediate(resolve));
    let settled = false;
    void pending.then(() => { settled = true; }, () => { settled = true; });
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(settled, false);
  } finally { await controller.dispose(); }
});

test("matching requested-epoch sequence-zero initialize error rejects without state advance", async () => {
  const id = "44444444-4444-4444-8444-444444444444";
  const channel = new LoopbackChannel();
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel, { commandIdFactory: () => id }));
  try {
    await controller.initialize();
    channel.resetPhysicalConnection();
    channel.holdInitialize = true;
    const reconnect = controller.reconnect();
    await new Promise<void>((resolve) => setImmediate(resolve));
    channel.emitAdmissionError(1, id);
    await assert.rejects(reconnect, /pending command limit/);
  } finally { await controller.dispose(); }
});

test("a discarded old-epoch send failure cannot tear down a completed reconnect", async () => {
  const channel = new LoopbackChannel();
  const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel));
  try {
    await controller.initialize();
    channel.deferNextSend = true;
    const obsolete = controller.dispatch({ _tag: "Navigate", target: "Complete" });
    await channel.deferredSendStarted;
    await controller.reconnect();
    channel.rejectDeferredSend?.(new Error("late old-epoch failure"));
    await assert.rejects(obsolete, /reconnect replaced pending commands/);
    await controller.dispatch({ _tag: "Navigate", target: "Complete" });
  } finally {
    await controller.dispose();
  }
});

async function semanticSuite(layer: Layer.Layer<unknown>): Promise<void> {
  const runtime = createApplicationBridgeRuntime(layer as never);
  try {
    const snapshot = await runtime.runPromise(ApplicationBridge.pipe(Effect.flatMap((bridge) => bridge.initialize)));
    assert.deepEqual(snapshot, { revision: 0, view: "Welcome" });
    const event = runtime.runPromise(ApplicationBridge.pipe(
      Effect.flatMap((bridge) => Stream.runHead(bridge.events)),
    ));
    await new Promise<void>((resolve) => setImmediate(resolve));
    const receipt = await runtime.runPromise(ApplicationBridge.pipe(
      Effect.flatMap((bridge) => bridge.dispatch({ _tag: "Navigate", target: "Complete" })),
    ));
    assert.deepEqual(receipt, { _tag: "NavigationAccepted", revision: 1 });
    assert.deepEqual((await event)._tag, "Some");
  } finally {
    await runtime.dispose();
  }
}

class LoopbackChannel implements FrameChannel {
  public state: "connected" | "closed" = "connected";
  public sentFrames = 0;
  public failNextSend = false;
  public holdDispatch = false;
  public holdInitialize = false;
  public receiptBeforeEvent = false;
  public admissionRejectNextDispatch = false;
  public deferNextSend = false;
  public deferredSendStarted: Promise<void> = Promise.resolve();
  public rejectDeferredSend: ((error: Error) => void) | undefined;
  private readonly listeners = new Set<(event: FrameChannelEvent) => void>();
  private sequence = 0;
  private revision = 0;
  private connectionEpoch = 0;
  private readonly session = "11111111-1111-4111-8111-111111111111";

  public emitSequenceGap(): void {
    this.sequence++;
    this.emit(this.envelope("event", undefined, {
      _tag: "NavigationChanged",
      revision: this.revision,
      view: "Speculative",
    }));
  }

  public resetPhysicalConnection(): void {
    this.sequence = 0;
    this.revision = 0;
  }

  public emitLateResponse(): void {
    this.emit({
      protocol: "runic.test",
      version: 1,
      contractFingerprint: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      connectionEpoch: Math.max(0, this.connectionEpoch - 1),
      kind: "receipt",
      sessionId: this.session,
      sequence: 99,
      revision: this.revision,
      commandId: "00000000-0000-4000-8000-000000000099",
      payload: { _tag: "NavigationAccepted", revision: this.revision },
    });
  }

  public emitAdmissionError(connectionEpoch: number, commandId: string): void {
    this.emit({ protocol: "runic.test", version: 1, contractFingerprint: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", connectionEpoch, kind: "error", sessionId: this.session, sequence: 0, revision: this.revision, commandId, payload: { _tag: "CommandRejected", message: "The pending command limit was exceeded.", retryable: true } });
  }

  public async send(bytes: Uint8Array): Promise<void> {
    this.sentFrames++;
    if (this.failNextSend) {
      this.failNextSend = false;
      throw new Error("injected send failure");
    }
    const request = JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>;
    const commandId = String(request.commandId);
    const kind = String(request.kind);
    const requestedEpoch = Number(request.connectionEpoch);
    if (this.deferNextSend) {
      this.deferNextSend = false;
      let started!: () => void;
      this.deferredSendStarted = new Promise<void>((resolve) => { started = resolve; });
      const deferred = new Promise<void>((_, reject) => { this.rejectDeferredSend = reject; });
      started();
      await deferred;
    }
    if (requestedEpoch > this.connectionEpoch && kind === "initialize") this.sequence = 0;
    this.connectionEpoch = requestedEpoch;
    let response: Record<string, unknown>;
    if (kind === "initialize") {
      response = this.envelope("snapshot", commandId, { revision: 0, view: "Welcome" });
    } else if (kind === "dispatch") {
      if (this.admissionRejectNextDispatch) {
        this.admissionRejectNextDispatch = false;
        queueMicrotask(() => this.emit({
          protocol: "runic.test", version: 1,
          contractFingerprint: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          connectionEpoch: this.connectionEpoch, kind: "error", sessionId: this.session,
          sequence: 0, revision: this.revision, commandId,
          payload: { _tag: "CommandRejected", message: "The pending command limit was exceeded.", retryable: true },
        }));
        return;
      }
      this.revision++;
      if (this.receiptBeforeEvent) {
        response = this.envelope("receipt", commandId, { _tag: "NavigationAccepted", revision: this.revision });
        const event = this.envelope("event", undefined, { _tag: "NavigationChanged", revision: this.revision, view: "Complete" });
        if (!this.holdDispatch) queueMicrotask(() => { this.emit(response); this.emit(event); });
        return;
      }
      this.emit(this.envelope("event", undefined, { _tag: "NavigationChanged", revision: this.revision, view: "Complete" }));
      response = this.envelope("receipt", commandId, { _tag: "NavigationAccepted", revision: this.revision });
    } else if (kind === "uiReady") {
      response = this.envelope("receipt", commandId, { _tag: "UiReadyAccepted" });
    } else if (kind === "uiRendered") {
      response = this.envelope("receipt", commandId, { _tag: "UiRenderedAccepted" });
    } else {
      response = this.envelope("receipt", commandId, { _tag: "NavigationAccepted", revision: this.revision });
    }
    if ((kind === "dispatch" && this.holdDispatch) || (kind === "initialize" && this.holdInitialize)) return;
    queueMicrotask(() => this.emit(response));
  }

  public subscribe(listener: (event: FrameChannelEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  public async close(): Promise<void> {
    this.state = "closed";
  }

  private envelope(kind: string, commandId: string | undefined, payload: unknown): Record<string, unknown> {
    return {
      protocol: "runic.test",
      version: 1,
      contractFingerprint: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      connectionEpoch: this.connectionEpoch,
      kind,
      sessionId: this.session,
      sequence: ++this.sequence,
      revision: this.revision,
      ...(commandId === undefined ? {} : { commandId }),
      payload,
    };
  }

  private emit(message: Record<string, unknown>): void {
    const bytes = new TextEncoder().encode(JSON.stringify(message));
    for (const listener of this.listeners) listener({ _tag: "Frame", bytes });
  }
}

class ReconnectableLoopbackChannel implements FrameChannel {
  public readonly inner = new LoopbackChannel();
  public reconnectCalls = 0;
  public failReconnect = false;
  public get state(): FrameChannel["state"] { return this.inner.state; }
  public send(bytes: Uint8Array): Promise<void> { return this.inner.send(bytes); }
  public subscribe(listener: (event: FrameChannelEvent) => void): () => void { return this.inner.subscribe(listener); }
  public close(reason: string): Promise<void> { return this.inner.close(reason); }
  public async reconnect(): Promise<void> {
    this.reconnectCalls++;
    if (this.failReconnect) throw new Error("physical socket failure");
    this.inner.resetPhysicalConnection();
  }
}

class InitiallyDisconnectedLoopbackChannel implements FrameChannel {
  public readonly inner = new LoopbackChannel();
  public reconnectCalls = 0;
  private currentState: FrameChannel["state"] = "disconnected";
  public get state(): FrameChannel["state"] { return this.currentState; }
  public send(bytes: Uint8Array): Promise<void> { return this.inner.send(bytes); }
  public subscribe(listener: (event: FrameChannelEvent) => void): () => void { return this.inner.subscribe(listener); }
  public async close(reason: string): Promise<void> {
    this.currentState = "closed";
    await this.inner.close(reason);
  }
  public async reconnect(): Promise<void> {
    this.reconnectCalls++;
    this.currentState = "connected";
    this.inner.resetPhysicalConnection();
  }
}

class FakeWebSocket implements WebSocketFrameSocket {
  public readyState: number;
  public constructor(readyState = 1) { this.readyState = readyState; }
  public binaryType: BinaryType = "blob";
  public readonly sent: Uint8Array[] = [];
  public closeCode: number | undefined;
  private readonly listeners = new Map<string, Set<(event: Event | MessageEvent<unknown>) => void>>();

  public send(data: ArrayBufferView): void {
    this.sent.push(new Uint8Array(data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength)));
  }

  public close(code?: number): void {
    this.closeCode = code;
    this.readyState = 3;
    this.emit("close", new Event("close"));
  }

  public open(): void {
    this.readyState = 1;
    this.emit("open", new Event("open"));
  }

  public addEventListener(type: "open" | "error" | "close", listener: EventListener): void;
  public addEventListener(type: "message", listener: (event: MessageEvent<unknown>) => void): void;
  public addEventListener(type: "open" | "error" | "close" | "message", listener: EventListener | ((event: MessageEvent<unknown>) => void)): void {
    const set = this.listeners.get(type) ?? new Set();
    set.add(listener as (event: Event | MessageEvent<unknown>) => void);
    this.listeners.set(type, set);
  }

  public removeEventListener(type: "open" | "error" | "close", listener: EventListener): void;
  public removeEventListener(type: "message", listener: (event: MessageEvent<unknown>) => void): void;
  public removeEventListener(type: "open" | "error" | "close" | "message", listener: EventListener | ((event: MessageEvent<unknown>) => void)): void {
    this.listeners.get(type)?.delete(listener as (event: Event | MessageEvent<unknown>) => void);
  }

  public message(data: unknown): void { this.emit("message", new MessageEvent("message", { data })); }
  private emit(type: string, event: Event | MessageEvent<unknown>): void {
    for (const listener of this.listeners.get(type) ?? []) listener(event);
  }
}

class ReturnedBatchChannel implements FrameChannel {
  public state: FrameChannel["state"] = "connected";
  private readonly listeners = new Set<(event: FrameChannelEvent) => void>();
  private sequence = 0;
  private revision = 0;
  private connectionEpoch = 0;
  private readonly sessionId = "11111111-1111-4111-8111-111111111111";
  private readonly eventCount: number;

  public constructor(eventCount = 1) {
    this.eventCount = eventCount;
  }

  public async send(bytes: Uint8Array): Promise<void> {
    const request = JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>;
    this.connectionEpoch = Number(request.connectionEpoch);
    const commandId = String(request.commandId);
    const frames = request.kind === "initialize"
      ? [this.envelope("snapshot", commandId, { revision: 0, view: "Welcome" })]
      : this.dispatchFrames(commandId);
    queueMicrotask(() => {
      const owned = new TextEncoder().encode(JSON.stringify(frames));
      for (const listener of this.listeners) listener({ _tag: "Frame", bytes: owned });
    });
  }

  public subscribe(listener: (event: FrameChannelEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  public async close(): Promise<void> {
    this.state = "closed";
    this.listeners.clear();
  }

  private dispatchFrames(commandId: string): Record<string, unknown>[] {
    this.revision++;
    const events = Array.from({ length: this.eventCount }, () =>
      this.envelope("event", commandId, {
        _tag: "NavigationChanged",
        revision: this.revision,
        view: "Complete",
      }));
    return [
      ...events,
      this.envelope("receipt", commandId, { _tag: "NavigationAccepted", revision: this.revision }),
    ];
  }

  private envelope(kind: string, commandId: string, payload: unknown): Record<string, unknown> {
    return {
      protocol: "runic.test",
      version: 1,
      contractFingerprint: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      connectionEpoch: this.connectionEpoch,
      kind,
      sessionId: this.sessionId,
      sequence: ++this.sequence,
      revision: this.revision,
      commandId,
      payload,
    };
  }
}
