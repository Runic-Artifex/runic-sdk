// Checks the mock producer against Views protocol 4 (specs/application/README.md), driving it as a client does:
// requests through `bridge.request` and pushes through `bridge.onMessage`. Every reply and push must be a valid
// message for the client, replies follow the frames they include, edits and checked writes follow the producer
// rules, and values compare canonically (RFC 8785).
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import { readPush, readReply, type Push, type Reply } from "../dist/protocol.js";
import { canonicalJson } from "../dist/mock-bridge.js";
import { createMockBridge, mockFailure, type MockBridge, type MockBridgeOptions } from "../dist/mock.js";

const client = { name: "conformance", version: "1.0.0" };
let sessions = 0;

/**
 * A raw client of a mock. Each reply is read with `readReply` for its request kind and each push with `readPush`, and
 * both must be valid. At each reply carrying `revision` R for its route, the route's frames up to R must already have
 * arrived (the inclusion rule), and each frame must continue the route's previous one.
 */
class RawClient {
  readonly pushes: Push[] = [];
  /** The revision each route reached: from an `open` or `snapshot` reply, then from frames. */
  readonly revisions = new Map<string, number>();
  readonly problems: string[] = [];
  readonly stop: () => void;

  constructor(readonly bridge: MockBridge) {
    this.stop = bridge.onMessage(text => {
      const reading = readPush(text);
      if (reading.result !== "valid") { this.problems.push(`invalid push ${text}: ${reading.result === "invalid" ? reading.reason : "ignored"}`); return; }
      const push = reading.push;
      this.pushes.push(push);
      if (push.kind === "frame") {
        const reached = this.revisions.get(push.route);
        if (reached !== undefined && push.baseRevision !== reached)
          this.problems.push(`the frame ${push.baseRevision} → ${push.revision} of ${push.route} does not continue revision ${reached}`);
        if (push.revision <= push.baseRevision) this.problems.push(`the frame of ${push.route} does not increase its revision`);
        this.revisions.set(push.route, push.revision);
      }
      if (push.kind === "operation" && push.status["revision"] !== undefined) {
        // A terminal status follows the frame at its revision.
        const reached = this.revisions.get(push.route) ?? -1;
        if (reached < (push.status["revision"] as number))
          this.problems.push(`the operation status at revision ${String(push.status["revision"])} arrived before the route reached it (${reached})`);
      }
    });
  }

  async send(message: Record<string, unknown>): Promise<Reply> {
    const kind = message["kind"] as string;
    const text = await this.bridge.request(JSON.stringify(message));
    const reply = readReply(kind, text);
    assert.ok(reply, `The reply to ${kind} is not valid for the client: ${text}`);
    const route = message["route"];
    if (typeof route === "string" && typeof reply.revision === "number") {
      if ((kind === "open" || kind === "snapshot") && reply.ok) {
        if (!this.revisions.has(route) || this.revisions.get(route)! < reply.revision) this.revisions.set(route, reply.revision);
      } else if (this.revisions.has(route) && this.revisions.get(route)! < reply.revision)
        this.problems.push(`the ${kind} reply at revision ${reply.revision} of ${route} arrived before its frames (${this.revisions.get(route)})`);
    }
    return reply;
  }

  hello(session = `session-${++sessions}`): Promise<Reply> {
    return this.send({ kind: "hello", protocol: 4, session, client });
  }

  frames(route: string): Extract<Push, { kind: "frame" }>[] {
    return this.pushes.filter((push): push is Extract<Push, { kind: "frame" }> => push.kind === "frame" && push.route === route);
  }

  done(): void {
    this.stop();
    assert.deepEqual(this.problems, []);
  }
}

async function connected(options: MockBridgeOptions = {}, define?: (bridge: MockBridge) => void) {
  const bridge = createMockBridge(options);
  define?.(bridge);
  const raw = new RawClient(bridge);
  return { bridge, raw };
}

