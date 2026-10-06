// Applies the shared collection delta fixtures in specs/application/fixtures/collection-deltas.
// The .NET producer test requires byte-identical frames from the same files.
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import { bridgeWire, connectView, defineCollection } from "../dist/index.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";

type Row = { readonly id: number; readonly label: string };
type State = { readonly rows: readonly Row[]; readonly title: string };
type Wire = { readonly revision: number; readonly rows: unknown; readonly title: unknown };
type FixtureCase = {
  readonly name: string;
  readonly initial: unknown;
  readonly steps: readonly unknown[];
  readonly frames?: readonly unknown[];
  readonly expected?: unknown;
  readonly dropped?: readonly number[];
  readonly recovery?: unknown;
  readonly failedReads?: number;
  readonly recoveries?: number;
  readonly error?: string;
};

const directory = new URL("../../../../specs/application/fixtures/collection-deltas/", import.meta.url);
const host = globalThis as unknown as Record<string | symbol, unknown>;
const decode = (wire: unknown): Row => {
  const row = bridgeWire.object(wire, value => value);
  return { id: bridgeWire.integer(row["id"], 0, 1_000_000), label: bridgeWire.string(row["label"]) };
};
const hydrate = (wire: Wire): State => ({ rows: bridgeWire.array(wire.rows, decode), title: bridgeWire.string(wire.title) });
const collections = { rows: defineCollection(decode, row => String(row.id)) };

const row = (id: number, width: number): Row => ({ id, label: `row ${id}`.padEnd(width, ".") });
const range = ([start, count, width = 0]: readonly number[]) =>
  Array.from({ length: count! }, (_, offset) => row(start! + offset, width));

/** Expands the `$rows` and `$adds` generators described in collection-deltas.md. */
function expand(value: unknown): unknown {
  if (Array.isArray(value)) return value.flatMap(item => {
    const generator = item as Record<string, readonly number[]> | null;
    if (generator && typeof generator === "object" && Object.keys(generator).length === 1) {
      if (generator["$rows"]) return range(generator["$rows"]);
      if (generator["$adds"]) return range(generator["$adds"]).map(added =>
        ({ field: "rows", kind: "add", index: added.id, oldIndex: -1, keys: [String(added.id)], items: [added] }));
    }
    return [expand(item)];
  });
  if (value !== null && typeof value === "object")
    return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, expand(child)]));
  return value;
}

/** Connects a client whose snapshot route answers `initial` first, then fails `failedReads` times, then answers `recovery`. */
async function connectRows(initial: unknown, recovery: unknown, failedReads = 0) {
  delete host[Symbol.for("runic.views.generated-client-runtime")];
  const bridge = installMockBridge(createMockBridge());
  let reads = 0;
  bridge.route("rowsSnapshot", () => {
    const read = reads++;
    if (read > 0 && read <= failedReads) throw new Error("The snapshot read failed.");
    return JSON.stringify({ ok: true, state: read === 0 ? initial : recovery, error: null, protocol: 1 });
  });
  const client = await connectView({ contract: "Tests.CollectionDeltaViewModel:fixtures", route: "rows", mount: false, collections, hydrate });
  const push = host["__rowsChanged"] as (wire: unknown) => void;
  return { client, push, recoveries: () => reads - 1 };
}

/** A minimal reading of collection-deltas.md, independent of the runtime's validation. */
function applyReference(state: Wire, wire: unknown): Wire {
  const frame = wire as { readonly __runicDelta?: number; readonly revision: number; readonly changes?: readonly Change[] };
  if (frame.__runicDelta === undefined) return wire as Wire;
  const rows = [...(state.rows as unknown[])];
  for (const change of frame.changes!) {
    const count = change.keys.length;
    if (change.kind === "add") rows.splice(change.index, 0, ...change.items);
    else if (change.kind === "remove") rows.splice(change.index, count);
    else if (change.kind === "replace") rows.splice(change.index, count, ...change.items);
    else rows.splice(change.index, 0, ...rows.splice(change.oldIndex, count));
  }
  return { ...state, revision: frame.revision, rows };
}
type Change = { readonly kind: string; readonly index: number; readonly oldIndex: number; readonly keys: readonly string[]; readonly items: readonly unknown[] };

const fixtures = readdirSync(directory).filter(name => name.endsWith(".json")).sort();

test("every frame kind, recovery path and key check has a fixture", () => {
  assert.deepEqual(fixtures, ["add.json", "batch.json", "keys.json", "move.json", "overflow.json", "recovery.json", "remove.json", "replace.json"]);
});

