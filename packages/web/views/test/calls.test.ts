// The development Bridge-call channel (`runic.views.calls`, specs/application/README.md, "Shared globals"): one start and
// one end event per generated client call while a reader listens, with timing, outcome and the reply's revision, each
// call run in an async stack task; and nothing at all, including no task, without one. Drawing the calls (the
// Performance track) is the reader's: views itself makes no Performance entries.
import assert from "node:assert/strict";
import { test } from "node:test";
import { BridgeError, type BridgeCallEvent, type BridgeCallListener, type QueryCollectionState, type RunicBridgeClient } from "../dist/index.js";
import { bridgeOperations, connectView, decodeQueryCollectionState, defineQueryCollection } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, mockFailure, type MockBridge } from "../dist/mock.js";

const callsKey = Symbol.for("runic.views.calls");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const contract = "Demo.NotesViewModel:ABC";

type Row = { readonly id: number; readonly label: string };
type State = { readonly title: string; readonly count: number; readonly rows: QueryCollectionState };
const members = { title: bridgeWire.string, count: (wire: unknown) => bridgeWire.integer(wire, 0, 1000), rows: decodeQueryCollectionState };
const rows = definitionRows(20);
const definition = defineQueryCollection((wire: unknown): Row => {
  const row = bridgeWire.object(wire, value => value);
  return { id: bridgeWire.integer(row["id"], 0, 1000), label: bridgeWire.string(row["label"]) };
});

function definitionRows(count: number): Row[] {
  return Array.from({ length: count }, (_, id) => ({ id, label: `row ${id}` }));
}

async function connect(bridge: MockBridge = createMockBridge()) {
  installMockBridge(bridge);
  const view = bridge.view("notes", {
    state: { title: "a", count: 0 },
    contract,
    commands: {
      save: state => ({ count: (state["count"] as number) + 1 }),
      fail: () => { throw new Error("The save failed."); },
    },
    operations: { save: "manual" },
    checkedFields: ["title"],
    queries: { rows: { rows, key: row => String((row as Row).id) } },
  });
  const client = await connectView<State>({ contract, route: "notes", members, operations: bridgeOperations, checkedFields: ["title"] });
  return { bridge, view, client };
}

function listen(options: { requested?: boolean; devtools?: boolean } = {}) {
  const events: BridgeCallEvent[] = [];
  const listener: BridgeCallListener = event => { events.push(event); };
  const channel = { listeners: new Set([listener]), requested: options.requested ?? true, ...(options.devtools === undefined ? {} : { devtools: options.devtools }) };
  host[callsKey] = channel;
  return { events, channel, ends: () => events.filter(event => event.phase === "end") as Extract<BridgeCallEvent, { phase: "end" }>[] };
}

/** Records `performance.measure` entries and `console.createTask` calls, keeping both working. */
function spyDevtools() {
  const measures: { name: string; options: { start: number; end: number; detail: { devtools: Record<string, unknown> } } }[] = [];
  const tasks: string[] = [];
  const runs: string[] = [];
  const timing = performance as unknown as { measure: (...args: unknown[]) => unknown };
  const taskConsole = console as unknown as { createTask?: (name: string) => { run<T>(run: () => T): T } };
  const savedMeasure = timing.measure;
  const savedCreateTask = taskConsole.createTask;
  timing.measure = (name: unknown, options: unknown) => { measures.push({ name: name as string, options: options as never }); return undefined; };
  taskConsole.createTask = name => { tasks.push(name); return { run: run => { runs.push(name); return run(); } }; };
  return {
    measures, tasks, runs,
    restore() {
      timing.measure = savedMeasure;
      if (savedCreateTask === undefined) delete taskConsole.createTask; else taskConsole.createTask = savedCreateTask;
    },
  };
}

test("each call kind is reported once, with its id, timing, outcome and the reply's revision", async () => {
  const { events, ends } = listen();
  const spy = spyDevtools();
  try {
    const { bridge, view, client } = await connect();
    // Connecting is not a call.
    assert.equal(events.length, 0);

    const saving = client.command("save");
    // The start is reported synchronously, in the caller's stack.
    assert.deepEqual(events.map(event => [event.phase, event.kind, event.member]), [["start", "command", "save"]]);
    const saved = await saving;
    assert.equal(saved.count, 1);
    await client.set("title", "b");
    const baseline = client.fieldBaseline("title");
    const receipt = await client.write("title", { requestId: "w-1", expectedVersion: baseline.version, expectedValue: baseline.value, value: "c" },
      wire => bridgeWire.string(wire));
    assert.equal(receipt.kind, "applied");
    assert.equal(await client.can("save"), true);
    const handle = await client.start("save", "op-1", undefined, { decode: value => value });
    view.operations("save")[0]!.succeed();
    await handle.wait();
    const collection = client.queryCollection("rows", definition);
    await collection.setQuery({ sort: [{ field: "id", descending: true }], filters: [] });
    collection.setRange(0, 5);
    for (let round = 0; round < 50 && !collection.current.rows.every(row => row.status === "loaded"); round++) await bridge.flush();
    await assert.rejects(client.command("fail"), BridgeError);

    const finished = ends();
    assert.deepEqual(finished.map(event => [event.kind, event.request, event.member, event.outcome]), [
      ["command", "command", "save", "ok"],
      ["set", "set", "title", "ok"],
      ["write", "write", "title", "ok"],
      ["can", "can", "save", "ok"],
      ["operation", "start", "save", "ok"],
      ["query", "query-set", "rows", "ok"],
      ["query", "query-rows", "rows", "ok"],
      ["command", "command", "fail", "error"],
    ]);
    // Every start has its end, with the same identity; ids increase per channel.
    const starts = events.filter(event => event.phase === "start");
    assert.equal(starts.length, finished.length);
    assert.deepEqual(finished.map(event => event.id), [1, 2, 3, 4, 5, 6, 7, 8]);
    for (const end of finished) {
      const start = starts.find(event => event.id === end.id)!;
      assert.deepEqual({ ...start, phase: "end" }, Object.fromEntries(Object.entries(end).filter(([key]) =>
        !["end", "duration", "outcome", "revision", "reason", "error"].includes(key))));
      assert.equal(end.route, "notes");
      assert.equal(end.contract, contract);
      assert.ok(end.end >= end.start);
      assert.equal(end.duration, end.end - end.start);
    }
    // A reply that carries a revision reports it: the frames with that revision carry the call's effects.
    assert.equal(typeof finished[0]!.revision, "number");
    assert.equal(typeof finished[1]!.revision, "number");
    assert.ok(finished[1]!.revision! > finished[0]!.revision!);
    const failed = finished.at(-1)!;
    assert.ok(failed.error instanceof BridgeError);
    assert.equal(failed.reason, "failed");

    // Each call has one async stack task, named after the C# view model; views makes no Performance entries.
    assert.deepEqual(spy.measures, []);
    assert.equal(spy.tasks.length, finished.length);
    assert.deepEqual(spy.tasks.slice(0, 2), ["Runic command NotesViewModel.save", "Runic set NotesViewModel.title"]);
    // The continuation that delivers the call's state runs in its task.
    assert.deepEqual(spy.runs, spy.tasks);
    client.dispose();
  } finally {
    spy.restore();
    delete host[callsKey];
  }
});

