import { clock } from "./clock.js";
import { emitBridgeDiagnostic, emitErrorDiagnostic } from "./diagnostics.js";
import { BridgeError, BridgeOperationUncertainError, decodeFailureDetail, uncertainOperation, type BridgeErrorReason, type BridgeFailureDetail } from "./errors.js";
import { abortable, throwIfAborted } from "./lifetime.js";
import { bridgeFailure, bridgeSuccess, type BridgeOutcome } from "./outcome.js";
import { replyError, type Reply } from "./protocol.js";
import { reportBridgeError } from "./transport.js";
import type { RouteOperationScope } from "./connection.js";

/**
 * `domain-failed` means the command failed with its declared failure type.
 * `timed-out` is a client-side terminal state: `wait({ timeout })` passed its
 * deadline and sent a cancellation request. .NET never reports it.
 */
export type BridgeOperationStatusKind =
  "running" | "succeeded" | "domain-failed" | "failed" | "cancelled" | "expired" | "unknown" | "timed-out";
/**
 * Why a completed operation's result or failure could not be delivered (specs/application, "Status"). Open: a
 * minor release may add a kind.
 */
export type BridgeOperationDeliveryFailureKind = "result-too-large" | "result-encoding-failed" | "stream-overflow" | "stream-retention-too-large";
/** Why a completed operation's result or failure could not be delivered. */
export interface BridgeOperationDeliveryFailure { readonly kind: BridgeOperationDeliveryFailureKind; readonly message: string }
interface BridgeOperationIdentity { readonly contract: string; readonly requestId: string }

/**
 * Every operation status, including `domain-failed`. Generic code that builds
 * statuses uses this type; applications read {@link BridgeOperationStatus}.
 */
export type BridgeOperationStatusOf<TResult, TFailure> =
  | BridgeOperationIdentity & {
    readonly kind: "running";
    /** How far the command got, from 0 to 1, when it reports progress; absent while the progress is unknown. */
    readonly progress?: number;
  }
  | BridgeOperationIdentity & {
    readonly kind: "succeeded";
    readonly result?: TResult;
    /** The result could not be retained or delivered. */
    readonly delivery?: BridgeOperationDeliveryFailure;
    readonly stream?: true;
  }
  | BridgeOperationIdentity & {
    readonly kind: "domain-failed";
    /** The declared failure; absent when it could not be delivered (`delivery`). */
    readonly failure?: TFailure;
    readonly delivery?: BridgeOperationDeliveryFailure;
    readonly stream?: true;
  }
  | BridgeOperationIdentity & {
    readonly kind: "failed";
    /** Why the operation failed. `detail` is present only when the producer runs in development. */
    readonly error: { readonly kind: "failed"; readonly message: string; readonly detail?: BridgeFailureDetail };
    readonly stream?: true;
  }
  | BridgeOperationIdentity & { readonly kind: "cancelled" | "expired" | "unknown"; readonly stream?: true }
  | BridgeOperationIdentity & {
    readonly kind: "timed-out";
    /** How .NET answered the cancellation request, or `unobserved` when the request failed. */
    readonly cancellation: BridgeOperationCancelKind | "unobserved";
  };

/**
 * The status of an operation, discriminated by `kind`. An operation that
 * declares no failure type never reports `domain-failed`.
 */
export type BridgeOperationStatus<TResult = void, TFailure = never> =
  [TFailure] extends [never]
    ? Exclude<BridgeOperationStatusOf<TResult, never>, { readonly kind: "domain-failed" }>
    : BridgeOperationStatusOf<TResult, TFailure>;
