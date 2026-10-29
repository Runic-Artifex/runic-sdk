import type { BridgeErrorKind, BridgeErrorReason, BridgeFailureDetail } from "./errors.js";
import type { RunicBridgeClient } from "./transport.js";
import { viewsVersion } from "./version.js";
import { applySplice, producerSplices, textSpliceFeature } from "./text.js";

/** A View's wire state: one member per state member, by wire name, encoded as on the wire. */
export type MockState = Record<string, unknown>;

/**
 * Runs before a `set` applies `value` (a wire value) to its member. Return more state changes, which are merged
 * after the value, so returning the member itself normalizes it; throw to reject the value. A thrown error becomes
 * a failed reply with its type, message and stack as `detail`; an error with a `kind` (and `reason`) keeps them.
 */
export type MockSetter = (state: MockState, value: unknown) => MockState | void | Promise<MockState | void>;

/**
 * Runs a command with its wire input (`undefined` for a command without input) and returns the state changes it
 * makes. A thrown error becomes a failed reply, an error with a `kind` (and `reason`) keeps them, and
 * `throw mockFailure(failure)` replies with the declared failure (`domain-failed`).
 */
export type MockCommand = (state: MockState, input: unknown) => MockState | void | Promise<MockState | void>;

/**
 * Whether a command is available for a wire input (`can`, and the check before the command runs). A thrown error
 * with a `kind` and `reason`, such as `{ kind: "rejected", reason: "unresolved-key" }`, rejects the command and
 * its start with them, and `can` answers `false`.
 */
export type MockAvailability = (state: MockState, input: unknown) => boolean;

/**
 * When the mock delivers replies and pushes. `immediate` (the default)
 * answers each request as soon as its handler returns and pushes at once.
 * `manual` queues requests, replies and pushes until `flush()`, `flushUntil()` or `advance()`,
 * so a test decides exactly when the client observes each one.
 */
export type MockScheduling = "immediate" | "manual";

/**
 * How long each request, reply and push travels: a number of milliseconds, a range `{ min, max }` from which each
 * message draws its own delay, or `"slow"` (30 to 120 ms), which makes key-by-key typing race its echoes.
 */
export type MockLatency = number | { readonly min: number; readonly max: number } | "slow";

export interface MockBridgeOptions {
  readonly scheduling?: MockScheduling;
  /**
   * Delays every request, reply and push. Replies and pushes stay in the order the mock sent them, as the
   * transports guarantee. Real timers measure it with `immediate` scheduling, and the virtual clock (`advance()`)
   * with `manual`.
   */
  readonly latency?: MockLatency;
  /**
   * Processes requests out of order: each request is admitted when its own delay ends, and requests due together
   * are admitted newest first, as a producer that does not keep a connection's admission order. Setter edits that
   * arrive after a newer edit of their member then answer `superseded`, as a producer does. Requests that change
   * state (`set`, `write`, `command`, `start`) run one at a time in the order they are admitted, as a window's turns:
   * a command handler that awaits holds the next one until it returns.
   */
  readonly reorderRequests?: boolean;
}

/** An unexpected failure the mock answers instead of running a handler. */
export interface MockErrorFailure {
  /**
   * The BridgeError kind of the reply. `transport` makes the request itself reject,
   * like a dropped host connection; the client reports it as `failed`, or as
   * `disconnected` after `disconnect()`. `disconnected` is not a reply kind: a producer
   * never sends it, so use `disconnect()` for a lost connection. `released` is how
   * a producer answers a released View.
   */
  readonly kind: Exclude<BridgeErrorKind, "disconnected"> | "transport";
  readonly message?: string;
  /** Development-only failure detail, as `BridgeError.detail` exposes it. */
  readonly detail?: BridgeFailureDetail;
  /** What refines `kind`, as `BridgeError.reason` exposes it, such as `unresolved-key` for `rejected`. */
  readonly reason?: BridgeErrorReason;
}

/**
 * A declared failure (`domain-failed`) the mock answers instead of running a
 * handler. A command replies with it; a `start` admits an operation
 * that has already failed with it.
 */
export interface MockDomainFailure<TFailure = unknown> {
  readonly kind: "domain-failed";
  /** The failure; the wire value for the untyped mock, the typed value for a generated mock. */
  readonly failure: TFailure;
  readonly message?: string;
}

/** A failure the mock answers instead of running a handler. */
export type MockFailure = MockErrorFailure | MockDomainFailure;

const domainFailureKey = Symbol.for("runic.views.mockFailure");

/**
 * An error that a mock command or operation handler throws to fail with its
 * declared failure: `throw mockFailure({ $case: "titleRequired" })`.
 */
export function mockFailure<TFailure>(failure: TFailure, message = "The command failed with its declared failure."): Error {
  return Object.assign(new Error(message), { kind: "domain-failed", [domainFailureKey]: { failure } });
}

/** @internal The failure carried by a `mockFailure` error, if `error` is one. */
export function mockFailureOf(error: unknown): { readonly failure: unknown } | undefined {
  return typeof error === "object" && error !== null
    ? (error as Record<symbol, { readonly failure: unknown } | undefined>)[domainFailureKey] : undefined;
}

/** One keyed collection change of a frame (specs/application/state-changes.md). */
export interface MockCollectionChange {
  readonly field: string;
  readonly kind: "add" | "remove" | "replace" | "move";
  readonly index: number;
  readonly oldIndex: number;
  readonly keys: readonly string[];
  readonly items: readonly unknown[];
}

/**
 * Edits one keyed collection of a mock View and pushes each edit as a frame with
 * indexed changes, like a producer's keyed collection. Inside `MockView.batch` the edits
 * share one frame.
 */
export interface MockCollection<TItem = unknown> {
  readonly items: readonly TItem[];
  readonly keys: readonly string[];
  /** Inserts rows at `index`, by default at the end. */
  add(items: TItem | readonly TItem[], index?: number): void;
  remove(key: string): void;
  replace(key: string, item: TItem): void;
  move(key: string, index: number): void;
}

/**
 * An operation's status kind, as the protocol names it. `expired`: the window no longer retains the finished
 * operation's outcome (`MockOperation.expire()`). `unknown`: the window does not know the operation id, for
 * example after a restart (`MockOperation.forget()`). The set is open: a minor release may add statuses, so code
 * that switches over it handles an unknown value.
 */
export type MockOperationKind = "running" | "succeeded" | "domain-failed" | "failed" | "cancelled" | "expired" | "unknown";

/** An operation a client started with `start{Command}()`. */
export interface MockOperation<TInput = unknown, TResult = unknown> {
  /** The command's wire name. */
  readonly member: string;
  /** The operation id. */
  readonly requestId: string;
  readonly input: TInput;
  readonly kind: MockOperationKind;
  /** Aborts when the client cancels the operation or the wait times out. */
  readonly signal: AbortSignal;
  /** The stream values emitted so far. */
  readonly items: readonly TResult[];
  /** The latest progress the operation reported, from 0 to 1, or undefined. */
  readonly progress: number | undefined;
  /** Reports progress, clamped to 0 to 1; watching clients receive it as `running` with `progress`. */
  reportProgress(value: number): void;
  succeed(result?: TResult): void;
  fail(message?: string, detail?: BridgeFailureDetail): void;
  /** Ends the operation with its declared failure (`domain-failed`), as a wire value. */
  failWith(failure: unknown): void;
  cancel(): void;
  /**
   * Drops the finished operation's outcome, as a producer does when its retention runs out: its status, watch,
   * stream and cancel then answer `expired`, and a `start` that repeats its id and input answers `expired`. Throws
   * for a running operation, which a producer does not expire.
   */
  expire(): void;
  /**
   * Forgets the operation id, as a restarted window has: its status, watch, stream and cancel then answer
   * `unknown`, and a `start` with the id starts a new operation. A running operation's signal
   * aborts, and a later outcome of its handler is ignored.
   */
  forget(): void;
  /** Appends values to a stream operation. */
  emit(...items: TResult[]): void;
}

