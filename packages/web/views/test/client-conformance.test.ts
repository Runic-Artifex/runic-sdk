// The client runner of the Views protocol 4 conformance corpus (specs/application/conformance/README.md): every
// scenario whose roles include `client`, against a scripted host bridge, and the client's message vectors.
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import { BridgeError, isBridgeOutcome, onBridgeConnection, onBridgeDiagnostic, type BridgeConnectionState } from "../dist/index.js";
import { bridgeOperations, connectView, defineCollection, defineCollections, defineInteractions, defineText, type ViewConnection } from "../dist/generated.js";
import * as wire from "../dist/wire.js";
import { decodeBridgeValidation } from "../dist/validation.js";
import { clock } from "../dist/clock.js";
import { readPush, readReply } from "../dist/protocol.js";
import { sharedRuntimeFor } from "../dist/runtime.js";
import type { BridgeOperationRuntimeHandle } from "../dist/operations.js";

const corpus = new URL("../../../../specs/application/conformance/", import.meta.url);
type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
type Step = Record<string, any>;
interface Scenario { readonly id: string; readonly roles: readonly string[]; readonly view: string; readonly ids: Record<string, { kind: string; value?: Json }>; readonly steps: readonly Step[] }

/* ------------------------------------------------------------------ test Views */

const int32 = (value: unknown) => wire.integer(value, -2147483648, 2147483647);
const row = (value: unknown) => wire.object(value, item => ({ id: int32(item["id"]), label: wire.string(item["label"]) }));
const rows = (value: unknown) => wire.array(value, row);
const reference = (value: unknown) => value === null ? null : wire.object(value, item => ({ kind: wire.string(item["kind"]), id: wire.string(item["id"]) }));
const rowCollections = defineCollections({ rows: defineCollection(row, item => String(item.id)) });
const failure = (value: unknown) => wire.union(value);

interface ViewModel {
  readonly members: Record<string, (value: unknown) => unknown>;
  readonly checkedFields?: readonly string[];
  /** Text members (text-splice/1). */
  readonly texts?: readonly string[];
  readonly collections?: ReturnType<typeof defineCollections>;
  readonly interactions?: ReturnType<typeof defineInteractions>;
  /** Commands that declare a failure. */
  readonly declared?: Readonly<Record<string, (value: unknown) => unknown>>;
  readonly operations?: Readonly<Record<string, { decode: (value: unknown) => unknown; decodeFailure?: (value: unknown) => unknown }>>;
  /** Scoped commands (presentation-epoch/1). */
  readonly scopedCommands?: readonly string[];
}

const models: Record<string, ViewModel> = {
  list: { members: { rows, title: wire.string }, texts: ["title"], collections: rowCollections },
  log: { members: { log: value => value === null ? null : wire.string(value), title: wire.string }, texts: ["title"],
    collections: defineCollections({ log: defineText(true) }) },
  notes: {
    members: {
      title: wire.string, note: wire.string, priority: value => wire.enumName(value, ["Low", "Normal", "High"]),
      due: value => value === null ? null : wire.localDate(value), elapsed: wire.duration, rows, count: int32, summary: wire.string,
      detail: reference, canClear: wire.boolean, canTouch: wire.boolean, canSave: wire.boolean, canConfirmClear: wire.boolean,
      isSlowImportExecuting: wire.boolean, isConfirmClearExecuting: wire.boolean,
    },
    checkedFields: ["note"],
    texts: ["title", "note", "summary"],
    collections: rowCollections,
    interactions: defineInteractions({
      confirm: { contract: "Tests.Notes:confirm", decodeInput: value => value, encodeOutput: value => value },
    }),
    declared: { save: failure, slowImport: failure },
    operations: { slowImport: { decode: int32, decodeFailure: failure } },
  },
  detail: { members: { lastAction: wire.string, canDismiss: wire.boolean }, texts: ["lastAction"] },
  form: { members: { name: wire.string, nameErrors: value => wire.array(value, wire.string), validation: decodeBridgeValidation }, texts: ["name"] },
  wizard: { members: { page: int32, canNext: wire.boolean, canBack: wire.boolean, canPing: wire.boolean }, scopedCommands: ["next", "back"] },
};
const modelOf = (route: string) => route.startsWith("content") ? models["detail"]! : models[route]!;

