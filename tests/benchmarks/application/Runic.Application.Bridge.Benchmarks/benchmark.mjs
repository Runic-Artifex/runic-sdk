import assert from "node:assert/strict";
import { performance } from "node:perf_hooks";
import { Schema } from "effect";
import {
  ApplicationBridgeLive,
  createApplicationBridgeController,
  defineApplicationContract,
} from "../../web/packages/application-bridge/dist/esm/index.js";

const modes = new Map([
  ["--quick", { sizes: [1, 32, 256], repetitions: 5 }],
  ["--full", { sizes: [1, 32, 256, 1_024], repetitions: 20 }],
  ["--gate", { sizes: [1, 256, 1_024], repetitions: 2 }],
]);
const selected = process.argv[2] ?? "--quick";
const mode = modes.get(selected);
if (mode === undefined || process.argv.length > 3) {
  console.error("Usage: benchmark.mjs [--quick|--full|--gate]");
  process.exit(2);
}

const Snapshot = Schema.Struct({ revision: Schema.Int, view: Schema.String });
const Command = Schema.Union(
  Schema.TaggedStruct("InitializeApplication", {}),
  Schema.TaggedStruct("Navigate", { target: Schema.String }),
);
const Receipt = Schema.TaggedStruct("NavigationAccepted", { revision: Schema.Int });
const HostEvent = Schema.TaggedStruct("NavigationChanged", { revision: Schema.Int, view: Schema.String });
const contract = defineApplicationContract({
  identity: "runic.benchmark",
  version: 1,
  fingerprint: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  command: Command,
  receipt: Receipt,
  event: HostEvent,
  snapshot: Snapshot,
  initialize: { _tag: "InitializeApplication" },
});
const decoder = new TextDecoder();

await warmUp();
console.log("scenario,batch_size,repetitions,elapsed_us,retained_heap_delta_bytes,transport_frames,validated_events");
let failed = false;
for (const size of mode.sizes) {
  const transport = await measureTransport(size, mode.repetitions);
  print(transport);
  const effect = await measureEffect(size, mode.repetitions);
  print(effect);
  if (selected === "--gate") {
    if (transport.transportFrames !== mode.repetitions || transport.validatedEvents !== size * mode.repetitions) {
      console.error(`transport-returned-batch/${size}: structural count regression`);
      failed = true;
    }
    if (effect.transportFrames !== mode.repetitions + 1 || effect.validatedEvents !== size * mode.repetitions) {
      console.error(`effect-returned-batch/${size}: structural count regression`);
      failed = true;
    }
  }
}
process.exitCode = failed ? 1 : 0;

async function warmUp() {
  await measureTransport(8, 2);
  await measureEffect(8, 2);
}

async function measureTransport(batchSize, repetitions) {
  const batch = JSON.stringify(Array.from({ length: batchSize }, (_, sequence) => ({ sequence: sequence + 1 })));
  const target = { __runicToolkit_applicationBridge_send: async () => batch };
  const channel = createReturnedFrameChannel(target.__runicToolkit_applicationBridge_send);
  let transportFrames = 0;
  let validatedEvents = 0;
  channel.subscribe((event) => {
    if (event._tag !== "Frame") return;
    transportFrames++;
    const decoded = JSON.parse(decoder.decode(event.bytes));
    assert.ok(Array.isArray(decoded));
    validatedEvents += decoded.length;
  });
  collect();
  const heapBefore = process.memoryUsage().heapUsed;
  const started = performance.now();
  for (let iteration = 0; iteration < repetitions; iteration++) {
    await channel.send(Uint8Array.of(1));
  }
  const elapsed = performance.now() - started;
  await channel.close("benchmark complete");
  collect();
  return result(
    "transport-returned-batch",
    batchSize,
    repetitions,
    elapsed,
    process.memoryUsage().heapUsed - heapBefore,
    transportFrames,
    validatedEvents,
  );
}

async function measureEffect(batchSize, repetitions) {
  let sequence = 0;
  let revision = 0;
  let transportFrames = 0;
  const sessionId = "11111111-1111-4111-8111-111111111111";
  const envelope = (kind, commandId, payload) => ({
    protocol: contract.identity,
    version: contract.version,
    contractFingerprint: contract.fingerprint,
    connectionEpoch: 0,
    kind,
    sessionId,
    sequence: ++sequence,
    revision,
    ...(commandId === undefined ? {} : { commandId }),
    payload,
  });
  const target = {
    __runicToolkit_applicationBridge_send: async (bytes) => {
      transportFrames++;
      const request = JSON.parse(decoder.decode(bytes));
      if (request.kind === "initialize") {
        return JSON.stringify([envelope("snapshot", request.commandId, { revision: 0, view: "Welcome" })]);
      }
      revision++;
      return JSON.stringify([
        ...Array.from({ length: batchSize }, () =>
          envelope("event", undefined, { _tag: "NavigationChanged", revision, view: "Complete" })),
        envelope("receipt", request.commandId, { _tag: "NavigationAccepted", revision }),
      ]);
    },
  };
  const channel = createReturnedFrameChannel(target.__runicToolkit_applicationBridge_send);
  const controller = createApplicationBridgeController(
    contract,
    ApplicationBridgeLive(contract, channel, {
      maxFrameBytes: 1_048_576,
      maxBatchFrames: 4_096,
      maxBufferedEvents: 2_048,
    }),
  );
  let validatedEvents = 0;
  controller.subscribe(() => { validatedEvents++; });
  await controller.initialize();
  collect();
  const heapBefore = process.memoryUsage().heapUsed;
  const started = performance.now();
  for (let iteration = 0; iteration < repetitions; iteration++) {
    await controller.dispatch({ _tag: "Navigate", target: "Complete" });
  }
  const expectedEvents = batchSize * repetitions;
  while (validatedEvents < expectedEvents) await new Promise((resolve) => setImmediate(resolve));
  const elapsed = performance.now() - started;
  await controller.dispose();
  collect();
  return result(
    "effect-returned-batch",
    batchSize,
    repetitions,
    elapsed,
    process.memoryUsage().heapUsed - heapBefore,
    transportFrames,
    validatedEvents,
  );
}

function result(scenario, batchSize, repetitions, elapsed, retainedHeap, transportFrames, validatedEvents) {
  return {
    scenario,
    batchSize,
    repetitions,
    elapsedMicroseconds: Math.round(elapsed * 1_000),
    retainedHeap,
    transportFrames,
    validatedEvents,
  };
}

function print(value) {
  console.log([
    value.scenario,
    value.batchSize,
    value.repetitions,
    value.elapsedMicroseconds,
    value.retainedHeap,
    value.transportFrames,
    value.validatedEvents,
  ].join(","));
}

function createReturnedFrameChannel(sendFrame) {
  const listeners = new Set();
  let state = "connected";
  return {
    get state() { return state; },
    async send(bytes) {
      if (state !== "connected") throw new Error("The Application Bridge channel is not connected.");
      const response = await sendFrame(new Uint8Array(bytes));
      if (typeof response === "string" && response.length > 0) {
        const frame = new TextEncoder().encode(response);
        queueMicrotask(() => {
          for (const listener of listeners) listener({ _tag: "Frame", bytes: frame });
        });
      }
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    async close() {
      state = "closed";
      listeners.clear();
    },
  };
}

function collect() {
  globalThis.gc?.();
}
