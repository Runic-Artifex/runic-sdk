import { BridgeError } from "./errors.js";
import type { Message, Reply } from "./protocol.js";
import type { Slots } from "./scope.js";

// The development Bridge-call channel on a registered symbol of an instance's slots, which copies of this package and
// `@runic-artifex/vite-plugin-runic/client` share without a dependency (specs/application, "Shared globals"). A reader
// (the Vite development client) adds a listener and sets `requested`; every copy then reports each call a generated
// client makes, with a client-local id, its timing, its outcome and the reply's revision, and runs it in a V8 async
// stack task, so a reply leads back to the call. The reader draws the calls, such as the Vite development client's
// "Runic" track in Chromium's Performance panel, so production bundles carry no drawing code. Without a reader, a call
// costs one property read and nothing is kept or tagged. Only the development build (the `development` export
// condition, scripts/build.mjs) reports calls: the production build does not contain this module.
const callsKey = Symbol.for("runic.views.calls");

/**
 * trace-context/1: a call's request carries the W3C trace context its reader gave it (`BridgeCallChannel.trace`), so
 * the .NET span of the call (`runic.bridge.*`) and the ViewModel's own spans join the page's trace.
 */
export const traceContextFeature = "trace-context/1";
/**
 * handler-timing/1: while a reader listens, a call's request asks for its C# handler's name and duration, which a .NET
 * host in Development adds to the reply.
 */
export const handlerTimingFeature = "handler-timing/1";

/** A call's W3C trace context: the page's span for the call, as `traceparent` and optionally `tracestate`. */
export interface BridgeCallTrace {
  readonly traceparent: string;
  readonly tracestate?: string;
}

/**
 * The C# handler of a call, from a .NET host in Development (handler-timing/1): its name, such as
 * `EditorViewModel.Save`, and its duration in milliseconds as .NET measured it. `start` and `end` place it on the
 * page's clock (`performance.now()` milliseconds), centred in the time between sending the request and receiving its
 * reply, which assumes the request and the reply took equally long on the way.
 */
export interface BridgeCallHandler {
  readonly name: string;
  readonly start: number;
  readonly end: number;
  readonly duration: number;
}

/**
 * What a generated client call does: `command` (also a command with a declared failure), `set` (a property setter),
 * `write` (a checked field write), `can` (command availability), `region` (select or close a region), `operation` (the
 * start or recovery of a long-running command), `query` (a query collection's query, rows or index). Open: a later
 * release may add kinds.
 */
export type BridgeCallKind = "command" | "set" | "write" | "can" | "region" | "operation" | "query";

/**
 * How a call ended: `ok`; `error` (it rejected, or a command answered with its declared failure); `cancelled` (nothing
 * ran for it, or its answer no longer applies: superseded, released, aborted or cancelled).
 */
export type BridgeCallOutcome = "ok" | "error" | "cancelled";

/** What identifies one call in both of its events. */
export interface BridgeCallIdentity {
  /**
   * A client-local id, increasing per channel, so per page. It is not sent to .NET; correlate with .NET through
   * `revision`.
   */
  readonly id: number;
  readonly kind: BridgeCallKind;
  /** The wire request kind, such as `command`, `set` or `query-rows`. */
  readonly request: string;
  readonly route: string;
  /** The View contract of the route, `{C# view model}:{fingerprint}`. */
  readonly contract: string;
  /** The member's wire name. */
  readonly member: string;
  /** When the call started, in `performance.now()` milliseconds. */
  readonly start: number;
  /** The trace context the reader gave the call (`BridgeCallChannel.trace`), which its request carries. */
  readonly trace?: BridgeCallTrace;
}

/**
 * What the Bridge-call channel reports. `start` is delivered synchronously in the caller's stack (so a listener can read
 * the DOM event that caused the call); `end` once the call settled, after the route reached the reply's revision. A
 * listener ignores phases and fields it does not know.
 */
