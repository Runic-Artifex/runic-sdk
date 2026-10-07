import { emitBridgeDiagnostic, emitErrorDiagnostic } from "./diagnostics.js";
import { BridgeError, BridgeOperationUncertainError, decodeFailureDetail, type BridgeFailureDetail } from "./errors.js";
import { bridgeFailure, bridgeSuccess, type BridgeOutcome } from "./outcome.js";
import type { RunicBridgeClient } from "./transport.js";

/**
 * `domain-failed` means the command failed with its declared failure type.
 * `timedOut` is a client-side terminal state: `wait({ timeout })` passed its
 * deadline and sent a cancellation request. .NET never reports it.
 */
export type BridgeOperationStatusKind =
  "running" | "succeeded" | "domain-failed" | "failed" | "cancelled" | "expired" | "unknown" | "timedOut";
export type BridgeOperationDeliveryKind = "result-too-large" | "result-encoding-failed" | "stream-overflow" | "stream-retention-too-large";
/** Why a completed operation's result or failure could not be delivered. */
export interface BridgeOperationDelivery { readonly kind: BridgeOperationDeliveryKind; readonly message: string }
interface BridgeOperationIdentity { readonly contract: string; readonly requestId: string }

/**
 * Every operation status, including `domain-failed`. Generic code that builds
 * statuses uses this type; applications read {@link BridgeOperationStatus}.
 */
