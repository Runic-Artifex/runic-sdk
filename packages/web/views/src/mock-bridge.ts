import type { BridgeErrorKind, BridgeErrorReason, BridgeFailureDetail } from "./errors.js";
import type { RunicBridgeClient } from "./transport.js";

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

/** Whether a command is available for a wire input (`can`, and the check before the command runs). */
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
   * arrive after a newer edit of their member then answer `superseded`, as a producer does.
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
export function createMockBridge(options: MockBridgeOptions = {}): MockBridge {
  void options;
  throw new Error("Not implemented yet.");
}

/**
 * Installs a mock as `window.__runicBridge`, so generated clients connect to it
 * instead of waiting for a .NET host. Call it before the first `connect*()`.
 */
export function installMockBridge(bridge: MockBridge = createMockBridge()): MockBridge {
  (globalThis as unknown as { __runicBridge?: RunicBridgeClient }).__runicBridge = bridge;
  return bridge;
}