/* ------------------------------------------------------------------ the scripted host */

interface Connection { readonly name: string; readonly sent: { message: Json; text: string; resolve(text: string): void; reject(error: Error): void }[] }

class ScriptedHost {
  connected = false;
  connection: Connection | undefined;
  readonly messageListeners = new Set<(message: string) => void>();
  readonly connectionListeners = new Set<(connected: boolean) => void>();
  readonly bridge = {
    isConnected: () => this.connected,
    request: (text: string) => new Promise<string>((resolve, reject) => {
      if (!this.connected || !this.connection) { reject(new Error("The scripted host is disconnected.")); return; }
      this.connection.sent.push({ message: JSON.parse(text) as Json, text, resolve, reject });
    }),
    onMessage: (listener: (message: string) => void) => { this.messageListeners.add(listener); return () => { this.messageListeners.delete(listener); }; },
    onConnectionChange: (listener: (connected: boolean) => void) => {
      this.connectionListeners.add(listener);
      listener(this.connected);
      return () => { this.connectionListeners.delete(listener); };
    },
  };

  connect(name: string): void {
    this.connection = { name, sent: [] };
    this.connected = true;
    for (const listener of [...this.connectionListeners]) listener(true);
  }

  disconnect(): void {
    this.connected = false;
    for (const pending of this.connection?.sent ?? []) pending.reject(new Error("The connection closed."));
    for (const listener of [...this.connectionListeners]) listener(false);
  }

  push(text: string): void { for (const listener of [...this.messageListeners]) listener(text); }
}

/* ------------------------------------------------------------------ virtual time */

const timers = new Map<number, { due: number; callback: () => void }>();
let now = 0;
let timerId = 0;
const realClock = { set: clock.set, clear: clock.clear, now: clock.now };
function useVirtualClock(): void {
  timers.clear();
  now = 0;
  clock.set = (callback, milliseconds) => { const id = ++timerId; timers.set(id, { due: now + milliseconds, callback }); return id; };
  clock.clear = handle => { timers.delete(handle as number); };
  // The page's wall clock: the corpus's default times are on 2026-10-28 around 12:00 UTC.
  clock.now = () => Date.parse("2026-10-28T12:00:00Z") + now;
}
function useRealClock(): void { Object.assign(clock, realClock); }

const idle = async () => { for (let turn = 0; turn < 5; turn++) await new Promise(resolve => setTimeout(resolve, 0)); };

async function advance(milliseconds: number): Promise<void> {
  const target = now + milliseconds;
  for (;;) {
    await idle();
    const next = [...timers.entries()].filter(([, timer]) => timer.due <= target).sort((a, b) => a[1].due - b[1].due || a[0] - b[0])[0];
    if (!next) break;
    timers.delete(next[0]);
    now = Math.max(now, next[1].due);
    next[1].callback();
  }
  now = target;
  await idle();
}

/* ------------------------------------------------------------------ messages */

function generatedRow(id: number, width?: number): Json {
  const label = `row ${id}`;
  return { id, label: width === undefined ? label : label.padEnd(width, ".") };
}

/** Expands `$rows` and `$adds` generators in arrays. */
function expand(value: Json): Json {
  if (Array.isArray(value)) {
    const result: Json[] = [];
    for (const item of value) {
      if (item !== null && typeof item === "object" && !Array.isArray(item) && Object.keys(item).length === 1 && ("$rows" in item || "$adds" in item)) {
        const [start, count, width] = (item["$rows"] ?? item["$adds"]) as number[];
        for (let id = start!; id < start! + count!; id++)
          result.push("$rows" in item ? generatedRow(id, width) : {
            field: "rows", kind: "add", index: id, oldIndex: -1, keys: [String(id)], items: [generatedRow(id, width)],
          });
      } else result.push(expand(item));
    }
    return result;
  }
  if (value !== null && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, expand(item)]));
  return value;
}