const errorOf = (reply: Reply) => ({ kind: reply.error?.kind, reason: reply.error?.reason });

/* ------------------------------------------------------------------ canonical form */

test("canonicalJson writes the canonical form of the corpus's canonical vectors", () => {
  const directory = new URL("../../../../specs/application/conformance/vectors/canonical/", import.meta.url);
  let checked = 0;
  for (const file of readdirSync(directory).filter(name => name.endsWith(".json"))) {
    const vectors = JSON.parse(readFileSync(new URL(file, directory), "utf8")) as {
      cases: { id: string; input: { encoding: string; value: string }; expect: { output?: { value: string } } }[];
    };
    for (const vector of vectors.cases) {
      // Errors are the reader's (duplicate members, lone surrogates, depth): JSON.parse cannot report them.
      if (!vector.expect.output || vector.input.encoding !== "utf8") continue;
      assert.equal(canonicalJson(JSON.parse(vector.input.value)), vector.expect.output.value, vector.id);
      checked++;
    }
  }
  assert.ok(checked >= 40, `only ${checked} vectors were checked`);
});

test("canonicalJson sorts integer-like member names by UTF-16 code units", () => {
  assert.equal(canonicalJson({ "10": 1, "1": 2, "": 3 }), "{\"\":3,\"1\":2,\"10\":1}");
  assert.equal(canonicalJson({ b: { "2": [1.0, -0, 1e21], "10": null }, a: "x" }), "{\"a\":\"x\",\"b\":{\"10\":null,\"2\":[1,0,1e+21]}}");
  assert.equal(canonicalJson({ "9": 1, "10": 2, a: 3, "01": 4 }), "{\"01\":4,\"10\":2,\"9\":1,\"a\":3}");
});

/* ------------------------------------------------------------------ handshake */

test("hello: a request before hello is no-hello, and the connection gets no pushes until it succeeds", async () => {
  const { bridge, raw } = await connected();
  const view = bridge.view("notes", { state: { title: "a" } });
  assert.deepEqual(errorOf(await raw.send({ kind: "snapshot", route: "notes" })), { kind: "rejected", reason: "no-hello" });
  view.update({ title: "b" });
  assert.equal(raw.pushes.length, 0);
  const hello = await raw.hello();
  assert.equal(hello.ok, true);
  assert.equal(hello["protocol"], 4);
  assert.equal(typeof (hello["implementation"] as { name: unknown }).name, "string");
  view.update({ title: "c" });
  assert.equal(raw.pushes.length, 1);
  raw.done();
});

test("hello: another protocol is refused, and every hello reply carries the producer's protocol and implementation", async () => {
  const { raw } = await connected();
  const mismatch = await raw.send({ kind: "hello", protocol: 3, session: "s-1", client });
  assert.equal(mismatch.ok, false);
  assert.deepEqual(errorOf(mismatch), { kind: "failed", reason: "protocol-mismatch" });
  assert.equal(mismatch["protocol"], 4);
  assert.ok(mismatch["implementation"]);
  // A refused hello does not complete the handshake.
  assert.equal((await raw.hello("s-2")).ok, true);
  const second = await raw.hello("s-3");
  assert.deepEqual(errorOf(second), { kind: "rejected", reason: "invalid-request" });
  assert.equal(second["protocol"], 4);
  assert.ok(second["implementation"]);
  const missing = await raw.send({ kind: "hello", protocol: 4, client });
  assert.deepEqual(errorOf(missing), { kind: "rejected", reason: "invalid-request" });
  raw.done();
});

test("hello without a valid client is rejected with invalid-request", async () => {
  const { raw } = await connected();
  for (const invalid of [undefined, "conformance", { name: "", version: "1.0.0" }, { name: "conformance" }]) {
    const reply = await raw.send({ kind: "hello", protocol: 4, session: `s-${String(sessions++)}`, ...(invalid === undefined ? {} : { client: invalid }) });
    assert.deepEqual(errorOf(reply), { kind: "rejected", reason: "invalid-request" }, JSON.stringify(invalid));
  }
  raw.done();
});