export type BridgeOperationStatusOf<TResult, TFailure> =
  | BridgeOperationIdentity & { readonly kind: "running" }
  | BridgeOperationIdentity & {
    readonly kind: "succeeded";
    readonly result?: TResult;
    /** The result could not be retained or delivered. */
    readonly delivery?: BridgeOperationDelivery;
    readonly stream?: true;
  }
  | BridgeOperationIdentity & {
    readonly kind: "domain-failed";
    /** The declared failure; absent when it could not be delivered (`delivery`). */
    readonly failure?: TFailure;
    readonly delivery?: BridgeOperationDelivery;
    readonly stream?: true;
  }
  | BridgeOperationIdentity & {
    readonly kind: "failed";
    /** Why the operation failed. `detail` is present only when .NET runs in development. */
    readonly error: { readonly kind: "failed"; readonly message: string; readonly detail?: BridgeFailureDetail };
    readonly stream?: true;
  }
  | BridgeOperationIdentity & { readonly kind: "cancelled" | "expired" | "unknown"; readonly stream?: true }
  | BridgeOperationIdentity & {
    readonly kind: "timedOut";
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
export interface BridgeOperationWaitOptions {
  /**
   * Milliseconds to wait for the terminal status. When it passes, the client
   * sends a cancellation request and resolves to a `timedOut` status.
   */
  readonly timeout?: number;
}
export interface BridgeOperationCancelResult { readonly contract: string; readonly requestId: string; readonly kind: BridgeOperationCancelKind; }
export interface BridgeOperationStreamItem<TResult> { readonly sequence: number; readonly value: TResult; }
export interface BridgeOperationStreamPage<TResult> {
  readonly contract: string;
  readonly requestId: string;
  readonly kind: BridgeOperationStatusKind;
  readonly cursor?: number;
  readonly completed?: boolean;
  readonly items?: readonly BridgeOperationStreamItem<TResult>[];
  readonly delivery?: BridgeOperationDelivery;
}

/** The members every operation handle has. */
export interface BridgeOperationHandle<TResult = void, TFailure = never> {
  readonly requestId: string;
  status(): Promise<BridgeOperationStatus<TResult, TFailure>>;
  /** The terminal status. A failed observation is retried by the next read. */
  readonly completion: Promise<BridgeOperationStatus<TResult, TFailure>>;
  /**
   * Waits for the terminal status. With `timeout`, resolves to a `timedOut`
   * status after cancelling the operation, unless it finished first.
   */
  wait(options?: BridgeOperationWaitOptions): Promise<BridgeOperationStatus<TResult, TFailure>>;
  cancel(): Promise<BridgeOperationCancelResult>;
}

/** A recoverable .NET command execution identified by its request id. */
export interface BridgeOperation<TResult = void, TFailure = never> extends BridgeOperationHandle<TResult, TFailure> {
  /**
   * Waits like `wait()` and resolves to the result, or to the declared failure.
   * Rejects with `BridgeError` for `failed` (`failed`), `cancelled` (`cancelled`),
   * `timedOut` (`timeout`) and a result or failure that could not be delivered
   * (`failed`), and with `BridgeOperationUncertainError` for `expired` and
   * `unknown`: the outcome is unknown, so do not retry blindly.
   */
  outcome(options?: BridgeOperationWaitOptions): Promise<BridgeOutcome<TResult, TFailure>>;
}

/** An operation whose command yields a stream of results, read through `stream()`. */
export interface BridgeStreamOperation<TResult = void, TFailure = never> extends BridgeOperationHandle<TResult, TFailure> {
  /** Like {@link BridgeOperation.outcome}; a stream's values are read through `stream()`, so success has no value. */
  outcome(options?: BridgeOperationWaitOptions): Promise<BridgeOutcome<void, TFailure>>;
  stream(cursor?: number): Promise<BridgeOperationStreamPage<TResult>>;
}

/** What the runtime returns for a started or recovered operation: either handle shape. */
export type BridgeOperationRuntimeHandle<TResult, TFailure> = BridgeOperation<TResult, TFailure> & BridgeStreamOperation<TResult, TFailure>;

// Kinds .NET may send; `timedOut` exists only on the client.
const statusKinds: readonly string[] = ["running", "succeeded", "domain-failed", "failed", "cancelled", "expired", "unknown"];
type Decode<T> = (value: unknown) => T;
type Status<TResult, TFailure> = BridgeOperationStatusOf<TResult, TFailure>;

// The outcome() of a terminal status (W130-029 table): a void or stream
// operation does not need its value, so a lost value only rejects a value operation.
function outcomeOf<TResult, TFailure>(status: Status<TResult, TFailure>, contract: string, stream: boolean): BridgeOutcome<TResult, TFailure> {
  const route = "__runicOperationWait";
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
    case "timedOut":
      throw new BridgeError("timeout", `The operation ${status.requestId} did not complete in time; cancellation: ${status.cancellation}.`, { route, cause: status });
    default:
      throw new BridgeOperationUncertainError(contract, status.requestId, `The outcome of operation ${status.requestId} is ${status.kind}.`, { cause: status });
  }
}



// The value of a promise that has already resolved, or undefined. A
// zero-delay timer runs after the promise's pending reactions.
async function settledNow<T>(promise: Promise<T>): Promise<T | undefined> {
  return Promise.race([
    promise.then(value => value, () => undefined),
    new Promise<undefined>(resolve => setTimeout(resolve, 0, undefined)),
  ]);
}

const invalid = (cause: unknown) => cause instanceof Error ? cause.message : String(cause);

/** Operation helpers bound to one route contract. */
class OperationChannel {
  constructor(private readonly bridge: RunicBridgeClient, private readonly contract: string) {}

