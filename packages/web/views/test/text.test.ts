// text-splice/1 (specs/application/state-changes.md, "Text splices"): the splice helpers, setters that send splices,
// frames that apply them, the fallback to full values and the text field controller.
import assert from "node:assert/strict";
import { test } from "node:test";
import { createTextFieldController } from "../dist/index.js";
import { connectView } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";
import { createMockBridge, installMockBridge, type MockBridge, type MockBridgeOptions } from "../dist/mock.js";
import { applySplice, keptSuffix, producerSplices, singleSplice } from "../dist/text.js";

const large = Array.from({ length: 2000 }, (_, line) => `Line ${line}: the quick brown fox jumps over the lazy dog. `).join("").slice(0, 100_000);
type State = { readonly body: string; readonly title: string };
const members = { body: bridgeWire.string, title: bridgeWire.string };

function fresh(options: MockBridgeOptions = {}): MockBridge {
  return installMockBridge(createMockBridge(options));
}
async function until(condition: () => boolean, message: string): Promise<void> {
  for (let round = 0; !condition(); round++) {
    if (round > 1000) assert.fail(message);
    await new Promise(resolve => setTimeout(resolve, 2));
  }
}
function document(bridge: MockBridge, body = large) {
  return bridge.view("doc", { state: { body, title: "t" }, texts: ["body", "title"] });
}
const connect = () => connectView<State>({ contract: "Tests.Doc:fp", route: "doc", members, texts: ["body", "title"] });
const sets = (bridge: MockBridge) => bridge.calls.filter(call => call.kind === "set").map(call => call.message);
const frames = (pushes: string[]) => pushes.map(text => JSON.parse(text) as Record<string, unknown>).filter(push => push["kind"] === "frame");

test("the single splice keeps the common prefix and suffix and never splits a surrogate pair", () => {
  assert.deepEqual(singleSplice("abcdef", "abXdef"), { index: 2, delete: 1, text: "X" });
  assert.deepEqual(singleSplice("abc", "abc"), { index: 3, delete: 0, text: "" });
  assert.deepEqual(singleSplice("aaaa", "aaaaa"), { index: 4, delete: 0, text: "a" });
  assert.deepEqual(singleSplice("x\u{1F600}", "x\u{1F601}"), { index: 1, delete: 2, text: "\u{1F601}" });
  assert.deepEqual(singleSplice("\u{1F600}x", "\u{1F700}x"), { index: 0, delete: 2, text: "\u{1F700}" });
});

test("the kept suffix is the longest suffix the new value starts with", () => {
  const brute = (previous: string, value: string) => {
    if (value.startsWith(previous)) return previous.length;
    for (let kept = Math.min(previous.length - 1, value.length); kept > 0; kept--)
      if (value.startsWith(previous.slice(previous.length - kept))) return kept;
    return 0;
  };
  let seed = 7;
  const next = () => (seed = (seed * 1103515245 + 12345) % 2 ** 31) % 3;
  for (let round = 0; round < 500; round++) {
    const previous = Array.from({ length: 1 + round % 23 }, () => "ab\n"[next()]).join("");
    const value = previous.slice(next()) + Array.from({ length: next() }, () => "ab\n"[next()]).join("");
    assert.equal(keptSuffix(previous, value), brute(previous, value), JSON.stringify([previous, value]));
  }
});

test("a producer publishes one splice, the drop-and-append pair, or a set", () => {
  assert.deepEqual(producerSplices(large, large.slice(0, 500) + "X" + large.slice(500)), [{ index: 500, delete: 0, text: "X" }]);
  const log = Array.from({ length: 50 }, (_, line) => `line ${String(line).padStart(3, "0")}\n`).join("");
  assert.deepEqual(producerSplices(log, log.slice(18) + "line 050\n"), [{ index: 0, delete: 18, text: "" }, { index: log.length - 18, delete: 0, text: "line 050\n" }]);
  assert.equal(producerSplices("a".repeat(63), "a".repeat(63) + "b"), undefined);
  assert.deepEqual(producerSplices("a".repeat(64), "a".repeat(64) + "b"), [{ index: 64, delete: 0, text: "b" }]);
  assert.equal(producerSplices(large, "z".repeat(1000)), undefined);
});