test("hello: the session of a closed connection is released, and a new session works", async () => {
  const { bridge, raw } = await connected();
  assert.equal((await raw.hello("closed")).ok, true);
  bridge.disconnect();
  await assert.rejects(raw.send({ kind: "hello", protocol: 4, session: "x", client }), /disconnected/);
  bridge.reconnect();
  const reply = await raw.send({ kind: "hello", protocol: 4, session: "closed", client });
  assert.deepEqual(errorOf(reply), { kind: "released", reason: undefined });
  assert.equal(reply["protocol"], 4);
  // The new connection still needs a hello.
  assert.deepEqual(errorOf(await raw.send({ kind: "snapshot", route: "notes" })), { kind: "rejected", reason: "no-hello" });
  assert.equal((await raw.hello("open")).ok, true);
  raw.done();
});

/* ------------------------------------------------------------------ replies and pushes */

function notes(bridge: MockBridge) {
  return bridge.view("notes", {
    state: { title: "a", note: "", count: 0, rows: [{ id: 1, label: "one" }], canSave: true, isImportExecuting: false },
    checkedFields: ["note"],
    collections: { rows: item => String((item as { id: number }).id) },
    set: { title: (_state, value) => value === "" ? (() => { throw Object.assign(new Error("A title is required."), { kind: "rejected", reason: "invalid-input" }); })() : undefined },
    commands: {
      save: state => ({ count: (state["count"] as number) + 1 }),
      fail: () => { throw mockFailure({ $case: "titleTaken", existingTitle: "Todo" }); },
      crash: () => { throw new Error("Boom."); },
      import: () => undefined,
    },
    operations: { import: "manual", publish: () => { throw mockFailure({ $case: "offline" }); } },
    streams: ["publish"],
    contract: "Tests.Notes:fp-1",
  });
}

