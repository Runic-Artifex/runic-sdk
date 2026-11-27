#!/usr/bin/env bun
// The conformance adapter of the TypeScript page client, @runic-artifex/views, in the client role
// (eng/conformance/PROTOCOL.md, `rax.conformance-adapter/1`). It links the package's production build (dist/), the
// test Views and the scripted host bridge of test/support/conformance-client.ts. The driver plays the producer: it
// writes what the host delivers and checks the requests the client sends. The adapter only calls the client's API.
//
// Run it through the driver: `bun run conformance ts-client`. It is a test tool and is never shipped.
import { readFileSync } from "node:fs";
import { createInterface } from "node:readline";
import { appearance, BridgeError, isBridgeOutcome, onBridgeConnection, onBridgeDiagnostic, windowLocale, type BridgeConnectionState, type QueryCollection } from "../dist/index.js";
import { bridgeOperations, connectView, type ViewConnection } from "../dist/generated.js";
import type { BridgeOperationRuntimeHandle } from "../dist/operations.js";
import { readPush, readReply } from "../dist/protocol.js";
import { sharedRuntimeFor } from "../dist/runtime.js";
import {
  advance, catalogRows, contractOf, decodeValue, idle, modelOf, models, ScriptedHost, UnknownValueType, useRealClock, useVirtualClock,
  type Connection, type Sent, type ValueParameters,
} from "./support/conformance-client.js";

// stdout carries the protocol's responses only: the client's console output goes to stderr.
for (const level of ["log", "info", "debug"] as const) console[level] = (...data: unknown[]) => console.error(...data);

const packageJson = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as { name: string; version: string };
const text = (base64: string) => Buffer.from(base64, "base64").toString("utf8");
const json = (value: unknown) => JSON.parse(JSON.stringify(value ?? null)) as unknown;

class Unsupported extends Error { readonly code = "unsupported"; }

type CallState = { state: "pending" } | { state: "resolved"; value: unknown } | { state: "rejected"; error: unknown };
type Operation = { handle?: BridgeOperationRuntimeHandle<unknown, unknown>; status?: unknown };

/** The features the client lists in its `hello`, read from the hello it sends to a probe connection. */
async function clientFeatures(): Promise<string[]> {
  const host = new ScriptedHost();
  const connection = host.connect("describe");
  const runtime = sharedRuntimeFor(globalThis as never, host.bridge as never);
  try {
    for (let wait = 0; connection.sent.length === 0 && wait < 500; wait++) await new Promise(resolve => setTimeout(resolve, 2));
    const hello = connection.sent[0]?.message as { kind?: unknown; features?: unknown } | undefined;
    if (hello?.kind !== "hello" || !Array.isArray(hello.features)) throw new Error("The client sent no hello to the probe connection.");
    return hello.features.filter((feature): feature is string => typeof feature === "string");
  } finally {
    host.disconnect();
    runtime.dispose?.();
    await idle();
  }
}

/** One scenario: the scripted host, the routes the application opened and what the client reported. */
class Scenario {
  readonly host = new ScriptedHost();
  private readonly connections = new Map<string, Connection>();
  private readonly tags = new Map<string, Sent>();
  private nextTag = 0;
  private readonly calls = new Map<string, CallState>();
  private readonly clients = new Map<string, ViewConnection<Record<string, unknown>>>();
  private readonly handlers = new Map<string, Record<string, (input: never) => unknown>>();
  private readonly notified = new Map<string, number>();
  private readonly handled = new Map<string, { readonly inputs: unknown[]; stop?: () => void }>();
  private readonly operations = new Map<string, Operation>();
  private readonly queries = new Map<string, QueryCollection<unknown>>();
  readonly diagnostics: string[] = [];
  connectionState: BridgeConnectionState | undefined;
  private readonly stops: (() => void)[] = [];
  private readonly reportError: ((error: unknown) => void) | undefined;

  constructor() {
    (globalThis as { __runicBridge?: unknown }).__runicBridge = this.host.bridge;
    this.stops.push(onBridgeDiagnostic(diagnostic => { this.diagnostics.push(diagnostic.code); }));
    // The runtime reports errors it cannot throw to a caller, such as a `failure` push, with `reportError`.
    const page = globalThis as { reportError?: ((error: unknown) => void) | undefined };
    this.reportError = page.reportError;
    page.reportError = () => {};
    // The connection state the client reports (presented/1: `closed` after the producer closed the window).
    this.stops.push(onBridgeConnection(state => { this.connectionState = state; }, { lostAfter: 2_147_483_647 }));
    useVirtualClock();
  }