class Ids {
  readonly bound = new Map<string, Json>();
  constructor(readonly scenario: Scenario) {}
  value(name: string): Json {
    if (!Object.hasOwn(this.scenario.ids, name)) throw new Error(`${this.scenario.id}: the id ${name} is not listed.`);
    if (this.bound.has(name)) return this.bound.get(name)!;
    const id = this.scenario.ids[name]!;
    if (id.kind === "wire") return id.value!;
    // A producer's value that this runner, playing the producer, writes first, such as an interaction's request id.
    // The corpus gives such ids no value or type; an epoch is a number, anything else a string.
    if (id.kind === "provided") {
      const value = /epoch/i.test(name) ? 1 : `provided:${name}`;
      this.bound.set(name, value);
      return value;
    }
    throw new Error(`${this.scenario.id}: the id ${name} has no value.`);
  }
}

const jsonType = (value: unknown) => value === null ? "null" : Array.isArray(value) ? "array" : typeof value;

/** Writes a message the runner sends: `$any` as its default, `$id` as its value, `$json` as raw text. */
function write(value: Json, ids: Ids): string {
  const raw: string[] = [];
  const replace = (item: Json): Json => {
    if (Array.isArray(item)) return item.map(replace);
    if (item !== null && typeof item === "object") {
      if ("$any" in item) return replace(item["default"]!);
      if ("$id" in item) return ids.value(item["$id"] as string);
      if ("$json" in item) { raw.push(item["$json"] as string); return `\u0000raw${raw.length - 1}\u0000`; }
      return Object.fromEntries(Object.entries(item).map(([key, member]) => [key, replace(member)]));
    }
    return item;
  };
  return JSON.stringify(replace(expand(value))).replace(/"\\u0000raw(\d+)\\u0000"/g, (_, index: string) => raw[Number(index)]!);
}

/** Compares a message the client sent with the expected one, binding `$id` values. Revisions are compared as values. */
function matches(actual: Json, expected: Json, ids: Ids, path = "$"): string | undefined {
  if (expected !== null && typeof expected === "object" && !Array.isArray(expected)) {
    if ("$any" in expected) return jsonType(actual) === expected["$any"] ? undefined : `${path}: expected any ${String(expected["$any"])}`;
    if ("$id" in expected) {
      const name = expected["$id"] as string;
      if (!Object.hasOwn(ids.scenario.ids, name)) return `${path}: the id ${name} is not listed`;
      if (!ids.bound.has(name)) { ids.bound.set(name, actual); return undefined; }
      return JSON.stringify(ids.bound.get(name)) === JSON.stringify(actual) ? undefined : `${path}: ${name} changed`;
    }
    if (actual === null || typeof actual !== "object" || Array.isArray(actual)) return `${path}: expected an object, got ${JSON.stringify(actual)}`;
    const keys = new Set([...Object.keys(expected), ...Object.keys(actual)]);
    for (const key of keys) {
      if (!Object.hasOwn(expected, key)) return `${path}.${key}: unexpected member ${JSON.stringify(actual[key])}`;
      if (!Object.hasOwn(actual, key)) return `${path}.${key}: missing`;
      const problem = matches(actual[key]!, expected[key]!, ids, `${path}.${key}`);
      if (problem) return problem;
    }
    return undefined;
  }
  if (Array.isArray(expected)) {
    const items = expand(expected) as Json[];
    if (!Array.isArray(actual) || actual.length !== items.length) return `${path}: expected ${items.length} items, got ${JSON.stringify(actual)}`;
    for (let index = 0; index < items.length; index++) {
      const problem = matches(actual[index]!, items[index]!, ids, `${path}[${index}]`);
      if (problem) return problem;
    }
    return undefined;
  }
  return actual === expected ? undefined : `${path}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`;
}

/* ------------------------------------------------------------------ the runner */

type CallState = { state: "pending" } | { state: "resolved"; value: unknown } | { state: "rejected"; error: unknown };