test("every reply and push of the mock is a valid message, and replies follow the frames they include", async () => {
  const { bridge, raw } = await connected();
  const view = notes(bridge);
  bridge.view("other", { state: { value: 1 } });
  await raw.hello();
  const open = await raw.send({ kind: "open", route: "notes", presentation: "p-1", handlers: [{ name: "confirm", contract: "Tests.Notes:confirm" }] });
  assert.deepEqual([open["presentation"], open["registration"], open["fields"]], ["ok", "ok", { note: { version: 0 } }]);
  assert.equal((await raw.send({ kind: "open", route: "notes", presentation: "p-1" }))["presentation"], "ok");
  await raw.send({ kind: "snapshot", route: "other" });

  const set = await raw.send({ kind: "set", route: "notes", member: "title", value: "b", edit: 1 });
  assert.equal(set.revision, raw.revisions.get("notes"));
  const rejected = await raw.send({ kind: "set", route: "notes", member: "title", value: "", edit: 2 });
  assert.deepEqual(errorOf(rejected), { kind: "rejected", reason: "invalid-input" });
  const command = await raw.send({ kind: "command", route: "notes", member: "save" });
  assert.equal(raw.frames("notes").at(-1)!.changes[0]!["field"], "count");
  assert.equal(command.revision, raw.revisions.get("notes"));
  // A command that changes nothing replies with the route's last revision.
  assert.equal((await raw.send({ kind: "command", route: "notes", member: "import" })).revision, command.revision);
  assert.deepEqual((await raw.send({ kind: "can", route: "notes", member: "save" }))["value"], true);
  const declared = await raw.send({ kind: "command", route: "notes", member: "fail" });
  assert.deepEqual([declared.revision, declared.error?.kind, declared.error?.failure, declared.error?.detail],
    [command.revision, "domain-failed", { $case: "titleTaken", existingTitle: "Todo" }, undefined]);
  const crashed = await raw.send({ kind: "command", route: "notes", member: "crash" });
  assert.equal(crashed.error?.kind, "failed");
  assert.equal(crashed.revision, command.revision);

  const write = await raw.send({ kind: "write", route: "notes", member: "note", requestId: "w-1", expectedVersion: 0, expectedValue: "", value: "n" });
  assert.deepEqual(write["receipt"], { kind: "applied", snapshot: { value: "n", version: 1 } });
  const versioned = raw.frames("notes").at(-1)!.changes.find(change => change.field === "note");
  assert.deepEqual(versioned, { field: "note", kind: "set", value: "n", version: 1 });

  // Operations: an accepted start publishes its execution member first; the terminal status follows its frame.
  const start = await raw.send({ kind: "start", route: "notes", member: "import", operation: "op-1", contract: "Tests.Notes:fp-1" });
  assert.deepEqual([start["admission"], start["status"]], ["accepted", { kind: "running" }]);
  assert.deepEqual(raw.frames("notes").at(-1)!.changes, [{ field: "isImportExecuting", kind: "set", value: true }]);
  const identity = { route: "notes", member: "import", operation: "op-1", contract: "Tests.Notes:fp-1" };
  assert.deepEqual((await raw.send({ kind: "watch", ...identity }))["status"], { kind: "running" });
  view.operations("import")[0]!.reportProgress(0.5);
  view.operations("import")[0]!.succeed(42);
  const statuses = raw.pushes.filter(push => push.kind === "operation").map(push => push.status);
  assert.deepEqual(statuses, [{ kind: "running", progress: 0.5 }, { kind: "succeeded", revision: raw.revisions.get("notes"), result: 42 }]);
  assert.deepEqual((await raw.send({ kind: "status", ...identity }))["status"], statuses[1]);
  assert.equal((await raw.send({ kind: "unwatch", ...identity })).ok, true);
  assert.equal((await raw.send({ kind: "cancel", ...identity }))["cancellation"], "not-running");
  assert.deepEqual((await raw.send({ kind: "status", ...identity, member: "save" }))["status"], { kind: "unknown" });
  const publish = await raw.send({ kind: "start", route: "notes", member: "publish", operation: "op-2", contract: "Tests.Notes:fp-1" });
  assert.equal(publish["admission"], "accepted");
  await new Promise(resolve => setTimeout(resolve, 0));
  const published = await raw.send({ kind: "status", route: "notes", member: "publish", operation: "op-2", contract: "Tests.Notes:fp-1" });
  assert.deepEqual(published["status"], { kind: "domain-failed", revision: raw.revisions.get("notes"), failure: { $case: "offline" }, stream: true });
  assert.deepEqual((await raw.send({ kind: "stream", route: "notes", member: "publish", operation: "op-2", contract: "Tests.Notes:fp-1", cursor: 0 }))["page"],
    { kind: "domain-failed", cursor: 0, completed: true, items: [] });

  // Interactions, collections, failures and the remaining kinds.
  const interaction = view.interact("confirm", "Sure?");
  const request = raw.pushes.at(-1)!;
  assert.equal(request.kind, "interaction");
  assert.equal((await raw.send({ kind: "interaction-reply", route: "notes", presentation: "p-1", requestId: request["requestId"], outcome: "answered", output: true }))["result"], "ok");
  assert.deepEqual(await interaction, { kind: "answered", output: true });
  assert.equal((await raw.send({ kind: "interaction-reply", route: "notes", presentation: "p-1", requestId: request["requestId"], outcome: "answered", output: true }))["result"], "stale");
  assert.equal((await raw.send({ kind: "interactions", route: "notes", presentation: "p-1", generation: 1, handlers: [] }))["registration"], "ok");
  assert.equal((await raw.send({ kind: "interactions", route: "notes", presentation: "p-1", generation: 0, handlers: [] }))["registration"], "stale");
  view.collection("rows").add({ id: 2, label: "two" });
  view.pushFailure({ message: "rows: a duplicate key." });
  assert.equal(raw.pushes.at(-1)!.kind, "failure");
  assert.deepEqual((await raw.send({ kind: "receive" }))["messages"], []);
  assert.equal((await raw.send({ kind: "close", route: "notes", presentation: "p-1" }))["presentation"], "ok");
  assert.equal((await raw.send({ kind: "close", route: "notes", presentation: "never-opened" }))["presentation"], "ok");
  raw.done();
});