  async end(): Promise<void> {
    for (const stop of this.stops) stop();
    for (const view of this.clients.values()) view.dispose();
    this.host.disconnect();
    delete (globalThis as { __runicBridge?: unknown }).__runicBridge;
    await idle();
    (globalThis as { reportError?: ((error: unknown) => void) | undefined }).reportError = this.reportError;
    useRealClock();
  }

  async connect(name: string): Promise<void> {
    this.connections.set(name, this.host.connect(name));
    // The client connects to the host bridge: its page runtime starts and sends hello.
    sharedRuntimeFor(globalThis as never, this.host.bridge as never);
    await idle();
  }

  async disconnect(name: string): Promise<void> {
    if (this.host.connection !== this.connections.get(name)) throw new Error(`The connection ${name} is not the host's current connection.`);
    this.host.disconnect();
    await idle();
  }

  async nextRequest(name: string, waitMs: number): Promise<{ tag: string; text: string } | null> {
    const connection = this.connections.get(name);
    if (!connection) throw new Error(`The connection ${name} was not connected.`);
    const deadline = Date.now() + waitMs;
    while (connection.sent.length === 0 && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 2));
    const sent = connection.sent.shift();
    if (!sent) return null;
    const tag = `${name}#${++this.nextTag}`;
    this.tags.set(tag, sent);
    return { tag, text: sent.text };
  }

  async deliver(replyTo: string | null, message: string): Promise<void> {
    if (replyTo === null) this.host.push(message);
    else {
      const request = this.tags.get(replyTo);
      if (!request) throw new Error(`No request was taken with the tag ${replyTo}.`);
      request.resolve(message);
    }
    await idle();
  }

  private client(route: string) {
    const found = this.clients.get(route);
    if (!found) throw new Error(`The route ${route} is not open.`);
    return found;
  }

  private track(id: string, work: () => Promise<unknown>) {
    this.calls.set(id, { state: "pending" });
    work().then(value => this.calls.set(id, { state: "resolved", value }), error => this.calls.set(id, { state: "rejected", error }));
  }

  private handledKey = (route: string, request: string) => `${route}\u0000${request}`;

  private startHandling(route: string, request: string) {
    const surface = this.client(route).requests[request];
    if (!surface) throw new Error(`The route ${route} has no view request ${request}.`);
    const record = this.handled.get(this.handledKey(route, request))!;
    record.stop = surface.handle((input: never) => { record.inputs.push(input); });
  }

  private query(route: string, member: string) {
    const key = `${route}\u0000${member}`;
    let found = this.queries.get(key);
    if (!found) this.queries.set(key, found = this.client(route).queryCollection(member, catalogRows));
    return found;
  }

  // README "Calls": the language-neutral call mapped to the client's API.
  async call(id: string, call: any): Promise<void> {
    const route: string = call.route;
    switch (call.op) {
      case "register": {
        const answer = call.answer;
        const handler = () => answer === "never" ? new Promise(() => {}) : answer === "fail" ? Promise.reject(new Error("failed"))
          : answer?.output !== undefined ? answer.output : Promise.reject(new Error("cancelled"));
        this.handlers.set(route, { ...this.handlers.get(route), [call.interaction]: handler });
        this.calls.set(id, { state: "resolved", value: null });
        break;
      }
      case "open": {
        const model = modelOf(route);
        if (!model) throw new Unsupported(`The adapter has no test View for the route ${route}.`);
        this.track(id, async () => {
          const view = await connectView<Record<string, unknown>>({
            contract: contractOf(route), route, members: model.members,
            ...(model.checkedFields ? { checkedFields: model.checkedFields } : {}),
            ...(model.texts ? { texts: model.texts } : {}),
            ...(model.scopedCommands ? { scopedCommands: model.scopedCommands } : {}),
            ...(model.collections ? { collections: model.collections } : {}),
            ...(model.interactions ? { interactions: model.interactions } : {}),
            ...(this.handlers.has(route) ? { handlers: this.handlers.get(route)! } : {}),
            ...(model.requests ? { requests: model.requests } : {}),
            operations: bridgeOperations,
          });
          this.clients.set(route, view);
          // Handlers of `handle` calls made before the route opened.
          for (const [key, record] of this.handled)
            if (key.startsWith(`${route}\u0000`) && !record.stop) this.startHandling(route, key.slice(route.length + 1));
          let first = true;
          view.subscribe(() => { if (first) first = false; else this.notified.set(route, (this.notified.get(route) ?? 0) + 1); });
          return null;
        });
        break;
      }
      case "handle": {
        // A handler that records each input, registered at once, or once the route opened.
        this.handled.get(this.handledKey(route, call.request))?.stop?.();
        this.handled.set(this.handledKey(route, call.request), { inputs: [] });
        if (this.clients.has(route)) this.startHandling(route, call.request);
        this.calls.set(id, { state: "resolved", value: null });
        break;
      }
      case "unhandle":
        this.handled.get(this.handledKey(route, call.request))?.stop?.();
        this.calls.set(id, { state: "resolved", value: null });
        break;
      case "close": this.track(id, async () => { this.client(route).dispose(); return null; }); break;
      case "set": this.track(id, async () => { await this.client(route).set(call.member, call.value); return null; }); break;
      case "write": this.track(id, () => this.client(route).write(call.member, {
        ...call.path === undefined ? {} : { path: call.path },
        requestId: call.requestId, expectedVersion: call.baseline.version, expectedValue: call.baseline.value, value: call.value,
      }, value => value)); break;
      case "can": this.track(id, () => this.client(route).can(call.member, call.input)); break;
      case "region": this.track(id, () => this.client(route).region(call.member, call.action, call.key)); break;
      case "command": {
        const declared = modelOf(route)?.declared?.[call.member];
        this.track(id, async () => {
          if (!declared) { await this.client(route).command(call.member, call.input); return null; }
          const outcome = await this.client(route).commandOutcome(call.member, declared, call.input);
          if (isBridgeOutcome(outcome) && !outcome.ok) throw Object.assign(new Error("declared"), { declared: outcome.failure });
          return null;
        });
        break;
      }
      case "start": {
        // `operation` is the operation's id as the driver writes it.
        const codec = modelOf(route)?.operations?.[call.member];
        if (!codec) throw new Unsupported(`The route ${route} has no operation ${call.member}.`);
        const record: Operation = {};
        this.operations.set(call.operation, record);
        this.track(id, async () => {
          record.handle = await this.client(route).start(call.member, call.operation, call.input, codec);
          return null;
        });
        break;
      }
      case "wait": case "status": case "cancel": {
        const record = this.operations.get(call.operation);
        const handle = record?.handle;
        if (!record || !handle) throw new Error(`The operation ${String(call.operation)} has not started.`);
        if (call.op === "wait") {
          const stop = handle.subscribe(status => { record.status = publicStatus(status); });
          this.track(id, async () => { try { return publicStatus(await handle.wait()); } finally { stop(); } });
        } else if (call.op === "status") this.track(id, async () => (record.status = publicStatus(await handle.status())));
        else this.track(id, async () => (await handle.cancel()).kind);
        break;
      }
      // query-collections/1: a range resolves with the keys of its rows once they are all loaded and current.
      case "queryRange": {
        const collection = this.query(route, call.member);
        collection.setRange(call.start, call.count);
        this.track(id, () => new Promise(resolve => {
          const check = () => {
            const current = collection.current;
            if (current.loading || current.rows.some(item => item.status !== "loaded" || item.stale)) return;
            stop();
            resolve(current.rows.map(item => item.key));
          };
          const stop = collection.subscribe(check);
          check();
        }));
        break;
      }
      case "setQuery": this.track(id, async () => { await this.query(route, call.member).setQuery(call.value); return null; }); break;
      case "indexOf": this.track(id, async () => (await this.query(route, call.member).indexOf(call.value)) ?? null); break;
      default: throw new Unsupported(`The adapter has no call ${String(call.op)}.`);
    }
    await idle();
  }

  async callResult(id: string, waitMs: number): Promise<unknown> {
    const deadline = Date.now() + waitMs;
    while (this.calls.get(id)?.state === "pending" && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 2));
    const call = this.calls.get(id);
    if (!call) throw new Error(`No call ${id} was made.`);
    if (call.state === "pending") return { state: "pending" };
    if (call.state === "resolved") return { state: "resolved", value: json(call.value) };
    const error = call.error as { declared?: unknown };
    if (error && typeof error === "object" && "declared" in error) return { state: "rejected", error: { kind: "domain-failed", failure: json(error.declared) } };
    if (call.error instanceof BridgeError)
      return { state: "rejected", error: { kind: call.error.kind, ...(call.error.reason === undefined ? {} : { reason: call.error.reason }) } };
    return { state: "rejected", error: { kind: `unmapped:${call.error instanceof Error ? call.error.name : typeof call.error}`, message: String(call.error) } };
  }

  state(route: string) { return json(this.client(route).snapshot); }
  operation(id: string) { return json(this.operations.get(id)?.status); }
  notifications(route: string) { return this.notified.get(route) ?? 0; }
  requests(route: string, request: string) { return json(this.handled.get(this.handledKey(route, request))?.inputs ?? []); }
}