/** What an operation handler's completion means. */
export interface MockOperationOutcome {
  /** Merged into the state before the operation succeeds. */
  readonly state?: MockState;
  /** The wire result of the operation. */
  readonly result?: unknown;
}

/**
 * Runs a started operation with its wire input. When the returned promise resolves, a still running
 * operation succeeds with its outcome; a throw fails it, or cancels it after the client
 * asked to cancel, and `throw mockFailure(failure)` ends it `domain-failed`. Cancellation is cooperative: a cancel
 * request aborts `operation.signal`, and the operation keeps running until its handler stops.
 * `"manual"` leaves every operation running until the test settles it through
 * `MockView.operations()`.
 */
export type MockOperationHandler = (state: MockState, input: unknown, operation: MockOperation) =>
  MockOperationOutcome | void | Promise<MockOperationOutcome | void>;

export interface MockViewDefinition {
  readonly state: MockState;
  /**
   * Setter handlers by member wire name, such as `{ set: { title: (state, value) => … } }`. A `set` of a state
   * member without a handler applies its value.
   */
  readonly set?: Readonly<Record<string, MockSetter>>;
  /** Command handlers by member wire name, such as `{ commands: { save: state => … } }`. */
  readonly commands?: Readonly<Record<string, MockCommand>>;
  /**
   * Command availability by member wire name. Without one, a command is available unless the state has its
   * availability member `can{Name}` set to `false`.
   */
  readonly can?: Readonly<Record<string, MockAvailability>>;
  /** Row keys of each keyed collection field, which `collection(field)` edits with indexed changes. */
  readonly collections?: Readonly<Record<string, (item: unknown) => string>>;
  /** Wire names of checked fields (`write` and their `fields` versions). */
  readonly checkedFields?: readonly string[];
  /**
   * Wire names of text members (string members other than append-only text fields). On a connection that uses
   * `text-splice/1`, frames change them with splices, and setters accept splices, as a producer does.
   */
  readonly texts?: readonly string[];
  /**
   * Operation handlers by command member, such as `save`. Without one, an
   * operation runs the command's handler, if any, and succeeds.
   */
  readonly operations?: Readonly<Record<string, MockOperationHandler | "manual">>;
  /** Command members whose operations yield a stream. */
  readonly streams?: readonly string[];
  /** The View's operation contract, `{view}:{fingerprint}`. Defaults to `mock.{route}:mock`. */
  readonly contract?: string;
}

/** How the browser answered an interaction request. */
export type MockInteractionReply =
  | { readonly kind: "answered"; readonly output: unknown }
  | { readonly kind: "cancelled" }
  | { readonly kind: "failed" }
  /** No mounted client handles the interaction, so .NET would use its fallback. */
  | { readonly kind: "unhandled" };

export interface MockInteractionOptions {
  /** Cancels the request, as .NET does when its interaction is cancelled. */
  readonly signal?: AbortSignal;
}

/** Which requests `failNext` answers: a request kind of a route, optionally of one member. */
export interface MockRequestMatch {
  readonly route: string;
  /** The request kind, such as `set`, `command`, `can`, `write` or `start`. */
  readonly kind: string;
  readonly member?: string;
}

export interface MockView {
  /** The route, such as `counter` or `content1`. */
  readonly route: string;
  /** The current wire state. */
  readonly state: MockState;
  /** The revision of the current state. */
  readonly revision: number;
  /**
   * Merges a patch and publishes it like a producer: a frame with a `set` change for each member whose value
   * changed, at the window's next revision.
   */
  update(patch: MockState | ((state: MockState) => MockState)): void;
  /** Pushes a raw message to the client, such as a stale or malformed frame. `route` defaults to this route. */
  push(message: Readonly<Record<string, unknown>>): void;
  /**
   * Pushes a `failure`: the producer cannot publish this route for now, so the
   * client keeps its last state and reports the error.
   */
  pushFailure(failure: { readonly kind?: BridgeErrorKind; readonly message: string; readonly detail?: BridgeFailureDetail }): void;
  /** Edits a field declared in `collections`. */
  collection(field: string): MockCollection;
  /** Publishes the collection edits and updates made by `edit` as one frame. */
  batch(edit: () => void): void;
  /** Answers the next `times` requests of `kind` (and `member`) of this route with `failure`. */
  failNext(match: { readonly kind: string; readonly member?: string }, failure: MockFailure, times?: number): void;
  /** Operations started on this View, oldest first, optionally of one command member. */
  operations(member?: string): readonly MockOperation[];
  /** Interaction names that an open presentation currently handles. */
  readonly interactionHandlers: readonly string[];
  /** Pushes an interaction request to the presentation that handles it, as a producer does. */
  interact(name: string, input: unknown, options?: MockInteractionOptions): Promise<MockInteractionReply>;
}

/** One request the client sent, in order. */
export interface MockCall {
  readonly kind: string;
  readonly route?: string;
  readonly member?: string;
  /** The whole request. */
  readonly message: Readonly<Record<string, unknown>>;
}

/**
 * Answers a request before the mock does: return a reply object to answer it (as `{ ok: true, … }` or
 * `{ ok: false, error }`), or `undefined` to let the mock answer. A throw makes the request reject, like a dropped
 * transport.
 */
export type MockInterceptor = (request: MockCall) =>
  Readonly<Record<string, unknown>> | undefined | Promise<Readonly<Record<string, unknown>> | undefined>;

/** An in-memory `__runicBridge`: a Views protocol 4 producer for generated clients without .NET. */
export interface MockBridge extends RunicBridgeClient {
  /** Every request in order, including `hello` and rejected ones. */
  readonly calls: readonly MockCall[];
  /** The virtual time in milliseconds, which only `advance()` moves. */
  readonly now: number;
  /** Queued requests, replies, pushes and timers that are due now or later. */
  readonly pending: number;
  /** Serves a View on `route`: `open`, `close`, `snapshot`, `set`, `write`, `command`, `can` and operations. */
  view(route: string, definition: MockViewDefinition): MockView;
  /** Answers requests before the mock does, until the returned function is called. */
  intercept(handler: MockInterceptor): () => void;
  /** Pushes a message to the connected page, such as a `menu-bar` push. */
  push(message: Readonly<Record<string, unknown>>): void;
  /** Answers the next `times` requests that match with `failure`. */
  failNext(match: MockRequestMatch, failure: MockFailure, times?: number): void;
  /**
   * Rejects further requests until `reconnect()` and reports the loss to `onConnectionChange` listeners. Like a
   * producer when a connection closes, it closes the connection's presentations and watches, and a later `hello`
   * with its session answers `released`.
   */
  disconnect(): void;
  /** Reports a new connection to `onConnectionChange` listeners, as a host transport does after a reconnect. */
  reconnect(): void;
  /** Resolves after `milliseconds` of virtual time, for handlers that model latency. */
  sleep(milliseconds: number): Promise<void>;
  /** Delivers every queued request, reply and push that is due, until the client is idle. */
  flush(): Promise<void>;
  /** Flushes until `promise` settles, then returns its result. Fails when it cannot settle without time passing. */
  flushUntil<T>(promise: Promise<T>): Promise<T>;
  /** Moves the virtual clock, delivering timed work in order. */
  advance(milliseconds: number): Promise<void>;
}