test("requests the producer does not know are invalid-request, and unknown routes are released", async () => {
  const { bridge, raw } = await connected();
  notes(bridge);
  await raw.hello();
  for (const message of [
    { kind: "mount", route: "notes" },
    { kind: "set", route: "notes", member: "title", value: "b" },
    { kind: "set", route: "notes", member: "title", value: "b", edit: 0 },
    { kind: "set", route: "notes", member: "title", edit: 1 },
    { kind: "set", route: "notes", member: "nothing", value: 1, edit: 1 },
    { kind: "command", route: "notes", member: "nothing" },
    { kind: "write", route: "notes", member: "title", requestId: "w", expectedVersion: 0, expectedValue: "a", value: "b" },
    { kind: "write", route: "notes", member: "note", requestId: "w", expectedVersion: -1, expectedValue: "", value: "b" },
    { kind: "start", route: "notes", member: "import" },
    { kind: "open", route: "notes" },
    { kind: "snapshot" },
  ]) assert.deepEqual(errorOf(await raw.send(message)), { kind: "rejected", reason: "invalid-request" }, JSON.stringify(message));
  for (const message of [{ kind: "snapshot", route: "gone" }, { kind: "open", route: "gone", presentation: "p" }, { kind: "command", route: "gone", member: "save" }])
    assert.deepEqual(errorOf(await raw.send(message)), { kind: "released", reason: undefined });
  raw.done();
});

for (const [name, options] of [
  ["immediate scheduling", {}],
  ["a latency range", { latency: { min: 0, max: 8 } }],
  ["manual scheduling with a latency range", { scheduling: "manual", latency: { min: 5, max: 60 } }],
] as const satisfies readonly (readonly [string, MockBridgeOptions])[]) {
  test(`revisions follow the inclusion rule and frames continue each other with ${name}`, async () => {
    const { bridge, raw } = await connected(options);
    const view = notes(bridge);
    const other = bridge.view("other", { state: { value: 0 } });
    const settle = <T>(promise: Promise<T>) => options.scheduling === "manual"
      ? (async () => { let done = false; void promise.finally(() => { done = true; }); while (!done) await bridge.advance(10); return promise; })()
      : promise;
    await settle(raw.hello());
    await settle(raw.send({ kind: "open", route: "notes", presentation: "p-1" }));
    await settle(raw.send({ kind: "open", route: "other", presentation: "p-2" }));
    const replies: Promise<Reply>[] = [];
    for (let index = 1; index <= 10; index++) {
      view.update({ note: `n${index}` });
      other.update({ value: index });
      view.collection("rows").add({ id: index + 1, label: `row ${index + 1}` });
      replies.push(raw.send({ kind: "set", route: "notes", member: "title", value: `t${index}`, edit: index }));
      replies.push(raw.send({ kind: "command", route: "notes", member: "save" }));
    }
    const answered = await settle(Promise.all(replies));
    // Each reply's revision is a revision of its route that the frames already reached.
    const published = new Set(raw.frames("notes").map(frame => frame.revision));
    for (const reply of answered) assert.ok(published.has(reply.revision!), `revision ${reply.revision} was never published`);
    assert.equal(view.state["title"], "t10");
    // Revisions are one counter for the window: the routes' frames never share one.
    const all = raw.pushes.filter(push => push.kind === "frame").map(push => push["revision"] as number);
    assert.equal(new Set(all).size, all.length);
    assert.deepEqual(all, [...all].sort((left, right) => left - right));
    raw.done();
  });
}

