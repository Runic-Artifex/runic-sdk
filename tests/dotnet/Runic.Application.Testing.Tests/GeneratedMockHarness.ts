// Runs generated clients against their generated typed mocks: the mock codecs
// must round-trip every fixture shape, keyed collections must arrive as delta
// frames, and operations, streams and interactions must follow the protocol.
import assert from "node:assert/strict";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { BridgeError, bridgeFailure, isBridgeOutcome } from "@runic-artifex/views";
import { createMockBridge, installMockBridge, mockFailure, type MockBridge } from "@runic-artifex/views/mock";

const [generatedDirectory] = Bun.argv.slice(2);
if (!generatedDirectory) throw new Error("Usage: GeneratedMockHarness.ts <generated directory>");
const load = (name: string) => import(pathToFileURL(resolve(generatedDirectory, `${name}.ts`)).href);

function fresh(): MockBridge {
  delete (globalThis as unknown as Record<symbol, unknown>)[Symbol.for("runic.views.generated-client-runtime")];
  return installMockBridge(createMockBridge());
}

// Every scalar, DTO, union, dictionary and nullable codec round-trips through the mock.
{
  const bridge = fresh();
  const { connectDataShape } = await load("dataShape");
  const { mockDataShape } = await load("dataShape.mock");
  const item = { source: "a", ["__proto__"]: "proto", "retry-after": 5 };
  const state = {
    "exact-id": 9007199254740993n, amount: "12.50", day: "2026-10-07", when: "2026-10-07T12:00:00+00:00", duration: "1.02:03:04",
    optionalItem: null, optional: 3, items: [item], optionalItems: [null, item], groups: [{ items: [item] }], lookup: Object.assign(Object.create(null) as Record<string, typeof item>, { first: item }),
    payload: { $case: "text", text: "hello" }, money: "1.00", whole: item,
  };
  const mock = mockDataShape(bridge, { state });
  const client = await connectDataShape();
  assert.deepEqual(client.snapshot, state);
  assert.equal(client.snapshot["exact-id"], 9007199254740993n);
  await client.setPayload({ $case: "count", count: 4 });
  assert.deepEqual(mock.state.payload, { $case: "count", count: 4 });
  const baseline = client.fieldBaseline("exact-id");
  const receipt = await client.writeExactId(9007199254740995n, { requestId: "write-1", baseline });
  assert.equal(receipt.kind, "applied");
  assert.equal(client.snapshot["exact-id"], 9007199254740995n);
  const stale = await client.writeExactId(1n, { requestId: "write-2", baseline });
  assert.equal(stale.kind, "conflict");
  mock.update({ whole: { ...item, source: "updated" } });
  assert.equal(client.snapshot.whole.source, "updated");
  client.dispose();
}

// Keyed collection edits reach the client as delta frames that keep unchanged rows.
{
  const bridge = fresh();
  const { connectCollectionDelta } = await load("collectionDelta");
  const { mockCollectionDelta } = await load("collectionDelta.mock");
  const mock = mockCollectionDelta(bridge, { state: { title: "rows", rows: [{ id: 1, label: "one" }, { id: 2, label: "two" }] } });
  const client = await connectCollectionDelta();
  const first = client.snapshot.rows[0];
  const frames: unknown[] = [];
  const host = globalThis as unknown as Record<string, (frame: unknown) => void>;
  const push = host["__collectionDeltaChanged"]!;
  host["__collectionDeltaChanged"] = frame => { frames.push(frame); push(frame); };
  mock.batch(() => {
    mock.collections.rows.add({ id: 3, label: "three" }, 0);
    mock.collections.rows.replace("2", { id: 2, label: "TWO" });
  });
  mock.collections.rows.move("3", 2);
  assert.equal(frames.length, 2);
  assert.ok(frames.every(frame => (frame as { __runicDelta?: number }).__runicDelta === 1));
  assert.deepEqual(client.snapshot.rows.map((row: { id: number }) => row.id), [1, 2, 3]);
  assert.equal(client.snapshot.rows[0], first);
  assert.deepEqual(mock.collections.rows.keys, ["1", "2", "3"]);
  client.dispose();
}

