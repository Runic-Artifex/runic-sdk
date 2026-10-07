// Drives the mock's collection edits through the shared collection delta fixtures in
// specs/application/fixtures/collection-deltas: the mock must push the frames the .NET
// producer pushes for the same edits. Cases about delivery, recovery and invalid keys
// exercise the producer's queue and key checks, which the mock does not model; its edits
// reject invalid keys with a RangeError instead.
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import { createMockBridge, installMockBridge, mockFailure, type MockView } from "../dist/mock.js";

type Row = { readonly id: number; readonly label: string };
type Mutation =
  | { readonly op: "insert"; readonly index: number; readonly item: Row }
  | { readonly op: "removeAt"; readonly index: number }
  | { readonly op: "set"; readonly index: number; readonly item: Row }
  | { readonly op: "move"; readonly oldIndex: number; readonly newIndex: number }
  | { readonly op: "appendRows"; readonly start: number; readonly count: number; readonly width?: number };
type FixtureCase = {
  readonly name: string;
  readonly initial: { readonly revision: number };
  readonly steps: readonly ({ readonly batch: readonly Mutation[] } | { readonly each: readonly Mutation[] })[];
  readonly frames?: readonly unknown[];
  readonly expected?: unknown;
  readonly error?: string;
  readonly delivery?: string;
  readonly dropped?: readonly number[];
};

const directory = new URL("../../../../specs/application/fixtures/collection-deltas/", import.meta.url);
const row = (id: number, width: number): Row => ({ id, label: `row ${id}`.padEnd(width, ".") });
const range = ([start, count, width = 0]: readonly number[]) => Array.from({ length: count! }, (_, offset) => row(start! + offset, width));

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

function apply(view: MockView, mutation: Mutation): void {
  const rows = view.collection("rows");
  const keyAt = (index: number) => rows.keys[index]!;
  switch (mutation.op) {
    case "insert": rows.add(mutation.item, mutation.index); break;
    case "removeAt": rows.remove(keyAt(mutation.index)); break;
    case "set": rows.replace(keyAt(mutation.index), mutation.item); break;
    case "move": rows.move(keyAt(mutation.oldIndex), mutation.newIndex); break;
    case "appendRows": for (const added of range([mutation.start, mutation.count, mutation.width ?? 0])) rows.add(added); break;
  }
}

// The mock numbers revisions across the Bridge; compare them relative to the initial state.
function relative(frame: unknown, offset: number): unknown {
  const value = { ...(frame as Record<string, unknown>) };
  for (const key of ["revision", "baseRevision"]) if (typeof value[key] === "number") value[key] = (value[key] as number) - offset;
  return value;
}

for (const file of readdirSync(directory).filter(name => name.endsWith(".json")).sort()) {
  const fixture = JSON.parse(readFileSync(new URL(file, directory), "utf8")) as { readonly cases: readonly FixtureCase[] };
  for (const testCase of fixture.cases) {
    if (testCase.error !== undefined || testCase.delivery !== undefined || testCase.dropped !== undefined) continue;
    test(`mock ${file}: ${testCase.name}`, () => {
      const bridge = installMockBridge(createMockBridge());
      const initial = expand(testCase.initial) as Record<string, unknown>;
      const { revision: _revision, ...state } = initial;
      const view = bridge.view("rows", { state, collections: { rows: item => String((item as Row).id) } });
      const offset = view.revision - (initial["revision"] as number);
      const frames: unknown[] = [];
      (globalThis as unknown as Record<string, unknown>)["__rowsChanged"] = (frame: unknown) => { frames.push(relative(frame, offset)); };
      for (const step of testCase.steps) {
        if ("batch" in step) view.batch(() => { for (const mutation of step.batch) apply(view, mutation); });
        else for (const mutation of step.each) apply(view, mutation);
      }
      assert.equal(JSON.stringify(frames), JSON.stringify(expand(testCase.frames)));
      assert.deepEqual({ ...view.state, revision: view.revision - offset }, expand(testCase.expected));
      delete (globalThis as unknown as Record<string, unknown>)["__rowsChanged"];
    });
  }
}

// specs/application/fixtures/domain-failures: the mock writes the declared-failure
// wire the .NET producer writes, for a command reply, an operation and a stream.
test("the mock answers declared failures with the .NET wire", async () => {
  const fixtures = new URL("../../../../specs/application/fixtures/domain-failures/", import.meta.url);
  const wire = (name: string) => (JSON.parse(readFileSync(new URL(`${name}.json`, fixtures), "utf8")) as { wire: Record<string, unknown> }).wire;
  const failure = { $case: "titleTaken", existingTitle: "Todo" };
  delete (globalThis as unknown as Record<symbol, unknown>)[Symbol.for("runic.views.generated-client-runtime")];
  const bridge = installMockBridge(createMockBridge());
  bridge.view("editor", {
    state: {},
    routes: { Submit: () => { throw mockFailure(failure); } },
    operations: { Save: () => { throw mockFailure(failure); }, Publish: () => { throw mockFailure(failure); } },
    streams: ["Publish"],
  });
  const reply = JSON.parse(await bridge.call("editorSubmit")) as Record<string, unknown>;
  assert.deepEqual({ ...reply, state: {} }, wire("command-reply"));
  for (const [member, fixture] of [["Save", "operation-status"], ["Publish", "stream-status"]] as const) {
    await bridge.call(`editorStart${member}`, "save-1-" + member);
    const identity = JSON.stringify({ contract: "Tests.Editor:fixture:editor", member, requestId: "save-1-" + member });
    const status = JSON.parse(await bridge.call("__runicOperationWait", identity)) as Record<string, unknown>;
    assert.deepEqual({ ...status, requestId: "save-1" }, wire(fixture), fixture);
  }
});