test("requests that change state run one at a time, so each command sees the effects of the one before", async () => {
  const { bridge, raw } = await connected();
  const view = notes(bridge);
  await raw.hello();
  await raw.send({ kind: "open", route: "notes", presentation: "p-1" });
  const replies = await Promise.all(Array.from({ length: 5 }, () => raw.send({ kind: "command", route: "notes", member: "save" })));
  assert.equal(view.state["count"], 5);
  assert.deepEqual(replies.map(reply => reply.revision), [...new Set(replies.map(reply => reply.revision))]);
  raw.done();
});

/* ------------------------------------------------------------------ edits */

test("edits: a set whose edit number is not higher than the last admitted one is superseded", async () => {
  const { bridge, raw } = await connected();
  const view = notes(bridge);
  await raw.hello();
  await raw.send({ kind: "open", route: "notes", presentation: "p-1" });
  const applied = await raw.send({ kind: "set", route: "notes", member: "title", value: "b", edit: 3 });
  assert.deepEqual(applied, { ok: true, revision: raw.revisions.get("notes") });
  for (const edit of [3, 2, 1]) {
    const reply = await raw.send({ kind: "set", route: "notes", member: "title", value: `stale ${edit}`, edit });
    assert.deepEqual(reply, { ok: true, revision: applied.revision, superseded: true });
  }
  assert.equal(view.state["title"], "b");
  // Edit numbers are per member, and a failed set still raises its member's number.
  assert.equal((await raw.send({ kind: "set", route: "notes", member: "note", value: "x", edit: 1 }))["superseded"], undefined);
  assert.equal((await raw.send({ kind: "set", route: "notes", member: "title", value: "", edit: 4 })).ok, false);
  assert.equal((await raw.send({ kind: "set", route: "notes", member: "title", value: "c", edit: 4 }))["superseded"], true);
  assert.equal(view.state["title"], "b");
  // Edit numbers belong to a connection: a new one admits them from 1 again.
  bridge.disconnect();
  bridge.reconnect();
  await raw.hello();
  assert.equal((await raw.send({ kind: "set", route: "notes", member: "title", value: "d", edit: 1 }))["superseded"], undefined);
  assert.equal(view.state["title"], "d");
  raw.done();
});

test("reorderRequests admits requests out of order, and the stale edit answers superseded", async () => {
  const { bridge, raw } = await connected({ reorderRequests: true });
  const view = notes(bridge);
  await raw.hello();
  await raw.send({ kind: "open", route: "notes", presentation: "p-1" });
  const [older, newer] = await Promise.all([
    raw.send({ kind: "set", route: "notes", member: "title", value: "x", edit: 1 }),
    raw.send({ kind: "set", route: "notes", member: "title", value: "xy", edit: 2 }),
  ]);
  assert.equal(newer["superseded"], undefined);
  assert.equal(older["superseded"], true);
  assert.ok(older.revision! <= newer.revision!);
  assert.equal(view.state["title"], "xy");
  assert.deepEqual(raw.frames("notes").map(frame => frame.changes), [[{ field: "title", kind: "set", value: "xy" }]]);
  raw.done();
});

test("reorderRequests with a latency range admits each request when its own delay ends", async () => {
  const { bridge, raw } = await connected({ scheduling: "manual", reorderRequests: true, latency: { min: 0, max: 100 } });
  const view = notes(bridge);
  const admitted: number[] = [];
  bridge.intercept(({ kind, message }) => { if (kind === "set") admitted.push(message["edit"] as number); return undefined; });
  const hello = raw.hello();
  await bridge.advance(200);
  await hello;
  const sets = Promise.all(Array.from({ length: 20 }, (_, index) =>
    raw.send({ kind: "set", route: "notes", member: "title", value: `v${index + 1}`, edit: index + 1 })));
  await bridge.advance(200);
  const replies = await sets;
  assert.equal(admitted.length, 20);
  // An edit admitted after a higher one is superseded; the highest admitted edit's value stays.
  let highest = 0;
  for (const edit of admitted) {
    assert.equal(replies[edit - 1]!["superseded"], edit < highest ? true : undefined, `edit ${edit}`);
    highest = Math.max(highest, edit);
  }
  assert.equal(view.state["title"], "v20");
  raw.done();
});