// Operations run their handlers, stream values and report failures through the generated client.
{
  const bridge = fresh();
  const { connectTypedReactive } = await load("typedReactive");
  const { mockTypedReactive } = await load("typedReactive.mock");
  const request = { documentId: "doc", content: "body", expectedVersion: 1 };
  const mock = mockTypedReactive(bridge, {
    state: { saveEnabled: true, executions: 0, plainApplyCount: 0 },
    commands: { save: (state: { executions: number }) => ({ executions: state.executions + 1 }) },
    operations: {
      save: (_state: unknown, input: typeof request) => ({ result: { documentId: input.documentId, savedVersion: 2, contentLength: input.content.length } }),
      streamResult: "manual",
      fail: () => { throw new Error("The save failed."); },
    },
  });
  const client = await connectTypedReactive();
  const saved = await (await client.startSave(request)).wait();
  assert.deepEqual(saved.result, { documentId: "doc", savedVersion: 2, contentLength: 4 });
  assert.deepEqual(mock.operations.save[0].input, request);
  assert.equal((await client.save(request)).executions, 1);

  const stream = await client.startStreamResult(request);
  assert.equal(client.snapshot.isStreamResultExecuting, true);
  mock.operations.streamResult[0].emit(1, 2);
  assert.deepEqual((await stream.stream(0)).items, [{ sequence: 1, value: 1 }, { sequence: 2, value: 2 }]);
  mock.operations.streamResult[0].emit(3);
  mock.operations.streamResult[0].succeed();
  const page = await stream.stream(2);
  assert.deepEqual([page.items, page.completed], [[{ sequence: 3, value: 3 }], true]);
  assert.equal(client.snapshot.isStreamResultExecuting, false);

  const failed = await (await client.startFail(request)).wait();
  assert.equal(failed.kind, "failed");
  assert.equal(failed.error?.message, "The save failed.");

  mock.failNext("save", { kind: "rejected", message: "Not now." });
  await assert.rejects(client.save(request), (error: unknown) => error instanceof BridgeError && error.kind === "rejected");
  client.dispose();
}

// Interaction requests reach the mounted client's handler with typed input and output.
{
  const bridge = fresh();
  const { connectGeneratedInteraction } = await load("generatedInteraction");
  const { mockGeneratedInteraction } = await load("generatedInteraction.mock");
  const mock = mockGeneratedInteraction(bridge, { state: { attempt: 0, acceptedCount: 0 } });
  const client = await connectGeneratedInteraction();
  assert.deepEqual(await mock.interactions.confirm.request({ title: "Delete?", attempt: 1 }), { kind: "unhandled" });
  const stop = client.interactions.confirm.handle((input: { title: string; attempt: number }) => input.attempt > 1);
  await client.ask();
  assert.equal(mock.interactions.confirm.handled, true);
  assert.deepEqual(await mock.interactions.confirm.request({ title: "Delete?", attempt: 2 }), { kind: "answered", output: true });
  stop();
  client.dispose();
}

// A DTO interaction output round-trips through the client's encoder and the mock's decoder.
{
  const bridge = fresh();
  const { connectDtoInteraction } = await load("dtoInteraction");
  const { mockDtoInteraction } = await load("dtoInteraction.mock");
  const mock = mockDtoInteraction(bridge, { state: { picked: null } });
  const client = await connectDtoInteraction();
  const stop = client.interactions.chooseEntry.handle((input: string) => ({ name: input, count: 2 }));
  await client.pick();
  assert.deepEqual(await mock.interactions.chooseEntry.request("chosen"), { kind: "answered", output: { name: "chosen", count: 2 } });
  stop();
  client.dispose();
}

// Declared failures (W130-029) round-trip through the generated mock's failure encoders
// and the client's decoders: commands resolve a BridgeOutcome, operations outcome().
{
  const bridge = fresh();
  const { connectFailureToolkit } = await load("failureToolkit");
  const { mockFailureToolkit } = await load("failureToolkit.mock");
  const mock = mockFailureToolkit(bridge, {
    state: { title: "" },
    commands: { save: () => { throw mockFailure({ $case: "titleTaken", existingTitle: "Todo" }); } },
    operations: { publish: "manual" },
  });
  const client = await connectFailureToolkit();
  const saved = await client.save();
  assert.equal(isBridgeOutcome(saved), true);
  assert.deepEqual(saved, bridgeFailure({ $case: "titleTaken", existingTitle: "Todo" }));
  mock.failNext("reserve", { kind: "domain-failed", failure: { limit: 3, requested: 5 } });
  assert.deepEqual(await client.reserve(5), bridgeFailure({ limit: 3, requested: 5 }));
  const publish = await client.startPublish();
  mock.operations.publish[0].failWith("Archived");
  assert.deepEqual(await publish.outcome(), bridgeFailure("Archived"));
  mock.failNext("startPublish", { kind: "domain-failed", failure: "Locked" });
  assert.deepEqual(await (await client.startPublish()).outcome(), bridgeFailure("Locked"));
  // An undeclared operation still resolves without a value.
  const discard = await client.startDiscard();
  assert.equal((await discard.outcome()).ok, true);
  client.dispose();
}

console.log("GENERATED_MOCKS_OK");
