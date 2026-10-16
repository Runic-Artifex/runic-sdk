// Applies the shared text delta fixtures in specs/application/fixtures/text-deltas.
// The .NET producer test requires byte-identical frames from the same files.
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import { connectView, defineCollections, defineText } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";

type State = { readonly log: string | null; readonly title: string };
type Wire = { readonly revision: number; readonly log: unknown; readonly title: unknown };
type FixtureCase = { readonly name: string; readonly initial: Wire; readonly frames: readonly unknown[]; readonly expected: Wire };

const directory = new URL("../../../../specs/application/fixtures/text-deltas/", import.meta.url);
const host = globalThis as unknown as Record<string | symbol, unknown>;
const hydrate = (wire: Wire): State => ({
  log: wire.log === null ? null : bridgeWire.string(wire.log),
  title: bridgeWire.string(wire.title),
});
const collections = defineCollections({ log: defineText(true) });

/** Connects a client whose snapshot route answers `initial` first, then `recovery`. */
async function connectLog(initial: unknown, recovery: unknown) {
  delete host[Symbol.for("runic.views.generated-client-runtime")];
  const bridge = installMockBridge(createMockBridge());
  let reads = 0;
  bridge.route("logSnapshot", () =>
    JSON.stringify({ ok: true, state: reads++ === 0 ? initial : recovery, error: null, protocol: 2 }));
  const client = await connectView({ contract: "Tests.TextDeltaViewModel:fixtures", route: "log", mount: false, collections, hydrate });
  const push = host["__logChanged"] as (wire: unknown) => void;
  return { client, push, recoveries: () => reads - 1 };
}

/** A minimal reading of the text change in collection-deltas.md, independent of the runtime. */
function applyReference(state: Wire, wire: unknown): Wire {
  const frame = wire as { readonly __runicDelta?: number; readonly revision: number; readonly changes?: readonly { trim: number; text: string }[] };
  if (frame.__runicDelta === undefined) return wire as Wire;
  let log = state.log as string;
  for (const change of frame.changes!) log = log.slice(change.trim) + change.text;
  return { ...state, revision: frame.revision, log };
}

const fixtures = readdirSync(directory).filter(name => name.endsWith(".json")).sort();

test("the text fixtures are present", () => {
  assert.deepEqual(fixtures, ["text.json"]);
});

for (const file of fixtures) {
  const fixture = JSON.parse(readFileSync(new URL(file, directory), "utf8")) as { readonly cases: readonly FixtureCase[] };
  for (const testCase of fixture.cases) {
    test(`${file}: ${testCase.name}`, async () => {
      let reference = testCase.initial;
      const { client, push, recoveries } = await connectLog(reference, testCase.expected);
      let notifications = 0;
      client.subscribe(() => { notifications++; });
      for (const [index, frame] of testCase.frames.entries()) {
        const before = notifications;
        // A repeated frame is a duplicate and changes nothing.
        push(frame); push(frame);
        reference = applyReference(reference, frame);
        assert.equal(notifications - before, 1, `frame ${index} was not applied exactly once`);
        assert.deepEqual(client.snapshot, hydrate(reference), `frame ${index} produced another state`);
      }
      assert.equal(recoveries(), 0);
      assert.deepEqual(reference, testCase.expected);
      client.dispose();
    });
  }
}

// A change whose length is not the client's, or that is otherwise unusable, recovers with one snapshot read.
for (const [name, change] of [
  ["a length that is not the client's", { field: "log", kind: "text", length: 2, trim: 0, text: "b" }],
  ["a trim beyond the text", { field: "log", kind: "text", length: 1, trim: 2, text: "b" }],
  ["a text that is not a string", { field: "log", kind: "text", length: 1, trim: 0, text: 1 }],
  ["a collection change for the text field", { field: "log", kind: "add", index: 0, oldIndex: -1, keys: ["a"], items: ["a"] }],
] as const) {
  test(`${name} recovers with a snapshot`, async () => {
    const recovered = { revision: 1, log: "ab", title: "log" };
    const { client, push, recoveries } = await connectLog({ revision: 0, log: "a", title: "log" }, recovered);
    const target = globalThis as { reportError?: ((error: unknown) => void) | undefined };
    const previous = target.reportError;
    const reported: unknown[] = [];
    target.reportError = error => { reported.push(error); };
    try {
      push({ __runicDelta: 1, baseRevision: 0, revision: 1, changes: [change] });
      await new Promise(resolve => setTimeout(resolve, 0));
    } finally { target.reportError = previous; }
    assert.equal(reported.length, 1);
    assert.equal(recoveries(), 1);
    assert.deepEqual(client.snapshot, hydrate(recovered));
    client.dispose();
  });
}

test("a full state with a text field that is not a string is rejected", async () => {
  await assert.rejects(connectLog({ revision: 0, log: 1, title: "log" }, undefined), /invalid state/);
});