  // Without `decodeFailure` the operation declares no failure type, so a
  // `domain-failed` status (only possible from a mismatched host) is `failed`.
  parseStatus<TResult, TFailure>(json: string, requestId: string, decode: Decode<TResult>, decodeFailure: Decode<TFailure> | undefined,
    route = "__runicOperationStatus"): Status<TResult, TFailure> {
    let parsed: Record<string, unknown> | null;
    try { parsed = JSON.parse(json) as Record<string, unknown> | null; }
    catch (cause) { throw new BridgeError("failed", "The operation service returned invalid JSON.", { cause, route }); }
    if (parsed === null || typeof parsed !== "object" || parsed["contract"] !== this.contract || parsed["requestId"] !== requestId
      || typeof parsed["kind"] !== "string" || !statusKinds.includes(parsed["kind"]))
      throw new BridgeError("failed", "The operation service returned a mismatched status.", { route });
    let status = parsed as Record<string, unknown> & { kind: string };
    const error = status["error"] as { readonly detail?: unknown } | undefined;
    if (error !== undefined && error !== null) {
      const detail = decodeFailureDetail(error.detail);
      const { detail: _, ...rest } = error;
      status = { ...status, error: detail === undefined ? rest : { ...rest, detail } };
    }
    if (status.kind === "domain-failed" && decodeFailure === undefined) {
      const { failure: _, delivery: __, ...rest } = status;
      status = { ...rest, kind: "failed", error: { kind: "failed", message: "The operation failed." } };
    }
    if (status.kind === "failed" && (status["error"] === undefined || status["error"] === null))
      status = { ...status, error: { kind: "failed", message: "The operation failed." } };
    if (status["result"] !== undefined) {
      try { status = { ...status, result: decode(status["result"]) }; }
      catch (cause) { throw new BridgeError("failed", `The operation result is invalid: ${invalid(cause)}`, { cause, route }); }
    }
    if (status.kind === "domain-failed" && status["failure"] !== undefined) {
      try { status = { ...status, failure: decodeFailure!(status["failure"]) }; }
      catch (cause) { throw new BridgeError("failed", `The operation failure is invalid: ${invalid(cause)}`, { cause, route }); }
    }
    return status as unknown as Status<TResult, TFailure>;
  }

  async status<TResult, TFailure>(member: string, requestId: string, wait: boolean, decode: Decode<TResult>,
    decodeFailure: Decode<TFailure> | undefined): Promise<Status<TResult, TFailure>> {
    const identity = JSON.stringify({ contract: this.contract, member, requestId });
    const route = wait ? "__runicOperationWait" : "__runicOperationStatus";
    let reply: string;
    try { reply = await this.bridge.call(route, identity); }
    catch (cause) { throw new BridgeOperationUncertainError(this.contract, requestId, "The operation status could not be observed.", { cause }); }
    return this.parseStatus(reply, requestId, decode, decodeFailure, route);
  }

  async cancel(member: string, requestId: string): Promise<BridgeOperationCancelResult> {
    const route = "__runicOperationCancel";
    let reply: string;
    try { reply = await this.bridge.call(route, JSON.stringify({ contract: this.contract, member, requestId })); }
    catch (cause) { throw new BridgeOperationUncertainError(this.contract, requestId, "The cancellation request could not be observed.", { cause }); }
    let result: BridgeOperationCancelResult;
    try { result = JSON.parse(reply) as BridgeOperationCancelResult; }
    catch (cause) { throw new BridgeError("failed", "The cancellation service returned invalid JSON.", { cause, route }); }
    if (result === null || typeof result !== "object" || result.contract !== this.contract || result.requestId !== requestId)
      throw new BridgeError("failed", "The cancellation service returned a mismatched result.", { route });
    return result;
  }

  async streamPage<TResult>(member: string, requestId: string, cursor: number, decode: Decode<TResult>,
    declared: boolean): Promise<BridgeOperationStreamPage<TResult>> {
    const route = "__runicOperationStream";
    let reply: string;
    try { reply = await this.bridge.call(route, JSON.stringify({ contract: this.contract, member, requestId, cursor })); }
    catch (cause) { throw new BridgeOperationUncertainError(this.contract, requestId, "The operation stream could not be observed.", { cause }); }
    let page: BridgeOperationStreamPage<TResult>;
    try { page = JSON.parse(reply) as BridgeOperationStreamPage<TResult>; }
    catch (cause) { throw new BridgeError("failed", "The operation stream returned invalid JSON.", { cause, route }); }
    if (page === null || typeof page !== "object" || page.contract !== this.contract || page.requestId !== requestId)
      throw new BridgeError("failed", "The operation stream returned a mismatched identity.", { route });
    const items = page.items?.map(item => ({ ...item, value: decode(item.value) }));
    // As for statuses, an operation without a declared failure reports `failed`.
    const kind = page.kind === "domain-failed" && !declared ? "failed" : page.kind;
    return { ...page, kind, items } as BridgeOperationStreamPage<TResult>;
  }

