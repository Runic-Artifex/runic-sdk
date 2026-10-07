import { emitBridgeDiagnostic, emitErrorDiagnostic } from "./diagnostics.js";
import { BridgeError, BridgeOperationUncertainError, decodeFailureDetail, type BridgeFailureDetail } from "./errors.js";
import type { RunicBridgeClient } from "./transport.js";

/**
 * `timedOut` is a client-side terminal state: `wait({ timeout })` passed its
 * deadline and sent a cancellation request. .NET never reports it.
 */
export type BridgeOperationStatusKind = "running" | "succeeded" | "failed" | "cancelled" | "expired" | "unknown" | "timedOut";
export type BridgeOperationDeliveryKind = "result-too-large" | "result-encoding-failed" | "stream-overflow" | "stream-retention-too-large";
export interface BridgeOperationStatus<TResult = never> {
  readonly contract: string;
  readonly requestId: string;
  readonly kind: BridgeOperationStatusKind;
  /** Why the operation failed. `detail` is present only when .NET runs in development. */
  readonly error?: { readonly kind: "failed"; readonly message: string; readonly detail?: BridgeFailureDetail };
  readonly result?: TResult;
  readonly delivery?: { readonly kind: BridgeOperationDeliveryKind; readonly message: string };
  readonly stream?: true;
  /** For `timedOut`: how .NET answered the cancellation request, or `unobserved` when the request failed. */
  readonly cancellation?: BridgeOperationCancelKind | "unobserved";
}
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
  readonly delivery?: { readonly kind: BridgeOperationDeliveryKind; readonly message: string };
}

/** A recoverable .NET command execution identified by its request id. */
export interface BridgeOperation<TResult = never> {
  readonly requestId: string;
  status(): Promise<BridgeOperationStatus<TResult>>;
  /** The terminal status. A failed observation is retried by the next read. */
  readonly completion: Promise<BridgeOperationStatus<TResult>>;
  /**
   * Waits for the terminal status. With `timeout`, resolves to a `timedOut`
   * status after cancelling the operation, unless it finished first.
   */
  wait(options?: BridgeOperationWaitOptions): Promise<BridgeOperationStatus<TResult>>;
  cancel(): Promise<BridgeOperationCancelResult>;
}

/** An operation whose command yields a stream of results. */
export interface BridgeStreamOperation<TResult = never> extends BridgeOperation<TResult> {
  stream(cursor?: number): Promise<BridgeOperationStreamPage<TResult>>;
}

// Kinds .NET may send; `timedOut` exists only on the client.
const statusKinds: readonly BridgeOperationStatusKind[] = ["running", "succeeded", "failed", "cancelled", "expired", "unknown"];


// The value of a promise that has already resolved, or undefined. A
// zero-delay timer runs after the promise's pending reactions.
async function settledNow<T>(promise: Promise<T>): Promise<T | undefined> {
  return Promise.race([
    promise.then(value => value, () => undefined),
    new Promise<undefined>(resolve => setTimeout(resolve, 0, undefined)),
  ]);
}

/** Operation helpers bound to one route contract. */
class OperationChannel {
  constructor(private readonly bridge: RunicBridgeClient, private readonly contract: string) {}

  parseStatus<TResult>(json: string, requestId: string, decode: (value: unknown) => TResult,
    route = "__runicOperationStatus"): BridgeOperationStatus<TResult> {
    let status: BridgeOperationStatus<TResult>;
    try { status = JSON.parse(json) as BridgeOperationStatus<TResult>; }
    catch (cause) { throw new BridgeError("failed", "The operation service returned invalid JSON.", { cause, route }); }
    if (status === null || typeof status !== "object" || status.contract !== this.contract || status.requestId !== requestId
      || !statusKinds.includes(status.kind))
      throw new BridgeError("failed", "The operation service returned a mismatched status.", { route });
    if (status.error !== undefined) {
      const detail = decodeFailureDetail(status.error.detail);
      const { detail: _, ...error } = status.error;
      status = { ...status, error: detail === undefined ? error : { ...error, detail } };
    }
    if (status.result !== undefined) {
      try { status = { ...status, result: decode(status.result) }; }
      catch (cause) {
        throw new BridgeError("failed", `The operation result is invalid: ${cause instanceof Error ? cause.message : String(cause)}`, { cause, route });
      }
    }
    return status;
  }