async function run(scenario: Scenario): Promise<void> {
  const host = new ScriptedHost();
  (globalThis as { __runicBridge?: unknown }).__runicBridge = host.bridge;
  const ids = new Ids(scenario);
  const diagnostics: string[] = [];
  const stopDiagnostics = onBridgeDiagnostic(diagnostic => { diagnostics.push(diagnostic.code); });
  // The runtime reports errors it cannot throw to a caller, such as a `failure` push, with `reportError`.
  const page = globalThis as { reportError?: ((error: unknown) => void) | undefined };
  const reportError = page.reportError;
  page.reportError = () => {};
  const requests = new Map<string, Connection["sent"][number]>();
  const calls = new Map<string, CallState>();
  const clients = new Map<string, ViewConnection<Record<string, unknown>>>();
  const handlers = new Map<string, Record<string, (input: never) => unknown>>();
  const notifications = new Map<string, number>();
  // The connection state the client reports (presented/1: `closed` after the producer closed the window).
  let connectionState: BridgeConnectionState | undefined;
  const stopConnection = onBridgeConnection(state => { connectionState = state; }, { lostAfter: 2_147_483_647 });
  const operations = new Map<string, { handle?: BridgeOperationRuntimeHandle<unknown, unknown>; status?: unknown; route: string; member: string }>();
  const kindOf = (value: unknown) => value instanceof BridgeError ? { kind: value.kind, ...(value.reason === undefined ? {} : { reason: value.reason }) } : { kind: "thrown" };
  const publicStatus = (status: unknown) => {
    const { contract: _, requestId: __, ...rest } = status as Record<string, unknown>;
    return rest;
  };
  const track = (id: string, work: () => Promise<unknown>) => {
    calls.set(id, { state: "pending" });
    work().then(value => calls.set(id, { state: "resolved", value }), error => calls.set(id, { state: "rejected", error }));
  };
  const client = (route: string) => {
    const found = clients.get(route);
    if (!found) throw new Error(`${scenario.id}: the route ${route} is not open.`);
    return found;
  };
  const sameDeep = (actual: unknown, expected: Json, label: string) => {
    const problem = matches(JSON.parse(JSON.stringify(actual ?? null)) as Json, expected, ids);
    assert.equal(problem, undefined, `${scenario.id}: ${label}: ${problem}`);
  };

  try {
    for (const [index, step] of scenario.steps.entries()) {
      if (step["role"] === "producer") continue;
      const at = `step ${index}`;
      if ("connect" in step) {
        host.connect(step["connect"]);
        // The client connects to the host bridge: its page runtime starts and sends hello.
        sharedRuntimeFor(host.bridge as never);
        await idle();
        continue;
      }
      if ("disconnect" in step) { host.disconnect(); await idle(); continue; }
      // The producer's mutations (including presented/1's `renew` and `waitPresented`), its transport and its close.
      if ("mutate" in step || "delivery" in step || "closeWindow" in step) continue;
      if ("advance" in step) { await advance(step["advance"]); continue; }
      if ("call" in step) {
        const call = step["call"];
        const route: string = call.route;
        switch (call.op) {
          case "register": {
            const answer = call.answer;
            const handler = () => answer === "never" ? new Promise(() => {}) : answer === "fail" ? Promise.reject(new Error("failed"))
              : answer?.output !== undefined ? answer.output : Promise.reject(new Error("cancelled"));
            handlers.set(route, { ...handlers.get(route), [call.interaction]: handler });
            calls.set(call.id, { state: "resolved", value: null });
            break;
          }
          case "open": {
            const model = modelOf(route);
            track(call.id, async () => {
              const view = await connectView<Record<string, unknown>>({
                contract: route === "notes" ? "Tests.Notes:fp-1" : `Tests.${route}:fp-1`, route, members: model.members,
                ...(model.checkedFields ? { checkedFields: model.checkedFields } : {}),
                ...(model.texts ? { texts: model.texts } : {}),
                ...(model.scopedCommands ? { scopedCommands: model.scopedCommands } : {}),
                ...(model.collections ? { collections: model.collections } : {}),
                ...(model.interactions ? { interactions: model.interactions } : {}),
                ...(handlers.has(route) ? { handlers: handlers.get(route)! } : {}),
                operations: bridgeOperations,
              });
              clients.set(route, view);
              let first = true;
              view.subscribe(() => { if (first) first = false; else notifications.set(route, (notifications.get(route) ?? 0) + 1); });
              return null;
            });
            break;
          }
          case "close": track(call.id, async () => { client(route).dispose(); return null; }); break;
          case "set": track(call.id, async () => { await client(route).set(call.member, call.value); return null; }); break;
          case "write": track(call.id, () => client(route).write(call.member, {
            requestId: call.requestId, expectedVersion: call.baseline.version, expectedValue: call.baseline.value, value: call.value,
          }, value => value)); break;
          case "can": track(call.id, () => client(route).can(call.member, call.input)); break;
          case "command": {
            const declared = modelOf(route).declared?.[call.member];
            track(call.id, async () => {
              if (!declared) { await client(route).command(call.member, call.input); return null; }
              const outcome = await client(route).commandOutcome(call.member, declared, call.input);
              if (isBridgeOutcome(outcome) && !outcome.ok) throw Object.assign(new Error("declared"), { declared: outcome.failure });
              return null;
            });
            break;
          }
          case "start": {
            const codec = modelOf(route).operations![call.member]!;
            const record: { handle?: BridgeOperationRuntimeHandle<unknown, unknown>; status?: unknown; route: string; member: string } = { route, member: call.member };
            operations.set(call.operation, record);
            track(call.id, async () => {
              record.handle = await client(route).start(call.member, ids.value(call.operation) as string, call.input, codec);
              return null;
            });
            break;
          }
          case "wait": case "status": case "cancel": {
            const record = operations.get(call.operation)!;
            const handle = record.handle!;
            if (call.op === "wait") {
              const stop = handle.subscribe(status => { record.status = publicStatus(status); });
              track(call.id, async () => { try { return publicStatus(await handle.wait()); } finally { stop(); } });
            } else if (call.op === "status") track(call.id, async () => (record.status = publicStatus(await handle.status())));
            else track(call.id, async () => (await handle.cancel()).kind);
            break;
          }
          default: throw new Error(`${scenario.id}: unknown call ${String(call.op)}`);
        }
        await idle();
        continue;
      }
      if ("send" in step) {
        const connection = host.connection!;
        for (let wait = 0; connection.sent.length === 0 && wait < 50; wait++) await new Promise(resolve => setTimeout(resolve, 2));
        const sent = connection.sent.shift();
        assert.ok(sent, `${scenario.id} ${at}: the client sent no request; expected ${JSON.stringify(step["send"])}`);
        const problem = matches(sent.message, step["send"], ids);
        assert.equal(problem, undefined, `${scenario.id} ${at}: ${problem}\n  sent ${sent.text}`);
        requests.set(step["id"], sent);
        continue;
      }
      if ("receive" in step) {
        const text = write(step["receive"], ids);
        if (step["drop"] === true) continue;
        if (step["replyTo"] !== undefined) {
          const request = requests.get(step["replyTo"]);
          assert.ok(request, `${scenario.id} ${at}: no request ${String(step["replyTo"])}`);
          request.resolve(text);
        } else host.push(text);
        await idle();
        continue;
      }
      if ("expect" in step) {
        await idle();
        const expect = step["expect"];
        if (expect["model"] !== undefined) continue;
        if (expect["state"]) sameDeep(client(expect["state"].route).snapshot, expect["state"].value, `${at} state`);
        if (expect["call"]) {
          const call = calls.get(expect["call"].id);
          assert.ok(call, `${scenario.id} ${at}: no call ${String(expect["call"].id)}`);
          if (expect["call"].pending) assert.equal(call.state, "pending", `${scenario.id} ${at}: ${expect["call"].id} is ${call.state}`);
          if ("resolved" in expect["call"]) {
            assert.equal(call.state, "resolved", `${scenario.id} ${at}: ${expect["call"].id} is ${call.state}: ${call.state === "rejected" ? String(call.error) : ""}`);
            sameDeep(call.state === "resolved" ? call.value : undefined, expect["call"].resolved, `${at} result`);
          }
          if (expect["call"].rejected) {
            assert.equal(call.state, "rejected", `${scenario.id} ${at}: ${expect["call"].id} is ${call.state}`);
            const error = (call as { error: unknown }).error;
            const expected = expect["call"].rejected;
            if (expected.kind === "domain-failed") sameDeep((error as { declared?: unknown }).declared, expected.failure, `${at} failure`);
            else {
              const actual = kindOf(error);
              assert.equal(actual.kind, expected.kind, `${scenario.id} ${at}: ${String(error)}`);
              if (expected.reason !== undefined) assert.equal((actual as { reason?: string }).reason, expected.reason);
            }
          }
        }
        if (expect["operation"]) sameDeep(operations.get(expect["operation"].id)?.status, expect["operation"].status, `${at} operation`);
        if (expect["diagnostics"]) assert.deepEqual(diagnostics, expect["diagnostics"], `${scenario.id} ${at}: diagnostics`);
        if (expect["notifications"])
          assert.equal(notifications.get(expect["notifications"].route) ?? 0, expect["notifications"].count, `${scenario.id} ${at}: notifications`);
        if (expect["connection"] !== undefined) assert.equal(connectionState, expect["connection"], `${scenario.id} ${at}: connection`);
        continue;
      }
      throw new Error(`${scenario.id} ${at}: unknown step ${JSON.stringify(step)}`);
    }
    await idle();
    const unmatched = host.connection?.sent.map(sent => sent.text) ?? [];
    assert.deepEqual(unmatched, [], `${scenario.id}: the client sent requests no step expected`);
  } finally {
    stopDiagnostics();
    stopConnection();
    for (const view of clients.values()) view.dispose();
    host.disconnect();
    delete (globalThis as { __runicBridge?: unknown }).__runicBridge;
    await idle();
    page.reportError = reportError;
  }
}