/** Creates a mock producer. Install it with {@link installMockBridge}. */
/* ------------------------------------------------------------------ internals */

// Captured before a test can install fake timers, so flush() still yields.
const realSetTimeout = globalThis.setTimeout.bind(globalThis);
const yieldToEventLoop = () => new Promise<void>(resolve => { realSetTimeout(resolve, 0); });
const maximumRounds = 10_000;
const mockImplementation = { name: "@runic-artifex/views/mock", version: viewsVersion };

const upperFirst = (value: string) => value.charAt(0).toUpperCase() + value.slice(1);
const isRecord = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);
const isSafe = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= 0;

/**
 * The canonical form of a JSON value (RFC 8785) in JavaScript: scalars and member names as `JSON.stringify` writes
 * them, members sorted by UTF-16 code units. Sorting a plain object's keys and stringifying it is not enough,
 * because JavaScript enumerates integer-like keys first.
 */
export function canonicalJson(value: unknown): string {
  if (value === null || typeof value !== "object") return JSON.stringify(value) ?? "null";
  if (Array.isArray(value)) return `[${value.map(item => item === undefined ? "null" : canonicalJson(item)).join(",")}]`;
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record).filter(key => record[key] !== undefined).sort();
  return `{${keys.map(key => `${JSON.stringify(key)}:${canonicalJson(record[key])}`).join(",")}}`;
}
const sameJson = (left: unknown, right: unknown) => canonicalJson(left) === canonicalJson(right);

function failureFromError(error: unknown): { kind: string; message: string; detail?: BridgeFailureDetail; reason?: string } {
  const kind = typeof (error as { kind?: unknown })?.kind === "string" ? (error as { kind: string }).kind : "failed";
  const reason = (error as { reason?: unknown } | null | undefined)?.reason;
  // Like a producer in development, the reply carries the thrown error's detail.
  const detail = error instanceof Error
    ? { type: error.name, message: error.message, ...(typeof error.stack === "string" ? { stack: error.stack } : {}) }
    : undefined;
  return { kind, message: error instanceof Error ? error.message : String(error), ...(detail ? { detail } : {}),
    ...(typeof reason === "string" ? { reason } : {}) };
}

interface Deferred<T> { readonly promise: Promise<T>; resolve(value: T): void }
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

type Reply = Readonly<Record<string, unknown>>;
const ok = (members: Record<string, unknown> = {}): Reply => ({ ok: true, ...members });
const failed = (kind: string, message: string, members: Record<string, unknown> = {}): Reply => ({ ok: false, error: { kind, message, ...members } });
const invalidRequest = (message: string) => failed("rejected", message, { reason: "invalid-request" });
const released = () => failed("released", "This View was released.");

interface OperationRecord {
  readonly member: string;
  readonly operation: string;
  readonly key: string;
  readonly input: unknown;
  readonly inputJson: string;
  readonly view: MockRegisteredView;
  readonly stream: boolean;
  readonly controller: AbortController;
  readonly items: unknown[];
  /** Connections that watch it. */
  readonly watchers: Set<number>;
  kind: MockOperationKind;
  progress?: number;
  revision?: number;
  result?: unknown;
  failure?: unknown;
  error?: { readonly message: string; readonly detail?: BridgeFailureDetail };
  handle: MockOperation;
}

interface InteractionRequestRecord {
  readonly requestId: string;
  readonly name: string;
  readonly contract: string;
  readonly presentation: Presentation;
  readonly reply: Deferred<MockInteractionReply>;
}

interface Presentation {
  readonly id: string;
  readonly connection: number;
  handlers: { readonly name: string; readonly contract: string }[];
  generation: number;
}

/** The mock's state of one View route. */
interface MockRegisteredView {
  readonly route: string;
  readonly definition: MockViewDefinition;
  readonly contract: string;
  readonly fieldVersions: Map<string, number>;
  readonly presentations: Map<string, Presentation>;
  readonly requests: Map<string, InteractionRequestRecord>;
  /** Checked-write receipts by request id, for idempotent retries. */
  readonly writes: Map<string, { readonly request: string; readonly receipt: Record<string, unknown> }>;
  /** Indexed changes of the publication unit in progress. */
  readonly pending: MockCollectionChange[];
  /** Inside `batch`: publication waits until it ends. */
  batching: boolean;
  state: MockState;
  /** The state as last published, which the next publication compares members with. */
  published: MockState;
  revision: number;
  /** The revision of the last publication that changed each text member. */
  readonly textChanged: Map<string, number>;
  handle: MockView;
}

interface Connection {
  readonly id: number;
  session: string | undefined;
  /** The highest admitted edit per route and member. */
  readonly edits: Map<string, number>;
  /** The text value of the highest admitted edit of a text member, on a connection that uses text-splice/1. */
  readonly editTexts: Map<string, string | undefined>;
  /** The features in use on the connection. */
  features: ReadonlySet<string>;
  /** Requests that wait for the connection's hello, in arrival order: each resolves with whether it succeeded. */
  readonly held: ((succeeded: boolean) => void)[];
  readonly pending: Set<(error: Error) => void>;
}

interface Task { readonly due: number; readonly sequence: number; readonly run: () => void }

const newConnection = (id: number): Connection =>
  ({ id, session: undefined, edits: new Map(), editTexts: new Map(), features: new Set(), held: [], pending: new Set() });