/* ------------------------------------------------------------------ canonical comparison */

test("checked writes compare the expected value canonically and retries by request id", async () => {
  const value = { "10": 1, "1": [1.0, { b: true, a: null }], "": "x" };
  const { bridge, raw } = await connected({}, mock => mock.view("doc", { state: { body: value }, checkedFields: ["body"] }));
  await raw.hello();
  const open = await raw.send({ kind: "open", route: "doc", presentation: "p-1" });
  assert.deepEqual(open["fields"], { body: { version: 0 } });
  // The same JSON value written with other member order and number forms.
  const expectedValue = JSON.parse("{\"\":\"x\",\"1\":[1.0,{\"a\":null,\"b\":true}],\"10\":1.0}") as unknown;
  const request = { kind: "write", route: "doc", member: "body", requestId: "w-1", expectedVersion: 0, expectedValue, value: { "2": 2, "1": 1 } };
  const applied = await raw.send(request);
  assert.deepEqual(applied["receipt"], { kind: "applied", snapshot: { value: { "1": 1, "2": 2 }, version: 1 } });
  // A retry whose members are canonically equal gets the first receipt and applies nothing again.
  const retry = await raw.send({ ...request, value: JSON.parse("{\"1\":1.0,\"2\":2}") as unknown });
  assert.deepEqual(retry["receipt"], applied["receipt"]);
  assert.equal(raw.frames("doc").length, 1);
  const reused = await raw.send({ ...request, value: { "1": 1, "2": 3 } });
  assert.equal((reused["receipt"] as { kind: string }).kind, "conflict");
  const stale = await raw.send({ ...request, requestId: "w-2", expectedVersion: 1, expectedValue: { "1": 1, "2": 2.5 } });
  assert.deepEqual(stale["receipt"], { kind: "conflict", incoming: { value: { "1": 1, "2": 2 }, version: 1 }, message: "The field baseline no longer matches." });
  void bridge;
  raw.done();
});

test("an operation start with a known id is a duplicate for a canonically equal input and an identity conflict otherwise", async () => {
  const { bridge, raw } = await connected();
  const view = notes(bridge);
  await raw.hello();
  await raw.send({ kind: "open", route: "notes", presentation: "p-1" });
  const start = (input: unknown, member = "import") =>
    raw.send({ kind: "start", route: "notes", member, operation: "op-1", contract: "Tests.Notes:fp-1", input });
  assert.equal((await start({ "10": [1, 2], "1": { b: 1, a: 2 } }))["admission"], "accepted");
  const duplicate = await start(JSON.parse("{\"1\":{\"a\":2.0,\"b\":1},\"10\":[1,2.0]}"));
  assert.deepEqual([duplicate["admission"], duplicate["status"]], ["duplicate", { kind: "running" }]);
  for (const [input, member] of [[{ "10": [2, 1], "1": { a: 2, b: 1 } }, "import"], [{ "10": [1, 2], "1": { a: 2, b: 1 } }, "save"]] as const) {
    const conflict = await start(input, member);
    assert.deepEqual([conflict["admission"], conflict["reason"]], ["rejected", "identity-conflict"]);
  }
  assert.equal(view.operations().length, 1);
  // A start of a command that is unavailable is rejected and starts nothing.
  view.update({ canSave: false });
  const unavailable = await raw.send({ kind: "start", route: "notes", member: "save", operation: "op-2", contract: "Tests.Notes:fp-1" });
  assert.deepEqual([unavailable["admission"], unavailable["reason"], unavailable.revision], ["rejected", "unavailable", raw.revisions.get("notes")]);
  assert.equal(view.operations().length, 1);
  raw.done();
});