  async status<TResult>(member: string, requestId: string, wait: boolean, decode: (value: unknown) => TResult): Promise<BridgeOperationStatus<TResult>> {
    const identity = JSON.stringify({ contract: this.contract, member, requestId });
    const route = wait ? "__runicOperationWait" : "__runicOperationStatus";
    let reply: string;
    try { reply = await this.bridge.call(route, identity); }
    catch (cause) { throw new BridgeOperationUncertainError(this.contract, requestId, "The operation status could not be observed.", { cause }); }
    return this.parseStatus(reply, requestId, decode, route);
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

  async streamPage<TResult>(member: string, requestId: string, cursor: number, decode: (value: unknown) => TResult): Promise<BridgeOperationStreamPage<TResult>> {
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
    return { ...page, items } as BridgeOperationStreamPage<TResult>;
  }

  handle<TResult>(member: string, requestId: string, decode: (value: unknown) => TResult, stream: boolean,
    terminal?: BridgeOperationStatus<TResult>): BridgeStreamOperation<TResult> {
    let completion: Promise<BridgeOperationStatus<TResult>> | undefined;
    // A failed wait is not cached: the next wait() or completion read retries.
    const wait = () => completion ??= (terminal === undefined ? this.status(member, requestId, true, decode) : Promise.resolve(terminal))
      .catch(error => { completion = undefined; throw error; });
    const waitWithin = async (options?: BridgeOperationWaitOptions): Promise<BridgeOperationStatus<TResult>> => {
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
      return this.timeOut(member, requestId, decode, timeout, pending);
    };
    return {
      requestId,
      status: () => terminal === undefined ? this.status(member, requestId, false, decode) : Promise.resolve(terminal),
      get completion() { return wait(); },
      wait: waitWithin,
      cancel: () => this.cancel(member, requestId),
      ...(stream ? { stream: (cursor = 0) => this.streamPage(member, requestId, cursor, decode) } : {}),
    } as BridgeStreamOperation<TResult>;
  }

  // The deadline passed: ask .NET to cancel, and report a finished operation
  // as its real terminal state. The pending wait settles independently.
  // The cancellation applies to the operation, so every observer of it sees
  // the cancelled outcome, not only the caller whose deadline passed.
  private async timeOut<TResult>(member: string, requestId: string, decode: (value: unknown) => TResult,
    timeout: number, pending: Promise<BridgeOperationStatus<TResult>>): Promise<BridgeOperationStatus<TResult>> {
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
        const status = await this.status(member, requestId, false, decode);
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

  async recover<TResult>(member: string, requestId: string, decode: (value: unknown) => TResult, stream: boolean): Promise<BridgeStreamOperation<TResult>> {
    const status = await this.status(member, requestId, false, decode);
    if (status.kind === "unknown" || status.kind === "expired")
      throw new BridgeOperationUncertainError(this.contract, requestId, "The operation admission could not be recovered.");
    return this.handle(member, requestId, decode, stream, status.kind === "running" ? undefined : status);
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
 */
export interface BridgeOperations {
  start<TResult>(scope: OperationScope, member: string, requestId: string, payload: () => string,
    decode: (value: unknown) => TResult, stream: boolean): Promise<BridgeStreamOperation<TResult>>;
  recover<TResult>(scope: OperationScope, member: string, requestId: string,
    decode: (value: unknown) => TResult, stream: boolean): Promise<BridgeStreamOperation<TResult>>;
}

export const bridgeOperations: BridgeOperations = {
  async start(scope, member, requestId, payload, decode, stream) {
    const startRoute = `${scope.route}Start${member}`;
    const operations = new OperationChannel(scope.bridge, scope.contract);
    scope.assertConnected();
    await scope.ready();
    let reply: string;
    try { reply = await scope.bridge.call(startRoute, payload()); }
    catch (cause) {
      const recovered = await operations.status(member, requestId, false, decode);
      if (recovered.kind === "unknown" || recovered.kind === "expired")
        throw new BridgeOperationUncertainError(scope.contract, requestId, "The operation admission could not be recovered.", { cause });
      return operations.handle(member, requestId, decode, stream, recovered.kind === "running" ? undefined : recovered);
    }
    let admission: { readonly kind?: string; readonly reason?: string; readonly terminal?: unknown; readonly detail?: unknown };
    try { admission = JSON.parse(reply) as typeof admission; }
    catch (cause) { throw new BridgeError("failed", "The operation service returned invalid JSON.", { cause, route: startRoute }); }
    if (admission.kind === "accepted" || admission.kind === "duplicate") {
      const terminal = admission.terminal === null || admission.terminal === undefined
        ? undefined : operations.parseStatus(JSON.stringify(admission.terminal), requestId, decode);
      return operations.handle(member, requestId, decode, stream, terminal);
    }
    const detail = decodeFailureDetail(admission.detail);
    throw new BridgeError(admission.kind === "rejected" ? "rejected" : "failed", admission.reason ?? "The operation was not accepted.",
      { route: startRoute, ...(detail === undefined ? {} : { detail }) });
  },
  recover: (scope, member, requestId, decode, stream) => new OperationChannel(scope.bridge, scope.contract).recover(member, requestId, decode, stream),
};