export type BridgeCallEvent =
  | BridgeCallIdentity & { readonly phase: "start" }
  | BridgeCallIdentity & {
    readonly phase: "end";
    /** When the call settled, in `performance.now()` milliseconds, and `end - start`. */
    readonly end: number;
    readonly duration: number;
    readonly outcome: BridgeCallOutcome;
    /** The reply's revision: the frames with this revision carry the call's effects. */
    readonly revision?: number;
    /**
     * What refines the outcome: `released` for `ok` (the call ran, and .NET released its View); `superseded` (nothing
     * ran), `released` (the View was released before the call) or `aborted` for `cancelled`; for `error`, the reply's
     * or error's reason, such as `domain-failed`, or the `BridgeError` kind.
     */
    readonly reason?: string;
    /** The error the call rejected with, for `error` and `cancelled`; listeners must not change it. */
    readonly error?: unknown;
    /** The C# handler of the call's last reply that named one: only from a .NET host in Development. */
    readonly handler?: BridgeCallHandler;
  };

/** A reader of the Bridge-call channel. */
export type BridgeCallListener = (event: BridgeCallEvent) => void;

/**
 * The value at `globalThis[Symbol.for("runic.views.calls")]` (an instance's slots). A reader creates it if absent, adds
 * a listener and sets `requested`. `devtools: false` asks for the calls without developer tools integration: no async
 * stack tasks, and a reader that draws calls does not. `sequence` is the last id handed out, which every copy of the
 * package shares. `trace`, set by a reader that exports traces, gives each call its trace context; without it no
 * trace context is created or sent.
 */
export interface BridgeCallChannel {
  readonly listeners?: Set<BridgeCallListener>;
  requested?: boolean;
  devtools?: boolean;
  sequence?: number;
  trace?: () => BridgeCallTrace | undefined;
}

// V8's async stack tagging (`console.createTask`), absent in WebKit and Gecko.
interface ConsoleTask { run<T>(run: () => T): T }

/** @internal A call in progress while a reader listens. */
export interface BridgeCall {
  readonly channel: BridgeCallChannel;
  readonly identity: BridgeCallIdentity;
  readonly task: ConsoleTask | undefined;
  revision?: number | undefined;
  outcome?: BridgeCallOutcome | undefined;
  reason?: string | undefined;
  handler?: BridgeCallHandler | undefined;
}

function deliver(channel: BridgeCallChannel, event: BridgeCallEvent): boolean {
  const listeners = channel.requested === true ? channel.listeners : undefined;
  if (!(listeners instanceof Set) || listeners.size === 0) return false;
  for (const listener of [...listeners]) {
    try { listener(event); } catch (error) { console.error(error); }
  }
  return true;
}

/**
 * @internal Starts reporting a call when a reader listens; otherwise returns `undefined` after one property read. Call
 * it synchronously in the caller's stack, so the start event and the async stack lead to the code that made the call.
 */
export function beginCall(slots: Slots, kind: BridgeCallKind, request: string, route: string, contract: string, member: string): BridgeCall | undefined {
  const channel = slots[callsKey] as BridgeCallChannel | undefined;
  if (channel?.requested !== true) return undefined;
  const id = (typeof channel.sequence === "number" ? channel.sequence : 0) + 1;
  let trace: BridgeCallTrace | undefined;
  try { trace = channel.trace?.(); } catch (error) { console.error(error); }
  const identity: BridgeCallIdentity = {
    id, kind, request, route, contract, member, start: performance.now(),
    ...typeof trace?.traceparent === "string" ? { trace } : {},
  };
  if (!deliver(channel, { ...identity, phase: "start" })) return undefined;
  channel.sequence = id;
  // `Demo.EditorViewModel:3F2A…` names the C# view model `EditorViewModel`.
  const model = contract.split(":", 1)[0]!.split(".").pop() || route;
  let task: ConsoleTask | undefined;
  try {
    task = channel.devtools === false ? undefined
      : (console as { createTask?: (name: string) => ConsoleTask }).createTask?.(`Runic ${kind} ${model}.${member}`);
  } catch { /* Not V8. */ }
  return { channel, identity, task };
}