const scenarioFiles = readdirSync(new URL("scenarios/", corpus), { recursive: true, encoding: "utf8" }).filter(file => file.endsWith(".json")).sort();
const scenarios = scenarioFiles.map(file => JSON.parse(readFileSync(new URL(`scenarios/${file}`, corpus), "utf8")) as Scenario);

test("the corpus has client scenarios in every set", () => {
  const sets = new Set(scenarios.filter(scenario => scenario.roles.includes("client")).map(scenario => scenario.id.split(".")[0]));
  for (const set of ["handshake", "state-changes", "replies", "edits", "operations", "presentations", "interactions", "errors"]) assert.ok(sets.has(set), set);
});

for (const scenario of scenarios.filter(item => item.roles.includes("client"))) {
  test(`scenario ${scenario.id}`, async () => {
    useVirtualClock();
    try { await run(scenario); } finally { useRealClock(); }
  });
}

/* ------------------------------------------------------------------ message vectors */

interface Vector { readonly id: string; readonly operation: string; readonly request?: string; readonly input: { encoding: string; value: unknown }; readonly expect: { result: string } }
const vectorText = (input: Vector["input"]) => input.encoding === "json" ? JSON.stringify(input.value)
  : input.encoding === "utf8" ? input.value as string : Buffer.from(input.value as string, "base64").toString("utf8");

for (const file of ["pushes.json", "replies.json"]) {
  const vectors = JSON.parse(readFileSync(new URL(`vectors/messages/${file}`, corpus), "utf8")) as { cases: Vector[] };
  for (const vector of vectors.cases) {
    if (vector.operation === "read-request") continue;
    test(`vector ${vector.id}`, () => {
      const text = vectorText(vector.input);
      if (vector.operation === "read-push") assert.equal(readPush(text).result, vector.expect.result);
      else assert.equal(readReply(vector.request!, text) === undefined ? "invalid" : "valid", vector.expect.result);
    });
  }
}