export type BridgeOperationCancelKind = "cancellation-requested" | "not-running" | "unknown" | "expired";
/** Options of a generated `start{Command}()` call. */
export interface BridgeOperationStartOptions {
  /**
   * Names the operation, so the page can find it again with `recover{Command}(requestId)`, for example
   * after a reload. The default is a random UUID. A start with a request id that .NET already admitted
   * returns that operation instead of starting another.
   */
  readonly requestId?: string;
}
export interface BridgeOperationWaitOptions {
  /**
   * Milliseconds to wait for the terminal status. When it passes, the client
   * sends a cancellation request and resolves to a `timed-out` status.
   */
  readonly timeout?: number;
  /**
   * Abandons this wait when it aborts: the call rejects with `signal.reason`, an
   * `AbortError` DOMException unless `abort(reason)` gave another one. Unlike
   * `timeout`, it sends no cancellation request; the operation keeps running and
   * other waits are unaffected. Call `cancel()` to stop it. An already aborted
   * signal rejects at once; the wait removes its abort listener when it settles.
   */
  readonly signal?: AbortSignal | undefined;
}
export interface BridgeOperationCancelResult { readonly contract: string; readonly requestId: string; readonly kind: BridgeOperationCancelKind; }
export interface BridgeOperationStreamItem<TResult> { readonly sequence: number; readonly value: TResult; }
/** One page of a stream operation's items. */
export interface BridgeOperationStreamPage<TResult> {
  readonly contract: string;
  readonly requestId: string;
  /** The operation's status kind. A page is never `timed-out`, which only `wait()` decides. */
  readonly kind: Exclude<BridgeOperationStatusKind, "timed-out">;
  /** The cursor of the next page: pass it to the next `stream(cursor)` call. */
  readonly cursor?: number;
  readonly completed?: boolean;
  readonly items?: readonly BridgeOperationStreamItem<TResult>[];
  readonly delivery?: BridgeOperationDeliveryFailure;
}

/**
 * A recoverable command execution identified by its request id (the protocol's operation id). Its statuses carry
 * `contract`, the View's operation contract `{view}:{fingerprint}`, and `requestId`.
 */
export interface BridgeOperation<TResult = void, TFailure = never> {
  readonly requestId: string;
  /** Reads the current status. A terminal status resolves once the View's state includes the operation's effects. */
  status(): Promise<BridgeOperationStatus<TResult, TFailure>>;
  /**
   * Calls `listener` with each status the client observes, such as `running` with a new `progress` and the terminal
   * status, until the returned function is called. A subscription watches the operation on the producer, which
   * pushes its changes; the watch ends with the terminal status or when the last observer leaves. A terminal status
   * is delivered once the View's state includes the operation's effects. A listener that throws is reported and does
   * not stop delivery to others.
   */
  subscribe(listener: (status: BridgeOperationStatus<TResult, TFailure>) => void): () => void;
  /** The terminal status. A failed observation is retried by the next read. */
  readonly completion: Promise<BridgeOperationStatus<TResult, TFailure>>;
  /**
   * Waits for the terminal status, which the producer pushes to a `watch`. It resolves once the View's state
   * includes the operation's effects. With `timeout`, resolves to a `timed-out` status after cancelling the
   * operation, unless it finished first. With `signal`, rejects with its abort reason when it aborts first.
   */
  wait(options?: BridgeOperationWaitOptions): Promise<BridgeOperationStatus<TResult, TFailure>>;
  cancel(): Promise<BridgeOperationCancelResult>;
  /**
   * Waits like `wait()` and resolves to the result, or to the declared failure.
   * Rejects with `BridgeError` for `failed` (`failed`), `cancelled` (`cancelled`),
   * `timed-out` (`timeout`) and a result or failure that could not be delivered
   * (`failed`), and with `BridgeOperationUncertainError` for `expired` and
   * `unknown`: the outcome is unknown, so do not retry blindly. Rejects with the
   * abort reason when `signal` aborts first.
   */
  outcome(options?: BridgeOperationWaitOptions): Promise<BridgeOutcome<TResult, TFailure>>;
}

/**
 * An operation whose command yields a stream of `TItem` values, read through
 * `stream()`. Its status and `outcome()` carry no value, so it is a
 * `BridgeOperation<void, TFailure>`.
 */
export interface BridgeStreamOperation<TItem = void, TFailure = never> extends BridgeOperation<void, TFailure> {
  stream(cursor?: number): Promise<BridgeOperationStreamPage<TItem>>;
}

/** What the runtime returns for a started or recovered operation: either handle shape. */
export type BridgeOperationRuntimeHandle<TResult, TFailure> = BridgeOperation<TResult, TFailure> & BridgeStreamOperation<TResult, TFailure>;

