import assert from "node:assert/strict";
import test from "node:test";
import { Effect, Layer, Schema, Stream } from "effect";
import {
  ApplicationBridge,
  CsWebUiApplicationBridgeLive,
  MockApplicationBridge,
  TestApplicationBridge,
  createApplicationBridgeRuntime,
  createApplicationBridgeController,
  createCsWebUiFrameChannel,
  defineApplicationContract,
  type CsWebUiGlobal,
  type FrameChannel,
  type FrameChannelEvent,
} from "../dist/esm/index.js";

test("the CS-WebUI channel waits for its asynchronously installed native binding", async () => {
  const target: CsWebUiGlobal = {};
  const frames: Uint8Array[] = [];
  const channel = createCsWebUiFrameChannel(target, {
    bindingTimeoutMs: 100,
    bindingPollIntervalMs: 1,
    bindingSettleDelayMs: 1,
  });
  const send = channel.send(Uint8Array.of(1, 2, 3));

  setTimeout(() => {
    delete target.__runicToolkit_applicationBridge_receiveHostEvent;
    Object.defineProperty(target, "__runicToolkit_applicationBridge_send", {
      configurable: true,
      value: async (frame: Uint8Array) => { frames.push(frame); },
    });
  }, 5);

  await send;
  assert.deepEqual(frames, [Uint8Array.of(1, 2, 3)]);
  assert.equal(typeof target.__runicToolkit_applicationBridge_receiveHostEvent, "function");
  await channel.close("test complete");
});

test("the CS-WebUI channel reports a missing native binding after its timeout", async () => {
  const channel = createCsWebUiFrameChannel({}, {
    bindingTimeoutMs: 5,
    bindingPollIntervalMs: 1,
    bindingSettleDelayMs: 0,
  });
  await assert.rejects(
    channel.send(Uint8Array.of(1)),
    /native binding was unavailable after 5ms/,
  );
  await channel.close("test complete");
});

test("the CS-WebUI channel uses the sender installed after its settle window", async () => {
  let staleCalls = 0;
  const frames: Uint8Array[] = [];
  const target: CsWebUiGlobal = {
    __runicToolkit_applicationBridge_send: async () => {
      staleCalls++;
      throw new Error("stale bootstrap sender");
    },
  };
  const channel = createCsWebUiFrameChannel(target, {
    bindingTimeoutMs: 100,
    bindingPollIntervalMs: 1,
    bindingSettleDelayMs: 10,
  });
  const send = channel.send(Uint8Array.of(4, 5, 6));

  setTimeout(() => {
    Object.defineProperty(target, "__runicToolkit_applicationBridge_send", {
      configurable: true,
      value: async (frame: Uint8Array) => { frames.push(frame); },
    });
  }, 2);

  await send;
  assert.equal(staleCalls, 0);
  assert.deepEqual(frames, [Uint8Array.of(4, 5, 6)]);
  await channel.close("test complete");
});

test("the CS-WebUI channel restores its receiver after native binding invocation", async () => {
  const received: Uint8Array[] = [];
  const target: CsWebUiGlobal = {};
  Object.defineProperty(target, "__runicToolkit_applicationBridge_send", {
    configurable: true,
    value: async () => {
      delete target.__runicToolkit_applicationBridge_receiveHostEvent;
      await new Promise<void>((resolve) => setTimeout(resolve, 1));
      target.__runicToolkit_applicationBridge_receiveHostEvent?.(Uint8Array.of(7, 8, 9));
    },
  });
  const channel = createCsWebUiFrameChannel(target, { bindingSettleDelayMs: 0 });
  channel.subscribe((event) => {
    if (event._tag === "Frame") received.push(event.bytes);
  });

  await channel.send(Uint8Array.of(1));

  assert.deepEqual(received, [Uint8Array.of(7, 8, 9)]);
  assert.equal(typeof target.__runicToolkit_applicationBridge_receiveHostEvent, "function");
  await channel.close("test complete");
});

test("the CS-WebUI channel publishes a correlated response returned by the binding", async () => {
  const response = JSON.stringify({ kind: "snapshot" });
  const target: CsWebUiGlobal = {
    __runicToolkit_applicationBridge_send: async () => response,
  };
  const channel = createCsWebUiFrameChannel(target, { bindingSettleDelayMs: 0 });
  const received = new Promise<string>((resolve) => {
    channel.subscribe((event) => {
      if (event._tag === "Frame") resolve(new TextDecoder().decode(event.bytes));
    });
  });

  await channel.send(Uint8Array.of(1));

  assert.equal(await received, response);
  await channel.close("test complete");
});