/**
 * @internal Sends a request of a call: with its trace context when the connection uses trace-context/1, and asking for
 * its handler's timing when it uses handler-timing/1, which it records from the reply.
 */
export async function requestCall(call: BridgeCall, features: ReadonlySet<string> | undefined,
  send: (message: Message) => Promise<Reply>, message: Message): Promise<Reply> {
  const trace = features?.has(traceContextFeature) === true ? call.identity.trace : undefined;
  const timing = features?.has(handlerTimingFeature) === true;
  const sent = performance.now();
  const reply = await send(trace || timing ? { ...message, ...trace ? { trace } : {}, ...timing ? { timing: true } : {} } : message);
  const handler = reply["handler"] as { name?: unknown; duration?: unknown } | undefined;
  if (typeof handler?.name === "string" && typeof handler.duration === "number" && handler.duration >= 0) {
    const received = performance.now();
    const start = sent + Math.max(0, (received - sent - handler.duration) / 2);
    call.handler = { name: handler.name, start, end: start + handler.duration, duration: handler.duration };
  }
  return reply;
}

/** @internal Records what a reply says about the call: its revision, and an outcome other than `ok` that it resolves with. */
export function noteReply(call: BridgeCall | undefined, reply: Reply): void {
  if (call === undefined) return;
  if (reply.revision !== undefined) call.revision = reply.revision;
  // A superseded call ran nothing; a released one ran, and .NET released its View before the reply.
  const superseded = reply.ok && reply["superseded"] === true;
  call.outcome = !reply.ok ? "error" : superseded ? "cancelled" : undefined;
  call.reason = !reply.ok ? reply.error?.reason ?? reply.error?.kind : superseded ? "superseded" : reply.released === true ? "released" : undefined;
}

/** @internal Marks a call that resolved without running, such as a set a newer edit replaced. */
export function noteCancelled(call: BridgeCall | undefined, reason: string): void {
  if (call === undefined) return;
  call.outcome = "cancelled";
  call.reason = reason;
}

/**
 * @internal Runs a call's body in its async stack task: with V8's async stack tagging, a breakpoint in what runs after
 * the reply (a state subscriber, the caller's continuation) shows a `Runic {kind} {model}.{member}` segment that leads
 * to the code that made the call, even when the reply arrived on a transport callback.
 */
export function withinCall<T>(call: BridgeCall | undefined, run: () => T): T {
  return call?.task === undefined ? run() : call.task.run(run);
}

/** @internal Ends a call, resolved or rejected with `failure`, and reports it. */
export function endCall(call: BridgeCall, failed: boolean, failure?: unknown): void {
  const end = performance.now();
  let { outcome = "ok", reason } = call;
  if (failed) {
    const bridge = failure instanceof BridgeError;
    outcome = (bridge ? failure.kind === "cancelled" || failure.kind === "released" : (failure as Error | undefined)?.name === "AbortError")
      ? "cancelled" : "error";
    reason = bridge ? failure.reason ?? failure.kind : outcome === "cancelled" ? "aborted" : reason;
  }
  const { identity, revision, handler } = call;
  deliver(call.channel, {
    ...identity, phase: "end", end, duration: end - identity.start, outcome,
    ...revision === undefined ? {} : { revision },
    ...reason === undefined ? {} : { reason },
    ...failed ? { error: failure } : {},
    ...handler === undefined ? {} : { handler },
  });
}

/** @internal Reports `run` as one call. */
export async function traced<T>(call: BridgeCall, run: () => Promise<T>): Promise<T> {
  let result: T;
  try { result = await withinCall(call, run); }
  catch (error) { endCall(call, true, error); throw error; }
  endCall(call, false);
  return result;
}