/** How generated code decodes the values of one operation. Generated code is its only intended caller. */
export interface BridgeOperationCodec<TResult, TFailure = never> {
  /** Decodes the command's result, or a stream item for a stream operation. */
  readonly decode: (value: unknown) => TResult;
  /** Decodes the declared failure; given exactly when the command declares a failure type. */
  readonly decodeFailure?: ((value: unknown) => TFailure) | undefined;
  /** The command yields a stream, read with `stream()`. */
  readonly stream?: boolean | undefined;
}

/** The connection an operation starts on. Created by `connectView`; its members are internal to the runtime. */
export interface OperationScope {
  /** The View route. */
  readonly route: string;
  /** The View's operation contract, `{view}:{fingerprint}`. */
  readonly contract: string;
}

/**
 * The operation protocol, passed to `connectView` as `operations` by a generated client with operations. A View
 * without operations does not bundle it.
 */
export interface BridgeOperations {
  /** Sends `start` and returns the admitted operation. `input` is the encoded input, or undefined for none. */
  start<TResult, TFailure = never>(scope: OperationScope, member: string, operation: string, input: unknown,
    codec: BridgeOperationCodec<TResult, TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
  /** Reads the status of a known operation id and returns its handle. */
  recover<TResult, TFailure = never>(scope: OperationScope, member: string, operation: string,
    codec: BridgeOperationCodec<TResult, TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
}

type Decode<T> = (value: unknown) => T;
type Status<TResult, TFailure> = BridgeOperationStatusOf<TResult, TFailure>;
type AnyStatus = Status<unknown, unknown>;

// Kinds a producer may send; `timed-out` exists only on the client.
const statusKinds: readonly string[] = ["running", "succeeded", "domain-failed", "failed", "cancelled", "expired", "unknown"];
const terminalKinds = new Set(["succeeded", "domain-failed", "failed", "cancelled", "expired", "unknown"]);
const isTerminal = (status: { readonly kind: string }) => terminalKinds.has(status.kind);
const invalid = (cause: unknown) => cause instanceof Error ? cause.message : String(cause);

// The outcome() of a terminal status: a void or stream operation does not need its value, so a lost value only
// rejects a value operation.
function outcomeOf<TResult, TFailure>(status: Status<TResult, TFailure>, contract: string, route: string, stream: boolean): BridgeOutcome<TResult, TFailure> {
  switch (status.kind) {
    case "succeeded":
      if (status.delivery !== undefined && !stream && status.stream !== true)
        throw new BridgeError("failed", status.delivery.message, { route, cause: status });
      return bridgeSuccess((stream || status.stream === true ? undefined : status.result) as TResult);
    case "domain-failed":
      if (!("failure" in status) || status.failure === undefined)
        throw new BridgeError("failed", status.delivery?.message ?? "The operation failed, but its failure was not delivered.", { route, cause: status });
      return bridgeFailure(status.failure);
    case "failed":
      throw new BridgeError("failed", status.error.message, { route, cause: status, ...(status.error.detail === undefined ? {} : { detail: status.error.detail }) });
    case "cancelled":
      throw new BridgeError("cancelled", `The operation ${status.requestId} was cancelled.`, { route, cause: status });
    case "timed-out":
      throw new BridgeError("timeout", `The operation ${status.requestId} did not complete in time; cancellation: ${status.cancellation}.`, { route, cause: status });
    default:
      throw uncertainOperation(contract, route, status.requestId, `The outcome of operation ${status.requestId} is ${status.kind}.`, status);
  }
}

// The value of a promise that has already resolved, or undefined. A zero-delay timer runs after the promise's
// pending reactions.
async function settledNow<T>(promise: Promise<T>): Promise<T | undefined> {
  return Promise.race([
    promise.then(value => value, () => undefined),
    new Promise<undefined>(resolve => setTimeout(resolve, 0, undefined)),
  ]);
}

/** The requests of one operation, bound to its route, member and id. */
class OperationChannel<TResult, TFailure> {
  readonly route: string;
  readonly contract: string;

  constructor(readonly scope: RouteOperationScope, readonly member: string, readonly operation: string,
    readonly codec: BridgeOperationCodec<TResult, TFailure>) {
    this.route = scope.route;
    this.contract = scope.contract;
  }

  /** Reads a status object; without `decodeFailure` the operation declares no failure, so `domain-failed` is `failed`. */
  parse(wire: Readonly<Record<string, unknown>>): { readonly status: Status<TResult, TFailure>; readonly revision: number | undefined } {
    if (typeof wire["kind"] !== "string" || !statusKinds.includes(wire["kind"]))
      throw new BridgeError("failed", "The Bridge returned an operation status of an unknown kind.", { route: this.route });
    const { revision, progress, ...rest } = wire;
    let status: Record<string, unknown> = { ...rest, contract: this.contract, requestId: this.operation };
    if (status["kind"] === "running" && typeof progress === "number") status["progress"] = progress;
    const error = status["error"] as { readonly detail?: unknown } | undefined;
    if (error !== undefined && error !== null && typeof error === "object") {
      const detail = decodeFailureDetail(error.detail);
      const { detail: _, ...withoutDetail } = error;
      status = { ...status, error: detail === undefined ? withoutDetail : { ...withoutDetail, detail } };
    }
    if (status["kind"] === "domain-failed" && this.codec.decodeFailure === undefined) {
      const { failure: _, delivery: __, ...withoutFailure } = status;
      status = { ...withoutFailure, kind: "failed", error: { kind: "failed", message: "The operation failed." } };
    }
    if (status["kind"] === "failed" && (status["error"] === undefined || status["error"] === null))
      status = { ...status, error: { kind: "failed", message: "The operation failed." } };
    if (status["result"] !== undefined) {
      try { status = { ...status, result: this.codec.decode(status["result"]) }; }
      catch (cause) { throw new BridgeError("failed", `The operation result is invalid: ${invalid(cause)}`, { cause, route: this.route }); }
    }
    if (status["kind"] === "domain-failed" && status["failure"] !== undefined) {
      try { status = { ...status, failure: this.codec.decodeFailure!(status["failure"]) }; }
      catch (cause) { throw new BridgeError("failed", `The operation failure is invalid: ${invalid(cause)}`, { cause, route: this.route }); }
    }
    return { status: status as unknown as Status<TResult, TFailure>, revision: typeof revision === "number" ? revision : undefined };
  }

  /** Sends a request about the operation. A transport failure leaves its outcome unknown. */
  async request(kind: string, extra: Readonly<Record<string, unknown>> = {}): Promise<Reply> {
    this.scope.assertConnected();
    let reply: Reply;
    try {
      reply = await this.scope.runtime.request({ kind, route: this.route, member: this.member, operation: this.operation, contract: this.contract, ...extra }, this.route);
    } catch (cause) {
      if (cause instanceof BridgeError && cause.kind !== "disconnected" && cause.kind !== "failed") throw cause;
      throw uncertainOperation(this.contract, this.route, this.operation, `The operation ${kind} request could not be observed.`, cause);
    }
    if (!reply.ok) throw replyError(reply.error, this.route, `The operation ${kind} request failed.`);
    return reply;
  }

  /** A terminal status resolves once the route reached its revision. */
  async settle(parsed: { readonly status: Status<TResult, TFailure>; readonly revision: number | undefined }): Promise<Status<TResult, TFailure>> {
    if (parsed.revision !== undefined) await this.scope.waitForRevision(parsed.revision);
    return parsed.status;
  }

  async status(): Promise<Status<TResult, TFailure>> {
    const reply = await this.request("status");
    return this.settle(this.parse(reply["status"] as Readonly<Record<string, unknown>>));
  }

  async cancel(): Promise<BridgeOperationCancelResult> {
    const reply = await this.request("cancel");
    return { contract: this.contract, requestId: this.operation, kind: reply["cancellation"] as BridgeOperationCancelKind };
  }

  async stream(cursor: number): Promise<BridgeOperationStreamPage<TResult>> {
    const reply = await this.request("stream", { cursor });
    const page = reply["page"] as Record<string, unknown>;
    const declared = this.codec.decodeFailure !== undefined;
    let items: BridgeOperationStreamItem<TResult>[] | undefined;
    try {
      items = Array.isArray(page["items"])
        ? (page["items"] as { readonly sequence: number; readonly value: unknown }[]).map(item => ({ sequence: item.sequence, value: this.codec.decode(item.value) }))
        : undefined;
    } catch (cause) { throw new BridgeError("failed", `The operation stream returned an invalid item: ${invalid(cause)}`, { cause, route: this.route }); }
    // As for statuses, an operation without a declared failure reports `failed`.
    const kind = page["kind"] === "domain-failed" && !declared ? "failed" : page["kind"];
    return {
      ...page, contract: this.contract, requestId: this.operation, kind, ...(items === undefined ? {} : { items }),
    } as unknown as BridgeOperationStreamPage<TResult>;
  }
}

/**
 * The page's watch of one operation, shared by every handle of it: `watch` answers the current status, and the
 * producer then pushes each change until the terminal status. Without observers the watch ends with `unwatch`.
 */
class OperationWatch {
  private readonly listeners = new Set<(status: AnyStatus) => void>();
  private readonly waiters = new Set<{ resolve(status: AnyStatus): void; reject(error: unknown): void }>();
  private watching = false;
  private stops: (() => void)[] = [];
  private last: AnyStatus | undefined;
  terminal: AnyStatus | undefined;
  private delivering: Promise<void> = Promise.resolve();

  constructor(private readonly channel: OperationChannel<unknown, unknown>, private readonly key: string) {}

  private get observed(): boolean { return this.listeners.size !== 0 || this.waiters.size !== 0; }

  observe(listener: (status: AnyStatus) => void): () => void {
    this.listeners.add(listener);
    if (this.terminal ?? this.last) this.emitTo(listener, (this.terminal ?? this.last)!);
    this.ensure();
    return () => { this.listeners.delete(listener); this.release(); };
  }

  waitTerminal(): { readonly promise: Promise<AnyStatus>; release(): void } {
    if (this.terminal) return { promise: Promise.resolve(this.terminal), release() {} };
    let waiter!: { resolve(status: AnyStatus): void; reject(error: unknown): void };
    const promise = new Promise<AnyStatus>((resolve, reject) => { waiter = { resolve, reject }; });
    this.waiters.add(waiter);
    this.ensure();
    return { promise, release: () => { if (this.waiters.delete(waiter)) this.release(); } };
  }

  private emitTo(listener: (status: AnyStatus) => void, status: AnyStatus): void {
    try { listener(status); } catch (error) { reportBridgeError(error, this.channel.route); }
  }

  private ensure(): void {
    if (this.terminal || this.watching || !this.observed) return;
    const runtime = this.channel.scope.runtime;
    if (!runtime.connected) { this.listen(); return; }
    this.watching = true;
    this.listen();
    this.stops.push(runtime.onPush("operation", this.channel.route, push => {
      if (push["member"] === this.channel.member && push["operation"] === this.channel.operation)
        this.deliver(push["status"] as Readonly<Record<string, unknown>>);
    }));
    void this.channel.request("watch").then(
      reply => this.deliver(reply["status"] as Readonly<Record<string, unknown>>),
      error => {
        this.stopWatching();
        // A closed connection watches again after the reconnect; any other failure ends the waits.
        if (error instanceof BridgeOperationUncertainError && !runtime.connected) return;
        for (const waiter of [...this.waiters]) waiter.reject(error);
        this.waiters.clear();
        if (!this.observed) this.forget();
      });
  }

  // The producer ends a connection's watches with the connection; a new connection watches again.
  private listen(): void {
    if (this.stops.length !== 0) return;
    this.stops.push(this.channel.scope.runtime.onConnection(connected => {
      if (connected) { this.stopWatching(); this.ensure(); }
      else this.watching = false;
    }));
  }

  private stopWatching(): void {
    this.watching = false;
    for (const stop of this.stops.splice(0)) stop();
  }

  private deliver(wire: Readonly<Record<string, unknown>>): void {
    if (this.terminal) return;
    let parsed: { readonly status: AnyStatus; readonly revision: number | undefined };
    try { parsed = this.channel.parse(wire); }
    catch (error) {
      for (const waiter of [...this.waiters]) waiter.reject(error);
      this.waiters.clear();
      reportBridgeError(error, this.channel.route);
      return;
    }
    // Statuses are delivered in order; a terminal one once the route reached its revision.
    this.delivering = this.delivering.then(async () => {
      if (this.terminal) return;
      if (!isTerminal(parsed.status)) {
        this.last = parsed.status;
        for (const listener of [...this.listeners]) this.emitTo(listener, parsed.status);
        return;
      }
      if (parsed.revision !== undefined) await this.channel.scope.waitForRevision(parsed.revision);
      if (this.terminal) return;
      this.terminal = parsed.status;
      this.stopWatching();
      for (const listener of [...this.listeners]) this.emitTo(listener, parsed.status);
      for (const waiter of [...this.waiters]) waiter.resolve(parsed.status);
      this.waiters.clear();
      this.listeners.clear();
      this.forget();
    });
  }

  private release(): void {
    if (this.observed || this.terminal) return;
    if (this.watching && this.channel.scope.runtime.connected) void this.channel.request("unwatch").catch(() => undefined);
    this.stopWatching();
    this.forget();
  }

  private forget(): void {
    const operations = this.channel.scope.runtime.operations;
    if (operations.get(this.key) === this) operations.delete(this.key);
  }
}

function watchOf<TResult, TFailure>(channel: OperationChannel<TResult, TFailure>): OperationWatch {
  const operations = channel.scope.runtime.operations;
  const key = `${channel.route}\u0000${channel.member}\u0000${channel.operation}`;
  let watch = operations.get(key) as OperationWatch | undefined;
  if (!(watch instanceof OperationWatch)) {
    watch = new OperationWatch(channel as OperationChannel<unknown, unknown>, key);
    operations.set(key, watch);
  }
  return watch;
}

function handle<TResult, TFailure>(channel: OperationChannel<TResult, TFailure>, known?: Status<TResult, TFailure>): BridgeOperationRuntimeHandle<TResult, TFailure> {
  // A terminal status that the start or recovery already read needs no watch.
  const terminal = known !== undefined && isTerminal(known) ? known : undefined;
  let completion: Promise<Status<TResult, TFailure>> | undefined;
  // A failed wait is not cached: the next wait() or completion read retries.
  const wait = (): Promise<Status<TResult, TFailure>> => completion ??= (terminal !== undefined ? Promise.resolve(terminal)
    : watchOf(channel).waitTerminal().promise as Promise<Status<TResult, TFailure>>)
    .catch(error => { completion = undefined; throw error; });
  const waitWithin = async (options?: BridgeOperationWaitOptions): Promise<Status<TResult, TFailure>> => {
    const timeout = options?.timeout;
    const signal = options?.signal;
    if (timeout !== undefined && (!Number.isFinite(timeout) || timeout < 0))
      throw new RangeError("The operation timeout must be a non-negative number of milliseconds.");
    throwIfAborted(signal);
    if (timeout === undefined) return abortable(wait(), signal);
    let timer: unknown;
    const deadline = new Promise<undefined>(resolve => { timer = clock.set(() => resolve(undefined), timeout); });
    const pending = wait();
    try {
      const status = await abortable(Promise.race([pending, deadline]), signal);
      if (status !== undefined) return status;
    } finally {
      clock.clear(timer);
    }
    return abortable(timeOut(channel, timeout, pending), signal);
  };
  const operation = {
    requestId: channel.operation,
    status: () => terminal !== undefined ? Promise.resolve(terminal) : channel.status(),
    get completion() { return wait(); },
    wait: waitWithin,
    outcome: async (options?: BridgeOperationWaitOptions) => outcomeOf(await waitWithin(options), channel.contract, channel.route, channel.codec.stream === true),
    cancel: () => channel.cancel(),
    subscribe(listener: (status: Status<TResult, TFailure>) => void): () => void {
      if (terminal !== undefined) {
        try { listener(terminal); } catch (error) { reportBridgeError(error, channel.route); }
        return () => {};
      }
      return watchOf(channel).observe(listener as (status: AnyStatus) => void);
    },
    ...(channel.codec.stream === true ? { stream: (cursor = 0) => channel.stream(cursor) } : {}),
  };
  // The one cast from the status union generic code builds to the public type.
  return operation as unknown as BridgeOperationRuntimeHandle<TResult, TFailure>;
}

// The deadline passed: ask the producer to cancel, and report a finished operation as its real terminal state. The
// pending wait settles independently. The cancellation applies to the operation, so every observer of it sees the
// cancelled outcome, not only the caller whose deadline passed.
async function timeOut<TResult, TFailure>(channel: OperationChannel<TResult, TFailure>, timeout: number,
  pending: Promise<Status<TResult, TFailure>>): Promise<Status<TResult, TFailure>> {
  let cancellation: BridgeOperationCancelKind | "unobserved";
  try { cancellation = (await channel.cancel()).kind; }
  catch (error) {
    emitErrorDiagnostic(error, channel.route);
    cancellation = "unobserved";
  }
  if (cancellation === "not-running") {
    // The pending wait has usually settled by now; it is the authoritative terminal status.
    const settled = await settledNow(pending);
    if (settled !== undefined) return settled;
    try {
      const status = await channel.status();
      if (status.kind !== "running") return status;
    } catch (error) {
      emitErrorDiagnostic(error, channel.route);
      const late = await settledNow(pending);
      if (late !== undefined) return late;
    }
  }
  emitBridgeDiagnostic({
    kind: "operation", code: "timed-out", route: channel.route, member: channel.member, requestId: channel.operation,
    message: `The operation ${channel.member} (${channel.operation}) did not complete within ${timeout} ms; cancellation: ${cancellation}.`,
  });
  return { contract: channel.contract, requestId: channel.operation, kind: "timed-out", cancellation };
}

async function recover<TResult, TFailure>(channel: OperationChannel<TResult, TFailure>, cause?: unknown): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>> {
  const status = await channel.status();
  if (status.kind === "unknown" || status.kind === "expired")
    throw uncertainOperation(channel.contract, channel.route, channel.operation, "The operation admission could not be recovered.", cause);
  return handle(channel, status);
}

export const bridgeOperations: BridgeOperations = {
  async start<TResult, TFailure>(scope: OperationScope, member: string, operation: string, input: unknown,
    codec: BridgeOperationCodec<TResult, TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>> {
    const channel = new OperationChannel(scope as RouteOperationScope, member, operation, codec);
    channel.scope.assertConnected();
    await channel.scope.ready();
    let reply: Reply;
    try {
      reply = await channel.scope.runtime.request({
        kind: "start", route: channel.route, member, operation, contract: channel.contract, ...(input === undefined ? {} : { input }),
      }, channel.route);
    } catch (cause) {
      // The start may have been admitted: its status tells.
      if (cause instanceof BridgeError && cause.kind !== "failed" && cause.kind !== "disconnected") throw cause;
      return recover(channel, cause);
    }
    if (!reply.ok) throw replyError(reply.error, channel.route, "The operation could not be started.");
    const admission = reply["admission"];
    if (admission === "accepted" || admission === "duplicate") {
      await channel.scope.waitForRevision(reply.revision!);
      return handle(channel, await channel.settle(channel.parse(reply["status"] as Readonly<Record<string, unknown>>)));
    }
    const reason = typeof reply["reason"] === "string" ? reply["reason"] as BridgeErrorReason : undefined;
    if (admission === "expired")
      throw new BridgeError("failed", `The operation ${operation} has expired; its id cannot start another operation.`,
        { route: channel.route, reason: reason ?? "expired" });
    throw new BridgeError("rejected", `The operation was not accepted${reason === undefined ? "" : `: ${reason}`}.`,
      { route: channel.route, ...(reason === undefined ? {} : { reason }) });
  },
  recover: (scope, member, operation, codec) => recover(new OperationChannel(scope as RouteOperationScope, member, operation, codec)),
};