test("the CS-WebUI channel forwards an ordered host batch as one owned frame", async () => {
  const target: CsWebUiGlobal = {
    __runicToolkit_applicationBridge_send: async () => JSON.stringify([
      { kind: "event", sequence: 1 },
      { kind: "receipt", sequence: 2 },
    ]),
  };
  const channel = createCsWebUiFrameChannel(target, { bindingSettleDelayMs: 0 });
  const received: unknown[] = [];
  channel.subscribe((event) => {
    if (event._tag === "Frame") {
      received.push(JSON.parse(new TextDecoder().decode(event.bytes)));
    }
  });

  await channel.send(Uint8Array.of(1));

  assert.deepEqual(received, [[
    { kind: "event", sequence: 1 },
    { kind: "receipt", sequence: 2 },
  ]]);
  await channel.close("test complete");
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
  await semanticSuite(CsWebUiApplicationBridgeLive(contract, new LoopbackChannel()));
});

test("one ManagedRuntime owns and disposes the bridge layer", async () => {
  const channel = new LoopbackChannel();
  const runtime = createApplicationBridgeRuntime(CsWebUiApplicationBridgeLive(contract, channel));
  const snapshot = await runtime.runPromise(ApplicationBridge.pipe(Effect.flatMap((bridge) => bridge.initialize)));
  assert.deepEqual(snapshot, { revision: 0, view: "Welcome" });
  await runtime.dispose();
  assert.equal(channel.state, "closed");
});

test("the controller composes and forks Effect programs in its owned runtime", async () => {
  const channel = new LoopbackChannel();
  const controller = createApplicationBridgeController(
    contract,
    CsWebUiApplicationBridgeLive(contract, channel),
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
  const channel = createCsWebUiFrameChannel(createReturnedBatchTarget(), { bindingSettleDelayMs: 0 });
  const controller = createApplicationBridgeController(
    contract,
    CsWebUiApplicationBridgeLive(contract, channel),
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
  const channel = createCsWebUiFrameChannel(createReturnedBatchTarget(), { bindingSettleDelayMs: 0 });
  const controller = createApplicationBridgeController(
    contract,
    CsWebUiApplicationBridgeLive(contract, channel, { maxBatchFrames: 1 }),
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
  const channel = createCsWebUiFrameChannel(createReturnedBatchTarget(4), { bindingSettleDelayMs: 0 });
  const runtime = createApplicationBridgeRuntime(
    CsWebUiApplicationBridgeLive(contract, channel, { maxBufferedEvents: 1 }),
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
    CsWebUiApplicationBridgeLive(contract, channel, {
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
  const runtime = createApplicationBridgeRuntime(CsWebUiApplicationBridgeLive(contract, channel));
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
  const controller = createApplicationBridgeController(
    contract,
    CsWebUiApplicationBridgeLive(contract, channel),
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
    channel.resetPhysicalConnection();
    assert.equal((await controller.reconnect()).view, "Welcome");
    await controller.dispatch({ _tag: "Navigate", target: "Complete" });
    assert.equal((await recoveredEvent).view, "Complete");
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
  private readonly listeners = new Set<(event: FrameChannelEvent) => void>();
  private sequence = 0;
  private revision = 0;
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

  public async send(bytes: Uint8Array): Promise<void> {
    this.sentFrames++;
    const request = JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>;
    const commandId = String(request.commandId);
    const kind = String(request.kind);
    let response: Record<string, unknown>;
    if (kind === "initialize") {
      response = this.envelope("snapshot", commandId, { revision: 0, view: "Welcome" });
    } else if (kind === "dispatch") {
      this.revision++;
      this.emit(this.envelope("event", undefined, { _tag: "NavigationChanged", revision: this.revision, view: "Complete" }));
      response = this.envelope("receipt", commandId, { _tag: "NavigationAccepted", revision: this.revision });
    } else if (kind === "uiReady") {
      response = this.envelope("receipt", commandId, { _tag: "UiReadyAccepted" });
    } else if (kind === "uiRendered") {
      response = this.envelope("receipt", commandId, { _tag: "UiRenderedAccepted" });
    } else {
      response = this.envelope("receipt", commandId, { _tag: "NavigationAccepted", revision: this.revision });
    }
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

function createReturnedBatchTarget(eventCount = 1): CsWebUiGlobal {
  let sequence = 0;
  let revision = 0;
  const sessionId = "11111111-1111-4111-8111-111111111111";
  const envelope = (kind: string, commandId: string, payload: unknown) => ({
    protocol: "runic.test",
    version: 1,
    kind,
    sessionId,
    sequence: ++sequence,
    revision,
    commandId,
    payload,
  });
  return {
    __runicToolkit_applicationBridge_send: async (bytes) => {
      const request = JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>;
      const commandId = String(request.commandId);
      if (request.kind === "initialize") {
        return JSON.stringify([envelope("snapshot", commandId, { revision: 0, view: "Welcome" })]);
      }
      revision++;
      const events = Array.from({ length: eventCount }, () =>
        envelope("event", commandId, { _tag: "NavigationChanged", revision, view: "Complete" }));
      return JSON.stringify([
        ...events,
        envelope("receipt", commandId, { _tag: "NavigationAccepted", revision }),
      ]);
    },
  };
}