test("a splice applies only to the value it names", () => {
  assert.equal(applySplice("hello", { length: 5, index: 5, delete: 0, text: "!" }), "hello!");
  assert.throws(() => applySplice("hello", { length: 4, index: 0, delete: 0, text: "" }), /length/);
  assert.throws(() => applySplice("hello", { length: 5, index: 4, delete: 2, text: "" }), /range/);
  assert.throws(() => applySplice("a\u{1F600}", { length: 3, index: 2, delete: 0, text: "x" }), /surrogate/);
  assert.throws(() => applySplice(null, { length: 0, index: 0, delete: 0, text: "" }), /string/);
});

test("setters send splices of long text members, against the revision and then against the previous edit", async () => {
  const bridge = fresh({ scheduling: "manual", latency: 10 });
  const doc = document(bridge);
  const pending = connect();
  await bridge.advance(20);
  const client = await pending;
  const first = client.set("body", large + "!");
  const second = client.set("body", large + "!?");
  // The overlay shows the latest edit at once; the requests carry one character each.
  assert.equal(client.snapshot.body, large + "!?");
  const [one, two] = sets(bridge);
  assert.deepEqual(one!["splice"], { baseRevision: 0, length: large.length, index: large.length, delete: 0, text: "!" });
  assert.deepEqual(two!["splice"], { baseEdit: 1, length: large.length + 1, index: large.length + 1, delete: 0, text: "?" });
  assert.ok(JSON.stringify(one).length < 200 && JSON.stringify(two).length < 200);
  await bridge.advance(40);
  await Promise.all([first, second]);
  assert.equal(doc.state.body, large + "!?");
  assert.equal(client.snapshot.body, large + "!?");
  // A short member, and a value that keeps little of its base, cross whole.
  void client.set("title", "tt");
  void client.set("body", "short");
  const [, , title, body] = sets(bridge);
  assert.equal(title!["value"], "tt");
  assert.equal(body!["value"], "short");
  await bridge.advance(40);
  client.dispose();
});

test("the client applies splice frames and keeps the identity of other members", async () => {
  const bridge = fresh();
  const doc = document(bridge);
  const pushes: string[] = [];
  bridge.onMessage(text => pushes.push(text));
  const client = await connect();
  const before = client.snapshot;
  doc.update({ body: large.slice(0, 10) + large.slice(13) });
  await until(() => client.snapshot.body.length === large.length - 3, "The splice frame was not applied.");
  assert.equal(client.snapshot.body, large.slice(0, 10) + large.slice(13));
  assert.equal(client.snapshot.title, before.title);
  assert.deepEqual((frames(pushes).at(-1)!["changes"] as unknown[])[0], { field: "body", kind: "splice", length: large.length, index: 10, delete: 3, text: "" });
  client.dispose();
});

test("a splice the producer refuses is sent again in full as the latest edit", async () => {
  const bridge = fresh();
  const doc = document(bridge);
  const client = await connect();
  bridge.failNext({ route: "doc", kind: "set", member: "body" }, { kind: "rejected", reason: "splice-base", message: "No base." });
  await client.set("body", large + "!");
  const [refused, full] = sets(bridge);
  assert.ok(refused!["splice"] !== undefined && full!["value"] === large + "!");
  assert.deepEqual([refused!["edit"], full!["edit"]], [1, 2]);
  assert.equal(doc.state.body, large + "!");
  client.dispose();
});

test("without text-splice/1 setters send whole values", async () => {
  const bridge = fresh();
  document(bridge);
  bridge.intercept(call => call.kind === "hello"
    ? { ok: true, protocol: 4, implementation: { name: "Runic.Application.Views", version: "0.13.0" } } : undefined);
  const client = await connect();
  await client.set("body", large + "!");
  assert.equal(sets(bridge)[0]!["value"], large + "!");
  client.dispose();
});

test("a text field sends each keystroke of a long value as a splice", async () => {
  const bridge = fresh({ scheduling: "manual", latency: 5 });
  const doc = document(bridge);
  const pending = connect();
  await bridge.advance(10);
  const client = await pending;
  const field = createTextFieldController<State, typeof client>({ read: state => state.body, write: (view, value) => view.set("body", value) });
  field.setSource({ state: client.snapshot, client });
  let typed = large;
  for (const character of "hello") {
    typed += character;
    field.input(typed);
    await bridge.advance(3);
  }
  await bridge.advance(100);
  assert.equal(doc.state.body, large + "hello");
  for (const set of sets(bridge)) {
    assert.ok(set["splice"] !== undefined, `A keystroke sent ${JSON.stringify(set).slice(0, 80)}`);
    assert.ok(JSON.stringify(set).length < 200);
  }
  field.dispose();
  client.dispose();
});