  handle<TResult, TFailure>(member: string, requestId: string, decode: Decode<TResult>, decodeFailure: Decode<TFailure> | undefined,
    stream: boolean, terminal?: Status<TResult, TFailure>): BridgeOperationRuntimeHandle<TResult, TFailure> {
    let completion: Promise<Status<TResult, TFailure>> | undefined;
    // A failed wait is not cached: the next wait() or completion read retries.
    const wait = () => completion ??= (terminal === undefined ? this.status(member, requestId, true, decode, decodeFailure) : Promise.resolve(terminal))
      .catch(error => { completion = undefined; throw error; });
    const waitWithin = async (options?: BridgeOperationWaitOptions): Promise<Status<TResult, TFailure>> => {
      const timeout = options?.timeout;
      if (timeout === undefined) return wait();
      if (!Number.isFinite(timeout) || timeout < 0) throw new RangeError("The operation timeout must be a non-negative number of milliseconds.");
      let timer: ReturnType<typeof setTimeout> | undefined;
      const deadline = new Promise<undefined>(resolve => { timer = setTimeout(resolve, timeout, undefined); });
      const pending = wait();
      try {
        const status = await Promise.race([pending, deadline]);
        if (status !== undefined) return status;
      } finally {
        clearTimeout(timer);
      }
      return this.timeOut(member, requestId, decode, decodeFailure, timeout, pending);
    };
    const handle = {
      requestId,
      status: () => terminal === undefined ? this.status(member, requestId, false, decode, decodeFailure) : Promise.resolve(terminal),
      get completion() { return wait(); },
      wait: waitWithin,
      outcome: async (options?: BridgeOperationWaitOptions) => outcomeOf(await waitWithin(options), this.contract, stream),
      cancel: () => this.cancel(member, requestId),
      ...(stream ? { stream: (cursor = 0) => this.streamPage(member, requestId, cursor, decode, decodeFailure !== undefined) } : {}),
    };
    // The one cast from the status union generic code builds to the public type.
    return handle as unknown as BridgeOperationRuntimeHandle<TResult, TFailure>;
  }

  // The deadline passed: ask .NET to cancel, and report a finished operation
  // as its real terminal state. The pending wait settles independently.
  // The cancellation applies to the operation, so every observer of it sees
  // the cancelled outcome, not only the caller whose deadline passed.
  private async timeOut<TResult, TFailure>(member: string, requestId: string, decode: Decode<TResult>,
    decodeFailure: Decode<TFailure> | undefined, timeout: number,
    pending: Promise<Status<TResult, TFailure>>): Promise<Status<TResult, TFailure>> {
    let cancellation: BridgeOperationCancelKind | "unobserved";
    try { cancellation = (await this.cancel(member, requestId)).kind; }
    catch (error) {
      emitErrorDiagnostic(error, "__runicOperationCancel");
      cancellation = "unobserved";
    }
    if (cancellation === "not-running") {
      // The pending wait has usually settled by now; it is the authoritative
      // terminal status and needs no further call.
      const settled = await settledNow(pending);
      if (settled !== undefined) return settled;
      try {
        const status = await this.status(member, requestId, false, decode, decodeFailure);
        if (status.kind !== "running") return status;
      } catch (error) {
        emitErrorDiagnostic(error, "__runicOperationStatus");
        const late = await settledNow(pending);
        if (late !== undefined) return late;
      }
    }
    emitBridgeDiagnostic({
      kind: "operation", code: "timedOut", route: "__runicOperationWait", member, requestId,
      message: `The operation ${member} (${requestId}) did not complete within ${timeout} ms; cancellation: ${cancellation}.`,
    });
    return { contract: this.contract, requestId, kind: "timedOut", cancellation };
  }