// README "Calls": an operation's status without its contract and request id, which the corpus does not compare.
function publicStatus(status: unknown) {
  const { contract: _, requestId: __, ...rest } = status as Record<string, unknown>;
  return rest;
}

// views.decode: the normal form of a value vector's input, or `invalid-value` for the TypeError or RangeError of an
// invalid value. Any other failure is reported as it is, and the case fails.
function decode(type: string, parameters: ValueParameters, input: string): unknown {
  let value: unknown;
  try {
    value = JSON.parse(input);
  } catch (e) {
    return { error: `unmapped:${(e as Error).name}` };
  }
  try {
    return { value: JSON.stringify(decodeValue(type, parameters, value) ?? null) };
  } catch (e) {
    if (e instanceof UnknownValueType) throw new Unsupported(e.message);
    if (e instanceof TypeError || e instanceof RangeError) return { error: "invalid-value" };
    return { error: `unmapped:${(e as Error).name ?? typeof e}` };
  }
}

let scenario: Scenario | undefined;
const current = () => {
  if (!scenario) throw new Error("No scenario has begun (client.begin).");
  return scenario;
};

async function handle(op: string, args: any): Promise<unknown> {
  switch (op) {
    case "describe":
      return {
        protocol: "rax.conformance-adapter/1",
        implementation: { id: "ts-client", name: packageJson.name, version: packageJson.version },
        corpora: ["views/4"],
        roles: ["client"],
        features: await clientFeatures(),
        capabilities: ["virtual-clock"],
      };
    case "shutdown":
      return {};
    case "views.readPush":
      return { result: readPush(text(args.input)).result };
    case "views.readReply":
      return { result: readReply(args.request, text(args.input)) === undefined ? "invalid" : "valid" };
    case "views.decode":
      return decode(args.type, args.parameters, text(args.input));
    case "client.begin":
      if (!Object.hasOwn(models, args.view)) throw new Unsupported(`The adapter has no test View ${String(args.view)}.`);
      if (scenario) await scenario.end();
      scenario = new Scenario();
      return {};
    case "client.end":
      if (scenario) await scenario.end();
      scenario = undefined;
      return {};
    case "client.connect": await current().connect(args.conn); return {};
    case "client.disconnect": await current().disconnect(args.conn); return {};
    case "client.nextRequest": return { request: await current().nextRequest(args.conn, args.waitMs ?? 0) };
    case "client.deliver": await current().deliver(args.replyTo ?? null, args.text); return {};
    case "client.advance": current(); await advance(args.ms); return {};
    case "client.call": await current().call(args.id, args.call); return {};
    case "client.callResult": return current().callResult(args.id, args.waitMs ?? 0);
    case "client.state": await idle(); return { value: current().state(args.route) };
    case "client.operation": await idle(); return { status: current().operation(args.operation) };
    case "client.notifications": await idle(); return { count: current().notifications(args.route) };
    case "client.diagnostics": await idle(); return { codes: [...current().diagnostics] };
    case "client.connection": await idle(); return { state: current().connectionState ?? null };
    case "client.locale": await idle(); current(); return { locale: windowLocale() };
    case "client.appearance": await idle(); current(); return { appearance: json(appearance().host) };
    case "client.requests": await idle(); return { inputs: current().requests(args.route, args.request) };
    default:
      throw new Unsupported(`The TypeScript client adapter has no operation ${op}.`);
  }
}

const write = (message: unknown) => process.stdout.write(JSON.stringify(message) + "\n");
for await (const line of createInterface({ input: process.stdin })) {
  if (!line.trim()) continue;
  const { seq, op, args } = JSON.parse(line) as { seq: number; op: string; args?: unknown };
  try {
    write({ seq, ok: await handle(op, args ?? {}) });
  } catch (e) {
    const error = e as { code?: string; message?: string; stack?: string };
    if (error.code !== "unsupported") console.error(error.stack ?? String(e));
    write({ seq, error: { code: error.code === "unsupported" ? "unsupported" : "failed", message: String(error.message ?? e) } });
  }
  if (op === "shutdown") break;
}
