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
  readonly frames: readonly unknown[];
  readonly expected: unknown;
  readonly dropped?: readonly number[];
  readonly recoveries?: number;
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

/** Connects a client whose snapshot route answers `initial` first and `recovery` afterwards. */
async function connectRows(initial: unknown, recovery: unknown) {
  delete host[Symbol.for("runic.views.generated-client-runtime")];
  const bridge = installMockBridge(createMockBridge());
  let reads = 0;
  bridge.route("rowsSnapshot", () =>
    JSON.stringify({ ok: true, state: reads++ === 0 ? initial : recovery, error: null, protocol: 1 }));
  const client = await connectView({ contract: "Tests.CollectionDeltaViewModel:fixtures", route: "rows", mount: false, collections, hydrate });
  const push = host["__rowsChanged"] as (wire: unknown) => void;
  return { client, push, recoveries: () => reads - 1 };
}

const fixtures = readdirSync(directory).filter(name => name.endsWith(".json")).sort();

test("every frame kind and recovery path has a fixture", () => {
  assert.deepEqual(fixtures, ["add.json", "batch.json", "move.json", "overflow.json", "recovery.json", "remove.json", "replace.json"]);
});

for (const file of fixtures) {
  const fixture = JSON.parse(readFileSync(new URL(file, directory), "utf8")) as { readonly cases: readonly FixtureCase[] };
  for (const testCase of fixture.cases) {
    test(`${file}: ${testCase.name}`, async () => {
      const expected = expand(testCase.expected);
      const { client, push, recoveries } = await connectRows(expand(testCase.initial), expected);
      for (const [index, frame] of testCase.frames.entries()) {
        if (testCase.dropped?.includes(index)) continue;
        const wire = expand(frame);
        // A repeated frame is a duplicate and must not change the result.
        push(wire); push(wire);
      }
      await new Promise(resolve => setTimeout(resolve, 0));
      assert.equal(recoveries(), testCase.recoveries ?? 0);
      assert.deepEqual(client.snapshot, hydrate(expected as Wire));
      client.dispose();
    });
  }
}

test("a frame above 4096 changes is rejected and recovered with one snapshot read", async () => {
  const fixture = JSON.parse(readFileSync(new URL("overflow.json", directory), "utf8")) as { readonly cases: readonly FixtureCase[] };
  const atCap = fixture.cases[0]!;
  const frame = expand(atCap.frames[0]) as { changes: unknown[] };
  frame.changes.push(...(expand([{ $adds: [4096, 1] }]) as unknown[]));
  const recovery = expand({ revision: 4096, rows: [{ $rows: [0, 4097] }], title: "rows" });
  const { client, push, recoveries } = await connectRows(expand(atCap.initial), recovery);
  push(frame);
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(recoveries(), 1);
  assert.equal(client.snapshot.rows.length, 4097);
  client.dispose();
});