  async recover<TResult, TFailure>(member: string, requestId: string, decode: Decode<TResult>, stream: boolean,
    decodeFailure: Decode<TFailure> | undefined): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>> {
    const status = await this.status(member, requestId, false, decode, decodeFailure);
    if (status.kind === "unknown" || status.kind === "expired")
      throw new BridgeOperationUncertainError(this.contract, requestId, "The operation admission could not be recovered.");
    return this.handle(member, requestId, decode, decodeFailure, stream, status.kind === "running" ? undefined : status);
  }
}

/** The connection an operation starts on. */
export interface OperationScope {
  readonly bridge: RunicBridgeClient;
  /** `{contract}:{route}` of the connection. */
  readonly contract: string;
  readonly route: string;
  /** Throws a disconnected BridgeError when the connection can no longer call .NET. */
  readonly assertConnected: () => void;
  /** Resolves when .NET acknowledged the connection's interaction handlers. */
  readonly ready: () => Promise<void>;
}

/**
 * The operation protocol, passed to `connectView` as `operations` by a
 * generated client with operations. A View without operations does not bundle it.
 * `decodeFailure` is given for an operation that declares a failure type.
 */
export interface BridgeOperations {
  start<TResult, TFailure = never>(scope: OperationScope, member: string, requestId: string, payload: () => string,
    decode: Decode<TResult>, stream: boolean, decodeFailure?: Decode<TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
  recover<TResult, TFailure = never>(scope: OperationScope, member: string, requestId: string,
    decode: Decode<TResult>, stream: boolean, decodeFailure?: Decode<TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
}

export const bridgeOperations: BridgeOperations = {
  async start<TResult, TFailure>(scope: OperationScope, member: string, requestId: string, payload: () => string,
    decode: Decode<TResult>, stream: boolean, decodeFailure?: Decode<TFailure>) {
    const startRoute = `${scope.route}Start${member}`;
    const operations = new OperationChannel(scope.bridge, scope.contract);
    scope.assertConnected();
    await scope.ready();
    let reply: string;
    try { reply = await scope.bridge.call(startRoute, payload()); }
    catch (cause) {
      const recovered = await operations.status(member, requestId, false, decode, decodeFailure);
      if (recovered.kind === "unknown" || recovered.kind === "expired")
        throw new BridgeOperationUncertainError(scope.contract, requestId, "The operation admission could not be recovered.", { cause });
      return operations.handle(member, requestId, decode, decodeFailure, stream, recovered.kind === "running" ? undefined : recovered);
    }
    let admission: { readonly kind?: string; readonly reason?: string; readonly terminal?: unknown; readonly detail?: unknown };
    try { admission = JSON.parse(reply) as typeof admission; }
    catch (cause) { throw new BridgeError("failed", "The operation service returned invalid JSON.", { cause, route: startRoute }); }
    if (admission.kind === "accepted" || admission.kind === "duplicate") {
      const terminal = admission.terminal === null || admission.terminal === undefined
        ? undefined : operations.parseStatus(JSON.stringify(admission.terminal), requestId, decode, decodeFailure);
      return operations.handle(member, requestId, decode, decodeFailure, stream, terminal);
    }
    const detail = decodeFailureDetail(admission.detail);
    throw new BridgeError(admission.kind === "rejected" ? "rejected" : "failed", admission.reason ?? "The operation was not accepted.",
      { route: startRoute, ...(detail === undefined ? {} : { detail }) });
  },
  recover: (scope, member, requestId, decode, stream, decodeFailure) =>
    new OperationChannel(scope.bridge, scope.contract).recover(member, requestId, decode, stream, decodeFailure),
};