for (const file of fixtures) {
  const fixture = JSON.parse(readFileSync(new URL(file, directory), "utf8")) as { readonly cases: readonly FixtureCase[] };
  for (const testCase of fixture.cases) {
    if (testCase.error !== undefined && testCase.steps.length === 0) {
      // The client also rejects a full state with the keys the producer refuses.
      test(`${file}: ${testCase.name}`, async () => {
        await assert.rejects(connectRows(expand(testCase.initial), undefined), /invalid state/);
      });
      continue;
    }
    test(`${file}: ${testCase.name}`, async () => {
      // A case with an error keeps its initial state.
      const expected = expand(testCase.expected ?? testCase.initial);
      let reference = expand(testCase.initial) as Wire;
      const { client, push, recoveries } = await connectRows(reference, expand(testCase.recovery ?? testCase.expected), testCase.failedReads);
      let notifications = 0;
      client.subscribe(() => { notifications++; });
      const target = globalThis as { reportError?: ((error: unknown) => void) | undefined };
      const previousReport = target.reportError;
      const reported: unknown[] = [];
      target.reportError = error => { reported.push(error); };
      try {
        const firstDropped = Math.min(...(testCase.dropped ?? [Infinity]));
        for (const [index, frame] of testCase.frames!.entries()) {
          if (testCase.dropped?.includes(index)) continue;
          const wire = expand(frame);
          const before = notifications;
          if ((wire as { __runicFailure?: number }).__runicFailure !== undefined) {
            // A failure notice is reported each time and changes nothing.
            const failures = reported.length;
            push(wire); push(wire);
            assert.equal(reported.length - failures, 2, `frame ${index} was not reported`);
            assert.match(String((reported.at(-1) as Error).message), /\[RunicCollection\] keys/);
            assert.equal(notifications, before, `frame ${index} notified subscribers`);
            assert.deepEqual(client.snapshot, hydrate(reference), `frame ${index} changed the state`);
            continue;
          }
          // A repeated frame is a duplicate and must not change the result.
          push(wire); push(wire);
          if (index > firstDropped) continue;
          // Until a frame is missed, every frame applies without recovery and
          // matches an independent reading of the frame format.
          reference = applyReference(reference, wire);
          assert.equal(recoveries(), 0, `frame ${index} needed a recovery`);
          // A repeated state or frame at the same revision does not notify again.
          assert.equal(notifications - before, 1, `frame ${index} was not applied exactly once`);
          assert.deepEqual(client.snapshot, hydrate(reference), `frame ${index} produced another state`);
        }
        if (testCase.dropped === undefined) assert.deepEqual(reference, expected);
        // A failed read is retried after a backoff of at least 250 ms.
        for (let waited = 0; recoveries() < (testCase.recoveries ?? 0) && waited < 3000; waited += 10)
          await new Promise(resolve => setTimeout(resolve, 10));
        await new Promise(resolve => setTimeout(resolve, 0));
        assert.equal(recoveries(), testCase.recoveries ?? 0);
        assert.deepEqual(client.snapshot, hydrate(expected as Wire));
      } finally { target.reportError = previousReport; }
      client.dispose();
    });
  }
}

test("a frame above 4096 changes is rejected and recovered with one snapshot read", async () => {
  const fixture = JSON.parse(readFileSync(new URL("overflow.json", directory), "utf8")) as { readonly cases: readonly FixtureCase[] };
  const atCap = fixture.cases[0]!;
  const frame = expand(atCap.frames![0]) as { changes: unknown[] };
  frame.changes.push(...(expand([{ $adds: [4096, 1] }]) as unknown[]));
  const recovery = expand({ revision: 4097, rows: [{ $rows: [0, 4097] }], title: "rows" });
  const { client, push, recoveries } = await connectRows(expand(atCap.initial), recovery);
  // The runtime reports why it discarded the frame before it recovers.
  const target = globalThis as { reportError?: ((error: unknown) => void) | undefined };
  const previous = target.reportError;
  const reported: unknown[] = [];
  target.reportError = error => { reported.push(error); };
  try {
    push(frame);
    await new Promise(resolve => setTimeout(resolve, 0));
  } finally {
    target.reportError = previous;
  }
  assert.equal(reported.length, 1);
  assert.equal(recoveries(), 1);
  assert.equal(client.snapshot.rows.length, 4097);
  client.dispose();
});