test("without a reader nothing is recorded, measured or tagged", async () => {
  const spy = spyDevtools();
  try {
    // No channel at all.
    let { client } = await connect();
    await client.command("save");
    await client.set("title", "b");
    await assert.rejects(client.command("fail"));
    client.dispose();
    // A channel nobody requested, and a requested channel without listeners.
    const idle = listen({ requested: false });
    ({ client } = await connect());
    await client.command("save");
    client.dispose();
    assert.deepEqual(idle.events, []);
    host[callsKey] = { listeners: new Set(), requested: true };
    ({ client } = await connect());
    await client.command("save");
    client.dispose();
    assert.equal((host[callsKey] as { sequence?: number }).sequence, undefined);
    assert.deepEqual(spy.measures, []);
    assert.deepEqual(spy.tasks, []);
  } finally {
    spy.restore();
    delete host[callsKey];
  }
});

test("devtools: false reports calls without async stack tasks", async () => {
  const { ends } = listen({ devtools: false });
  const spy = spyDevtools();
  try {
    const { client } = await connect();
    await client.command("save");
    assert.equal(ends().length, 1);
    assert.deepEqual(spy.measures, []);
    assert.deepEqual(spy.tasks, []);
    client.dispose();
  } finally {
    spy.restore();
    delete host[callsKey];
  }
});

test("a released View's call and a superseded command end cancelled; a command that released its View ends ok", async () => {
  const { ends } = listen();
  try {
    const { client } = await connect();
    client.dispose();
    await assert.rejects(client.command("save"), (error: unknown) => error instanceof BridgeError && error.kind === "released");
    assert.deepEqual(ends().map(event => [event.member, event.outcome, event.reason]), [["save", "cancelled", "released"]]);

    // The producer answered `superseded`: the call resolves like one that ran, but nothing ran.
    const bridge = createMockBridge();
    const second = await connect(bridge);
    const installed = host["__runicBridge"] as RunicBridgeClient;
    const request = installed.request.bind(installed);
    let extra: Record<string, unknown> = { superseded: true };
    (installed as { request: RunicBridgeClient["request"] }).request = async text => {
      const reply = await request(text);
      return (JSON.parse(text) as { kind: string }).kind === "command" ? JSON.stringify({ ...JSON.parse(reply), ...extra }) : reply;
    };
    await second.client.command("save");
    const superseded = ends().at(-1)!;
    assert.deepEqual([superseded.outcome, superseded.reason], ["cancelled", "superseded"]);
    assert.equal(typeof superseded.revision, "number");
    // The command ran, and .NET released its View before the reply, as a navigation does.
    extra = { released: true };
    await second.client.command("save");
    const released = ends().at(-1)!;
    assert.deepEqual([released.outcome, released.reason], ["ok", "released"]);
    second.client.dispose();
  } finally { delete host[callsKey]; }
});

test("a command with a declared failure ends error with domain-failed; a throwing listener does not break the call", async () => {
  const { ends, channel } = listen();
  const errors: unknown[] = [];
  const consoleError = console.error;
  console.error = (error: unknown) => { errors.push(error); };
  channel.listeners.add(() => { throw new Error("listener"); });
  try {
    const bridge = createMockBridge();
    installMockBridge(bridge);
    bridge.view("notes", {
      state: { title: "a", count: 0 }, contract,
      commands: { save: () => { throw mockFailure({ code: "full" }); } },
    });
    const client = await connectView<Omit<State, "rows">>({ contract, route: "notes", members: { title: members.title, count: members.count } });
    const outcome = await client.commandOutcome("save", value => value);
    assert.equal(outcome.ok, false);
    assert.deepEqual(ends().map(event => [event.outcome, event.reason]), [["error", "domain-failed"]]);
    assert.equal(errors.length, 2);
    client.dispose();
  } finally {
    console.error = consoleError;
    delete host[callsKey];
  }
});
