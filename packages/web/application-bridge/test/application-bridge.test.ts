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
  defineApplicationContract,
  type FrameChannel,
  type FrameChannelEvent,
} from "../dist/esm/index.js";

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
    const failure = new Promise<string>((resolve) => {
      const unsubscribe = controller.subscribe(
        () => undefined,
        (error) => { unsubscribe(); resolve(error._tag); },
      );
    });
    await new Promise<void>((resolve) => setImmediate(resolve));
    channel.emitSequenceGap();
    assert.equal(await failure, "ProtocolDecodeError");
    channel.resetPhysicalConnection();
    assert.equal((await controller.reconnect()).view, "Welcome");
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