export function createMockBridge(options: MockBridgeOptions = {}): MockBridge {
  const manual = options.scheduling === "manual";
  const reorder = options.reorderRequests === true;
  const latency = options.latency;
  if (typeof latency === "number" && (!Number.isFinite(latency) || latency < 0) || typeof latency === "object"
    && (!Number.isFinite(latency.min) || !Number.isFinite(latency.max) || latency.min < 0 || latency.max < latency.min))
    throw new RangeError("The mock latency must be non-negative milliseconds, a { min, max } range or \"slow\".");
  const sample = (): number => {
    if (latency === undefined) return 0;
    if (typeof latency === "number") return latency;
    const { min, max } = latency === "slow" ? { min: 30, max: 120 } : latency;
    return min + Math.random() * (max - min);
  };
  const views = new Map<string, MockRegisteredView>();
  const operations = new Map<string, OperationRecord>();
  const interceptors = new Set<MockInterceptor>();
  const failures: { readonly match: MockRequestMatch; readonly failure: MockFailure }[] = [];
  const connectionListeners = new Set<(connected: boolean) => void>();
  const messageListeners = new Set<(message: string) => void>();
  const closedSessions = new Set<string>();
  const calls: MockCall[] = [];
  const tasks: Task[] = [];
  let connected = true;
  let connectionCount = 1;
  let connection: Connection = newConnection(1);
  let revision = 0;
  let now = 0;
  let sequence = 0;
  let interactionSequence = 0;
  // The page channel is FIFO: a reply or push is never delivered before one sent earlier.
  let outboundDue = 0;
  let inboundDue = 0;
  let outboundChain: Promise<void> = Promise.resolve();
  let inboundChain: Promise<void> = Promise.resolve();
  let reorderQueue: (() => void)[] | undefined;

  const schedule = (due: number, run: () => void, order = sequence++) => {
    const task = { due, sequence: order, run };
    tasks.push(task);
    return task;
  };
  function takeDue(): Task | undefined {
    let best = -1;
    for (let index = 0; index < tasks.length; index++) {
      const task = tasks[index]!;
      if (task.due > now) continue;
      const current = best < 0 ? undefined : tasks[best]!;
      if (!current || task.due < current.due || task.due === current.due && task.sequence < current.sequence) best = index;
    }
    return best < 0 ? undefined : tasks.splice(best, 1)[0];
  }
  const realDelay = (milliseconds: number) => new Promise<void>(resolve => { realSetTimeout(resolve, Math.max(0, milliseconds)); });

  /** Sends a reply or push to the page over the FIFO channel; dropped when its connection closed meanwhile. */
  function toPage(run: () => void): void {
    const owner = connection.id;
    const send = () => { if (connected && connection.id === owner) run(); };
    if (manual) {
      outboundDue = Math.max(now + sample(), outboundDue);
      schedule(outboundDue, send);
    } else if (latency === undefined) send();
    else {
      const wait = sample();
      outboundChain = outboundChain.then(() => realDelay(wait)).then(send);
    }
  }

  /** Admits a request at the producer: in order, or out of order with `reorderRequests`. */
  function toProducer(run: () => void): void {
    if (manual) {
      const due = reorder ? now + sample() : (inboundDue = Math.max(now + sample(), inboundDue));
      // Requests due together are admitted newest first.
      schedule(due, run, reorder ? -sequence++ : sequence++);
    } else if (reorder) {
      if (latency !== undefined) { void realDelay(sample()).then(run); return; }
      if (reorderQueue) { reorderQueue.push(run); return; }
      reorderQueue = [run];
      realSetTimeout(() => {
        const queued = reorderQueue!;
        reorderQueue = undefined;
        for (const next of queued.reverse()) next();
      }, 0);
    } else if (latency === undefined) run();
    else {
      const wait = sample();
      inboundChain = inboundChain.then(() => realDelay(wait)).then(run);
    }
  }

  function pushMessage(message: Readonly<Record<string, unknown>>, requiresHello = true): void {
    if (requiresHello && connection.session === undefined) return;
    const text = JSON.stringify(message);
    toPage(() => { for (const listener of [...messageListeners]) listener(text); });
  }

  /* --------------------------------------------------------------- publication */

  const fieldsOf = (view: MockRegisteredView) => view.definition.checkedFields?.length
    ? { fields: Object.fromEntries(view.definition.checkedFields.map(field => [field, { version: view.fieldVersions.get(field) ?? 0 }])) }
    : {};

  /** Publishes what changed since the last publication as one frame at the window's next revision. */
  function publish(view: MockRegisteredView): void {
    if (view.batching) return;
    const indexed = view.pending.splice(0);
    const indexedFields = new Set(indexed.map(change => change.field));
    const changes: Record<string, unknown>[] = [];
    const splicing = connection.features.has(textSpliceFeature);
    const texts: string[] = [];
    for (const field of new Set([...Object.keys(view.published), ...Object.keys(view.state)])) {
      if (indexedFields.has(field) || sameJson(view.published[field], view.state[field])) continue;
      const checked = view.definition.checkedFields?.includes(field) === true;
      if (checked) view.fieldVersions.set(field, (view.fieldVersions.get(field) ?? 0) + 1);
      const version = checked ? { version: view.fieldVersions.get(field) } : {};
      const previous = view.published[field];
      const value = view.state[field];
      const text = view.definition.texts?.includes(field) === true;
      if (text) texts.push(field);
      // text-splice/1: a text member changes by splices when they keep enough of its value.
      const splices = text && splicing && typeof previous === "string" && typeof value === "string"
        ? producerSplices(previous, value) : undefined;
      if (splices === undefined) {
        changes.push({ field, kind: "set", value: value ?? null, ...version });
        continue;
      }
      let length = (previous as string).length;
      for (const splice of splices) {
        changes.push({ field, kind: "splice", length, index: splice.index, delete: splice.delete, text: splice.text, ...version });
        length += splice.text.length - splice.delete;
      }
    }
    // A unit of more than 4,096 changes publishes the collections as sets of their fields.
    if (changes.length + indexed.length > 4096) {
      for (const field of indexedFields) changes.push({ field, kind: "set", value: view.state[field] as unknown });
      indexed.length = 0;
    }
    view.published = view.state;
    if (changes.length + indexed.length === 0) return;
    const base = view.revision;
    view.revision = ++revision;
    for (const field of texts) view.textChanged.set(field, view.revision);
    pushMessage({ kind: "frame", route: view.route, baseRevision: base, revision: view.revision, changes: [...indexed, ...changes] });
  }

  function commit(view: MockRegisteredView, next: MockState): void {
    view.state = next;
    publish(view);
  }

  function collectionOf(view: MockRegisteredView, field: string): MockCollection {
    const key = view.definition.collections?.[field];
    if (!key) throw new RangeError(`The mock View ${view.route} declares no collection ${field}.`);
    const rows = () => {
      const value = view.state[field];
      if (!Array.isArray(value)) throw new TypeError(`The mock View ${view.route} field ${field} is not an array.`);
      return value as unknown[];
    };
    const indexOf = (target: string) => {
      const at = rows().findIndex(item => key(item) === target);
      if (at < 0) throw new RangeError(`The mock collection ${field} has no row with key ${target}.`);
      return at;
    };
    // An edit changes the state and publishes the indexed change instead of a set of the field.
    const edit = (next: unknown[], change: MockCollectionChange) => {
      view.state = { ...view.state, [field]: next };
      view.pending.push(change);
      publish(view);
    };
    return {
      get items() { return rows(); },
      get keys() { return rows().map(key); },
      add(items, index) {
        const added = (Array.isArray(items) ? [...items] : [items]) as unknown[];
        if (added.length === 0) return;
        const next = [...rows()];
        const at = index ?? next.length;
        if (!Number.isSafeInteger(at) || at < 0 || at > next.length) throw new RangeError("The insert index is outside the collection.");
        next.splice(at, 0, ...added);
        const keys = next.map(key);
        if (new Set(keys).size !== keys.length) throw new RangeError(`The mock collection ${field} would contain a duplicate key.`);
        edit(next, { field, kind: "add", index: at, oldIndex: -1, keys: added.map(key), items: added });
      },
      remove(target) {
        const at = indexOf(target);
        const next = [...rows()];
        next.splice(at, 1);
        edit(next, { field, kind: "remove", index: at, oldIndex: at, keys: [target], items: [] });
      },
      replace(target, item) {
        const at = indexOf(target);
        if (key(item) !== target && rows().some(row => key(row) === key(item)))
          throw new RangeError(`The mock collection ${field} would contain a duplicate key.`);
        const next = [...rows()];
        next[at] = item;
        edit(next, { field, kind: "replace", index: at, oldIndex: at, keys: [target], items: [item] });
      },
      move(target, index) {
        const from = indexOf(target);
        const next = [...rows()];
        if (!Number.isSafeInteger(index) || index < 0 || index >= next.length) throw new RangeError("The move index is outside the collection.");
        const [moved] = next.splice(from, 1);
        next.splice(index, 0, moved);
        edit(next, { field, kind: "move", index, oldIndex: from, keys: [target], items: [] });
      },
    };
  }

  /* --------------------------------------------------------------- operations */

  function statusOf(record: OperationRecord | undefined): Record<string, unknown> {
    if (!record) return { kind: "unknown" };
    if (record.kind === "running") return { kind: "running", ...(record.progress === undefined ? {} : { progress: record.progress }) };
    if (record.kind === "expired" || record.kind === "unknown") return { kind: record.kind };
    return {
      kind: record.kind, revision: record.revision ?? record.view.revision,
      ...(record.kind === "failed" ? { error: { kind: "failed", message: record.error?.message ?? "The operation failed.",
        ...(record.error?.detail ? { detail: record.error.detail } : {}) } } : {}),
      ...(record.kind === "succeeded" && record.result !== undefined ? { result: record.result } : {}),
      ...(record.kind === "domain-failed" ? { failure: record.failure } : {}),
      ...(record.stream && record.kind !== "cancelled" ? { stream: true } : {}),
    };
  }

  function notifyWatchers(record: OperationRecord): void {
    if (!record.watchers.has(connection.id)) return;
    pushMessage({ kind: "operation", route: record.view.route, member: record.member, operation: record.operation,
      contract: record.view.contract, status: statusOf(record) });
  }

  const executing = (member: string) => `is${upperFirst(member)}Executing`;

  function settle(record: OperationRecord, kind: Exclude<MockOperationKind, "running">, result?: unknown, error?: OperationRecord["error"]): void {
    if (record.kind !== "running") return;
    const view = record.view;
    record.kind = kind;
    if (kind === "domain-failed") record.failure = result;
    else if (result !== undefined) record.result = result;
    if (error) record.error = error;
    if (kind === "cancelled") record.controller.abort();
    const flag = executing(record.member);
    if (Object.hasOwn(view.state, flag) && view.state[flag] === true &&
      ![...operations.values()].some(other => other !== record && other.view === view && other.member === record.member && other.kind === "running"))
      commit(view, { ...view.state, [flag]: false });
    record.revision = view.revision;
    notifyWatchers(record);
    record.watchers.clear();
  }

  function admit(view: MockRegisteredView, member: string, operation: string, input: unknown, declared?: { readonly failure: unknown }): Reply {
    const key = `${view.contract}\u0000${view.route}\u0000${operation}`;
    const inputJson = canonicalJson(input ?? null);
    const existing = operations.get(key);
    if (existing) {
      // An operation id names one operation: repeating it observes that operation, reusing it for other work conflicts.
      if (existing.member !== member || existing.inputJson !== inputJson)
        return ok({ revision: view.revision, admission: "rejected", reason: "identity-conflict" });
      if (existing.kind === "expired") return ok({ revision: view.revision, admission: "expired" });
      return ok({ revision: view.revision, admission: "duplicate", status: statusOf(existing) });
    }
    const refusal = unavailable(view, member, input);
    if (refusal) return ok({ revision: view.revision, admission: "rejected", reason: refusal.reason });
    const handler = view.definition.operations?.[member];
    const record: OperationRecord = {
      member, operation, key, input, inputJson, view, stream: view.definition.streams?.includes(member) ?? false,
      controller: new AbortController(), items: [], watchers: new Set(), kind: "running", handle: undefined as unknown as MockOperation,
    };
    record.handle = {
      get member() { return record.member; },
      get requestId() { return record.operation; },
      get input() { return record.input; },
      get kind() { return record.kind; },
      get signal() { return record.controller.signal; },
      get items() { return record.items; },
      get progress() { return record.progress; },
      reportProgress(value) {
        if (record.kind !== "running" || typeof value !== "number" || Number.isNaN(value)) return;
        record.progress = Math.min(1, Math.max(0, value));
        notifyWatchers(record);
      },
      succeed: result => settle(record, "succeeded", result),
      fail: (message, detail) => settle(record, "failed", undefined, { message: message ?? "The operation failed.", ...(detail ? { detail } : {}) }),
      failWith: failure => settle(record, "domain-failed", failure),
      cancel: () => settle(record, "cancelled"),
      expire: () => {
        if (record.kind === "running") throw new TypeError(`The operation ${member} is running; only a finished operation expires.`);
        if (record.kind === "expired" || record.kind === "unknown") return;
        record.kind = "expired";
        delete record.result;
        delete record.failure;
        delete record.error;
        record.items.length = 0;
      },
      forget: () => {
        if (record.kind === "unknown") return;
        if (record.kind === "running") settle(record, "unknown");
        else record.kind = "unknown";
        record.controller.abort();
        if (operations.get(key) === record) operations.delete(key);
      },
      emit: (...items) => {
        if (!record.stream) throw new TypeError(`The operation ${member} is not a stream.`);
        if (record.kind !== "running") throw new TypeError(`The operation ${member} has finished.`);
        record.items.push(...items);
      },
    };
    operations.set(key, record);
    const flag = executing(member);
    // The admission reply follows the publication of the command's execution state.
    if (Object.hasOwn(view.state, flag) && view.state[flag] !== true) commit(view, { ...view.state, [flag]: true });
    if (declared) settle(record, "domain-failed", declared.failure);
    else if (handler !== "manual") {
      const command = view.definition.commands?.[member];
      void (async () => {
        try {
          let outcome: MockOperationOutcome | void = undefined;
          if (handler) outcome = await handler(view.state, record.input, record.handle);
          else if (command) {
            const patch = await command(view.state, record.input);
            outcome = isRecord(patch) ? { state: patch } : undefined;
          }
          if (record.kind !== "running") return;
          if (outcome?.state) commit(view, { ...view.state, ...outcome.state });
          settle(record, "succeeded", outcome?.result);
        } catch (error) {
          // A declared failure is classified by its type, also after a cancel request.
          const declaredFailure = mockFailureOf(error);
          if (declaredFailure) { settle(record, "domain-failed", declaredFailure.failure); return; }
          const failure = failureFromError(error);
          // Cancellation is cooperative: a handler that stops by throwing after the request ends cancelled.
          const cancelled = failure.kind === "cancelled" || record.controller.signal.aborted;
          settle(record, cancelled ? "cancelled" : "failed", undefined,
            { message: failure.message, ...(failure.detail ? { detail: failure.detail } : {}) });
        }
      })();
    }
    return ok({ revision: view.revision, admission: "accepted", status: statusOf(record) });
  }

  function operationRequest(view: MockRegisteredView, kind: string, message: Record<string, unknown>): Reply {
    const operation = message["operation"];
    const member = message["member"];
    if (typeof operation !== "string" || typeof member !== "string") return invalidRequest("The operation request has no operation or member.");
    const found = operations.get(`${view.contract}\u0000${view.route}\u0000${operation}`);
    // An operation started for another member is unknown to this one.
    const record = found?.member === member && found.kind !== "unknown" ? found : undefined;
    switch (kind) {
      case "status": return ok({ status: statusOf(record) });
      case "watch":
        if (record?.kind === "running") record.watchers.add(connection.id);
        return ok({ status: statusOf(record) });
      case "unwatch":
        record?.watchers.delete(connection.id);
        return ok({ status: statusOf(record) });
      case "cancel": {
        if (!record) return ok({ cancellation: "unknown" });
        if (record.kind === "expired") return ok({ cancellation: "expired" });
        if (record.kind !== "running") return ok({ cancellation: "not-running" });
        // The operation sees the request through its signal and decides how it ends.
        record.controller.abort();
        return ok({ cancellation: "cancellation-requested" });
      }
      default: {
        const cursor = isSafe(message["cursor"]) ? message["cursor"] : 0;
        if (!record || record.kind === "expired") return ok({ page: { kind: record ? "expired" : "unknown", cursor, completed: true, items: [] } });
        return ok({ page: {
          kind: record.kind, cursor: record.items.length, completed: record.kind !== "running",
          items: record.items.map((value, index) => ({ sequence: index + 1, value })).filter(item => item.sequence > cursor),
        } });
      }
    }
  }

  /* ------------------------------------------------------------- interactions */

  function endPresentation(view: MockRegisteredView, presentation: Presentation): void {
    if (view.presentations.get(presentation.id) === presentation) view.presentations.delete(presentation.id);
    for (const request of [...view.requests.values()]) {
      if (request.presentation !== presentation) continue;
      view.requests.delete(request.requestId);
      request.reply.resolve({ kind: "cancelled" });
    }
  }

  function interact(view: MockRegisteredView, name: string, input: unknown, options: MockInteractionOptions = {}): Promise<MockInteractionReply> {
    const presentation = [...view.presentations.values()].find(item => item.connection === connection.id && connected
      && item.handlers.some(handler => handler.name === name));
    if (!presentation || options.signal?.aborted) return Promise.resolve({ kind: presentation ? "cancelled" : "unhandled" });
    const handler = presentation.handlers.find(item => item.name === name)!;
    const request: InteractionRequestRecord = {
      requestId: `interaction-${++interactionSequence}`, name, contract: handler.contract, presentation, reply: deferred<MockInteractionReply>(),
    };
    view.requests.set(request.requestId, request);
    options.signal?.addEventListener("abort", () => {
      if (!view.requests.delete(request.requestId)) return;
      pushMessage({ kind: "interaction-cancelled", route: view.route, presentation: presentation.id, requestId: request.requestId, ownerEpoch: 1,
        message: "The interaction was cancelled." });
      request.reply.resolve({ kind: "cancelled" });
    }, { once: true });
    pushMessage({
      kind: "interaction", route: view.route, presentation: presentation.id, requestId: request.requestId, ownerEpoch: 1, name,
      contract: handler.contract, expiresAt: new Date(Date.now() + 120_000).toISOString().replace(/\.\d{3}Z$/, "Z"), input,
    });
    return request.reply.promise;
  }

  /* ------------------------------------------------------------ requests */

  function available(view: MockRegisteredView, member: string, input: unknown): boolean {
    const check = view.definition.can?.[member];
    if (check) return check(view.state, input);
    return view.state[`can${upperFirst(member)}`] !== false;
  }

  /** Why a command cannot run now, as the reason of a rejection, or undefined when it can. */
  function unavailable(view: MockRegisteredView, member: string, input: unknown): { readonly message: string; readonly reason: string } | undefined {
    try { return available(view, member, input) ? undefined : { message: `${member} is unavailable.`, reason: "unavailable" }; }
    catch (error) {
      const failure = failureFromError(error);
      return { message: failure.message, reason: failure.reason ?? "unavailable" };
    }
  }

  function errorReply(view: MockRegisteredView, error: unknown, ran: boolean): Reply {
    const declared = mockFailureOf(error);
    if (declared) return { ok: false, revision: view.revision, error: { kind: "domain-failed", message: (error as Error).message, failure: declared.failure } };
    const failure = failureFromError(error);
    // A request that ran names its revision; a rejected one ran nothing.
    return { ok: false, ...(ran && failure.kind !== "rejected" ? { revision: view.revision } : {}), error: failure };
  }

  async function writeChecked(view: MockRegisteredView, message: Record<string, unknown>): Promise<Reply> {
    const field = message["member"] as string;
    if (!view.definition.checkedFields?.includes(field)) return invalidRequest(`${field} is not a checked field.`);
    if (typeof message["requestId"] !== "string" || !isSafe(message["expectedVersion"]) || !Object.hasOwn(message, "value"))
      return invalidRequest(`${field} has an invalid checked write.`);
    const reply = (receipt: Record<string, unknown>) => ok({ revision: view.revision, receipt });
    // A retried request id returns its first receipt, and reusing it for another write is a conflict.
    const key = `${field}\u0000${message["requestId"]}`;
    const request = canonicalJson([message["expectedVersion"], message["expectedValue"], message["value"]]);
    const previous = view.writes.get(key);
    const version = () => view.fieldVersions.get(field) ?? 0;
    if (previous) return reply(previous.request === request ? previous.receipt
      : { kind: "conflict", incoming: { value: view.state[field], version: version() },
        message: "The request identity was already used with a different checked write." });
    const retain = (receipt: Record<string, unknown>) => { view.writes.set(key, { request, receipt }); return reply(receipt); };
    if (message["expectedVersion"] !== version() || !sameJson(message["expectedValue"], view.state[field]))
      return retain({ kind: "conflict", incoming: { value: view.state[field], version: version() }, message: "The field baseline no longer matches." });
    try {
      const patch = await view.definition.set?.[field]?.(view.state, message["value"]);
      commit(view, { ...view.state, [field]: message["value"], ...(patch ?? {}) });
    } catch (error) {
      const failure = failureFromError(error);
      // A handler error with kind "committed-with-error" keeps the value, as a setter that ran before its validation failed does.
      if (failure.kind !== "committed-with-error") return retain({ kind: "rejected", message: failure.message });
      commit(view, { ...view.state, [field]: message["value"] });
      return retain({ kind: "committed-with-error", snapshot: { value: view.state[field], version: version() }, message: failure.message });
    }
    return retain({ kind: "applied", snapshot: { value: view.state[field], version: version() } });
  }

  function injected(view: MockRegisteredView, kind: string, message: Record<string, unknown>, failure: MockFailure): Reply {
    if (failure.kind === "domain-failed") {
      if (kind === "start")
        return admit(view, message["member"] as string, message["operation"] as string, message["input"], { failure: failure.failure });
      return { ok: false, revision: view.revision, error: { kind: "domain-failed", message: failure.message ?? `${String(message["member"])} failed.`, failure: failure.failure } };
    }
    const text = failure.message ?? `The mock failed ${kind} ${String(message["member"] ?? "")} of ${view.route}.`;
    if (failure.kind === "transport") throw new Error(text);
    if (kind === "start" && failure.kind === "rejected")
      return ok({ revision: view.revision, admission: "rejected", reason: failure.reason ?? "unavailable" });
    return failed(failure.kind, text, { ...(failure.detail ? { detail: failure.detail } : {}), ...(failure.reason === undefined ? {} : { reason: failure.reason }) });
  }

  /** Resolves with whether the connection's hello succeeded, or false after 5 seconds without one. */
  function helloOf(owner: Connection): Promise<boolean> {
    return new Promise<boolean>(resolve => {
      let done = false;
      let stop = () => {};
      const settle = (succeeded: boolean) => {
        if (done) return;
        done = true;
        stop();
        resolve(succeeded);
      };
      owner.held.push(settle);
      const expire = () => {
        const at = owner.held.indexOf(settle);
        if (at >= 0) owner.held.splice(at, 1);
        settle(false);
      };
      if (manual) {
        const task = schedule(now + 5_000, expire);
        stop = () => { const at = tasks.indexOf(task); if (at >= 0) tasks.splice(at, 1); };
      } else {
        const timer = realSetTimeout(expire, 5_000);
        stop = () => clearTimeout(timer);
      }
    });
  }

  const noHello = () => failed("rejected", "The connection has not completed hello.", { reason: "no-hello" });

  /** A hello that failed: the requests that waited for it answer no-hello. */
  function refuseHeld(reply: Reply): Reply {
    for (const resume of connection.held.splice(0)) resume(false);
    return reply;
  }

  async function answer(message: Record<string, unknown>, call: MockCall): Promise<Reply> {
    for (const interceptor of [...interceptors]) {
      const reply = await interceptor(call);
      if (reply === undefined) continue;
      // An intercepted hello ends the wait of the requests sent with it.
      if (call.kind === "hello" && reply["ok"] !== true) refuseHeld(reply as Reply);
      else if (call.kind === "hello" && connection.session === undefined && typeof message["session"] === "string") {
        connection.session = message["session"];
        for (const resume of connection.held.splice(0)) resume(true);
      }
      return reply;
    }
    const kind = call.kind;
    if (kind === "hello") {
      const identity = { protocol: 4, implementation: mockImplementation };
      if (message["protocol"] !== 4) return refuseHeld({ ok: false, ...identity, error: { kind: "failed", reason: "protocol-mismatch",
        message: `The mock implements Views protocol 4, but the client sent protocol ${String(message["protocol"])}.` } });
      const client = message["client"];
      if (connection.session !== undefined || typeof message["session"] !== "string" || !isRecord(client)
        || typeof client["name"] !== "string" || client["name"].length < 1 || client["name"].length > 128
        || typeof client["version"] !== "string" || client["version"].length < 1 || client["version"].length > 64)
        return refuseHeld({ ...invalidRequest("A second or invalid hello."), ...identity });
      const listed = message["features"];
      if (listed !== undefined && (!Array.isArray(listed) || listed.length > 64
        || listed.some(feature => typeof feature !== "string" || feature.length > 64)))
        return refuseHeld({ ...invalidRequest("The hello's features are not an array of at most 64 strings."), ...identity });
      if (closedSessions.has(message["session"])) return refuseHeld({ ...released(), ...identity });
      connection.session = message["session"];
      connection.features = new Set((listed as string[] | undefined ?? []).filter(feature => feature === textSpliceFeature));
      // Requests that waited for the hello are admitted in the order they arrived.
      for (const resume of connection.held.splice(0)) resume(true);
      return ok({ ...identity, features: [textSpliceFeature] });
    }
    if (connection.session === undefined) return noHello();
    if (kind === "receive") return ok({ messages: [] });
    const route = message["route"];
    if (typeof route !== "string") return invalidRequest("The request has no route.");
    const view = views.get(route);
    if (!view) return released();
    const member = typeof message["member"] === "string" ? message["member"] : undefined;
    const failureIndex = failures.findIndex(item => item.match.route === route && item.match.kind === kind
      && (item.match.member === undefined || item.match.member === member));
    if (failureIndex >= 0) return injected(view, kind, message, failures.splice(failureIndex, 1)[0]!.failure);
    const state = () => ({ revision: view.revision, state: view.state, ...fieldsOf(view) });
    switch (kind) {
      case "open": case "close": {
        const id = message["presentation"];
        if (typeof id !== "string") return invalidRequest("The request has no presentation.");
        const existing = view.presentations.get(id);
        if (kind === "close") {
          if (existing && existing.connection !== connection.id) return ok({ presentation: "ignored" });
          if (existing) endPresentation(view, existing);
          return ok({ presentation: "ok" });
        }
        if (existing && existing.connection !== connection.id) return ok({ ...state(), presentation: "ignored" });
        const handlers = Array.isArray(message["handlers"]) ? message["handlers"] as Presentation["handlers"] : undefined;
        view.presentations.set(id, existing ?? { id, connection: connection.id, handlers: handlers ?? [], generation: 0 });
        return ok({ ...state(), presentation: "ok", ...(handlers === undefined ? {} : { registration: "ok" }) });
      }
      case "snapshot": return ok(state());
      case "set": {
        const splice = message["splice"];
        if (member === undefined || Object.hasOwn(message, "value") === (splice !== undefined) || !isSafe(message["edit"]) || message["edit"] === 0)
          return invalidRequest("The set request is invalid.");
        const handler = view.definition.set?.[member];
        if (!handler && !Object.hasOwn(view.state, member)) return invalidRequest(`${route} has no member ${member}.`);
        const text = view.definition.texts?.includes(member) === true && connection.features.has(textSpliceFeature);
        if (splice !== undefined && (!text || !isRecord(splice))) return invalidRequest(`${route} has no text member ${member} that accepts splices.`);
        const key = `${route}\u0000${member}`;
        if (message["edit"] <= (connection.edits.get(key) ?? 0)) return ok({ revision: view.revision, superseded: true });
        let value = message["value"];
        if (isRecord(splice)) {
          // The base is the connection's latest admitted edit, or the published value at a revision no later
          // publication changed; without it, nothing is admitted and the client sends the value in full.
          const baseEdit = splice["baseEdit"];
          const baseRevision = splice["baseRevision"];
          const base = isSafe(baseEdit) ? (connection.edits.get(key) === baseEdit ? connection.editTexts.get(key) : undefined)
            : isSafe(baseRevision) && baseRevision <= view.revision && baseRevision >= (view.textChanged.get(member) ?? 0) ? view.published[member]
            : undefined;
          try { value = applySplice(base, splice); }
          catch { return failed("rejected", `The base of the splice of ${member} is not the mock's value; send the value.`, { reason: "splice-base" }); }
        }
        connection.edits.set(key, message["edit"]);
        if (text) connection.editTexts.set(key, typeof value === "string" ? value : undefined);
        try {
          const patch = await handler?.(view.state, value);
          commit(view, { ...view.state, [member]: value, ...(patch ?? {}) });
        } catch (error) { return errorReply(view, error, true); }
        return ok({ revision: view.revision });
      }
      case "write": return writeChecked(view, message);
      case "command": case "can": {
        if (member === undefined) return invalidRequest("The request has no member.");
        const handler = view.definition.commands?.[member];
        if (!handler && !view.definition.can?.[member]) return invalidRequest(`${route} has no command ${member}.`);
        const input = message["input"];
        const refusal = unavailable(view, member, input);
        if (kind === "can") return ok({ value: refusal === undefined });
        if (refusal) return failed("rejected", refusal.message, { reason: refusal.reason });
        try {
          const patch = await handler?.(view.state, input);
          if (isRecord(patch)) commit(view, { ...view.state, ...patch });
        } catch (error) { return errorReply(view, error, true); }
        return ok({ revision: view.revision });
      }
      case "start":
        if (member === undefined || typeof message["operation"] !== "string") return invalidRequest("The start request is invalid.");
        return admit(view, member, message["operation"], message["input"]);
      case "status": case "watch": case "unwatch": case "cancel": case "stream":
        return operationRequest(view, kind, message);
      case "interactions": {
        const presentation = typeof message["presentation"] === "string" ? view.presentations.get(message["presentation"]) : undefined;
        if (!presentation || presentation.connection !== connection.id) return ok({ registration: "ignored" });
        const generation = isSafe(message["generation"]) ? message["generation"] : -1;
        if (generation < presentation.generation) return ok({ registration: "stale" });
        presentation.generation = generation;
        presentation.handlers = Array.isArray(message["handlers"]) ? message["handlers"] as Presentation["handlers"] : [];
        // Requests of handlers that are gone end.
        for (const request of [...view.requests.values()])
          if (request.presentation === presentation && !presentation.handlers.some(handler => handler.name === request.name)) {
            view.requests.delete(request.requestId);
            request.reply.resolve({ kind: "cancelled" });
          }
        return ok({ registration: "ok" });
      }
      case "interaction-reply": {
        const request = typeof message["requestId"] === "string" ? view.requests.get(message["requestId"]) : undefined;
        if (!request || request.presentation.id !== message["presentation"]) return ok({ result: "stale" });
        view.requests.delete(request.requestId);
        const outcome = message["outcome"];
        request.reply.resolve(outcome === "answered" ? { kind: "answered", output: message["output"] }
          : outcome === "failed" ? { kind: "failed" } : { kind: "cancelled" });
        return ok({ result: "ok" });
      }
      default: return invalidRequest(`The request kind ${kind} is unknown.`);
    }
  }

  // Requests that change state are admitted one at a time, as a window's turns: each runs once the previous one
  // has answered. Reads, operation observation and interaction replies are not held, so a command that waits for
  // an interaction can receive its reply.
  let turn: Promise<unknown> = Promise.resolve();
  const turnKinds = new Set(["set", "write", "command", "start"]);
  function admitted(kind: string, run: () => Promise<Reply>): Promise<Reply> {
    if (!turnKinds.has(kind)) return run();
    const next = turn.then(run, run);
    turn = next.catch(() => undefined);
    return next;
  }

  async function flush(): Promise<void> {
    for (let round = 0; round < maximumRounds; round++) {
      for (let task = takeDue(); task; task = takeDue()) task.run();
      await yieldToEventLoop();
      if (!tasks.some(task => task.due <= now)) return;
    }
    throw new Error("The mock Bridge did not become idle: the client keeps requesting or the mock keeps pushing.");
  }

  function notifyConnection(value: boolean): void {
    for (const listener of [...connectionListeners]) listener(value);
  }

  const bridge: MockBridge = {
    calls,
    get now() { return now; },
    get pending() { return tasks.length; },
    isConnected: () => connected,
    request(text) {
      let message: Record<string, unknown>;
      try {
        const parsed = JSON.parse(text) as unknown;
        if (!isRecord(parsed)) throw new TypeError("A request is a JSON object.");
        message = parsed;
      } catch (error) { return Promise.reject(error); }
      const call: MockCall = {
        kind: typeof message["kind"] === "string" ? message["kind"] : "",
        ...(typeof message["route"] === "string" ? { route: message["route"] } : {}),
        ...(typeof message["member"] === "string" ? { member: message["member"] } : {}),
        message,
      };
      calls.push(call);
      if (!connected) return Promise.reject(new Error("The mock Bridge is disconnected."));
      const owner = connection;
      return new Promise<string>((resolve, reject) => {
        const drop = (error: Error) => reject(error);
        owner.pending.add(drop);
        const settle = (run: () => void) => toPage(() => { owner.pending.delete(drop); run(); });
        toProducer(() => {
          if (connection !== owner) return;
          const run = () => admitted(call.kind, () => answer(message, call)).then(
            reply => settle(() => resolve(JSON.stringify(reply))),
            error => settle(() => reject(error)));
          // A request that arrives before the hello waits for it, up to 5 seconds, and is then admitted in arrival
          // order; a receive never waits.
          if (call.kind === "hello" || call.kind === "receive" || owner.session !== undefined) { run(); return; }
          void helloOf(owner).then(succeeded => {
            if (connection !== owner) return;
            if (succeeded) run();
            else settle(() => resolve(JSON.stringify(noHello())));
          });
        });
      });
    },
    onMessage(listener) {
      messageListeners.add(listener);
      return () => { messageListeners.delete(listener); };
    },
    onConnectionChange(listener) {
      connectionListeners.add(listener);
      listener(connected);
      return () => { connectionListeners.delete(listener); };
    },
    view(route, definition) {
      if (!/^[A-Za-z0-9_-]{1,128}$/.test(route)) throw new RangeError("A mock route has 1 to 128 letters, digits, '_' or '-'.");
      const state = { ...definition.state };
      const view: MockRegisteredView = {
        route, definition, contract: definition.contract ?? `mock.${route}:mock`, fieldVersions: new Map(), presentations: new Map(),
        requests: new Map(), writes: new Map(), batching: false, pending: [], state, published: state, revision, textChanged: new Map(),
        handle: undefined as unknown as MockView,
      };
      view.handle = {
        route,
        get state() { return view.state; },
        get revision() { return view.revision; },
        update(patch) { commit(view, typeof patch === "function" ? patch(view.state) : { ...view.state, ...patch }); },
        push(message) { pushMessage({ route, ...message }); },
        pushFailure(failure) {
          pushMessage({ kind: "failure", route, revision,
            error: { kind: failure.kind ?? "failed", message: failure.message, ...(failure.detail ? { detail: failure.detail } : {}) } });
        },
        collection: field => collectionOf(view, field),
        batch(edit) {
          if (view.batching) { edit(); return; }
          view.batching = true;
          try { edit(); }
          finally {
            view.batching = false;
            publish(view);
          }
        },
        failNext(match, failure, times = 1) { bridge.failNext({ route, ...match }, failure, times); },
        operations: member => [...operations.values()].filter(item => item.view === view && (member === undefined || item.member === member))
          .map(item => item.handle),
        get interactionHandlers() {
          return [...new Set([...view.presentations.values()].filter(item => item.connection === connection.id)
            .flatMap(item => item.handlers.map(handler => handler.name)))];
        },
        interact: (name, input, options) => interact(view, name, input, options),
      };
      views.set(route, view);
      return view.handle;
    },
    intercept(handler) {
      interceptors.add(handler);
      return () => { interceptors.delete(handler); };
    },
    push(message) { pushMessage(message); },
    failNext(match, failure, times = 1) {
      if (!Number.isSafeInteger(times) || times < 1) throw new RangeError("times must be a positive integer.");
      for (let index = 0; index < times; index++) failures.push({ match, failure });
    },
    disconnect() {
      if (!connected) return;
      connected = false;
      const closed = connection;
      if (closed.session !== undefined) closedSessions.add(closed.session);
      for (const view of views.values())
        for (const presentation of [...view.presentations.values()]) if (presentation.connection === closed.id) endPresentation(view, presentation);
      for (const record of operations.values()) record.watchers.delete(closed.id);
      for (const drop of [...closed.pending]) drop(new Error("The mock Bridge is disconnected."));
      connection = newConnection(++connectionCount);
      notifyConnection(false);
    },
    reconnect() {
      if (connected) return;
      connected = true;
      notifyConnection(true);
    },
    sleep(milliseconds) {
      if (!Number.isFinite(milliseconds) || milliseconds < 0) throw new RangeError("The delay must be a non-negative number of milliseconds.");
      return new Promise<void>(resolve => { schedule(now + milliseconds, resolve); });
    },
    flush,
    async flushUntil<T>(promise: Promise<T>): Promise<T> {
      let settled = false;
      const observed = promise.then(value => { settled = true; return value; }, error => { settled = true; throw error; });
      void observed.catch(() => undefined);
      await flush();
      if (!settled) throw new Error("The promise did not settle after the mock Bridge became idle. Advance the virtual clock or settle the pending work.");
      return observed;
    },
    async advance(milliseconds) {
      if (!Number.isFinite(milliseconds) || milliseconds < 0) throw new RangeError("The delay must be a non-negative number of milliseconds.");
      const target = now + milliseconds;
      for (;;) {
        await flush();
        const next = tasks.reduce((earliest, task) => Math.min(earliest, task.due), Number.POSITIVE_INFINITY);
        if (next > target) break;
        now = Math.max(now, next);
      }
      now = target;
      await flush();
    },
  };
  internals.set(bridge, { registered: route => views.get(route)?.handle, contract: route => views.get(route)?.contract });
  return bridge;
}

/** @internal What the typed mocks (mock-typed.ts) read of a mock Bridge. Not part of the public API. */
export interface MockBridgeInternals {
  readonly registered: (route: string) => MockView | undefined;
  readonly contract: (route: string) => string | undefined;
}

const internals = new WeakMap<MockBridge, MockBridgeInternals>();
/** @internal Used by the typed mocks (mock-typed.ts). */
export function mockBridgeInternals(bridge: MockBridge): MockBridgeInternals {
  const value = internals.get(bridge);
  if (!value) throw new TypeError("The value is not a mock Bridge from createMockBridge().");
  return value;
}

/**
 * Installs a mock as `window.__runicBridge`, so generated clients connect to it
 * instead of waiting for a .NET host. Call it before the first `connect*()`.
 */
export function installMockBridge(bridge: MockBridge = createMockBridge()): MockBridge {
  (globalThis as unknown as { __runicBridge?: RunicBridgeClient }).__runicBridge = bridge;
  return bridge;
}
