import { BridgeError, BridgeOperationUncertainError } from "./errors.js";
import type { RunicBridgeClient } from "./transport.js";

export type BridgeOperationStatusKind = "running" | "succeeded" | "failed" | "cancelled" | "expired" | "unknown";
export type BridgeOperationDeliveryKind = "result-too-large" | "result-encoding-failed" | "stream-overflow" | "stream-retention-too-large";
export interface BridgeOperationStatus<TResult = never> {
  readonly contract: string;
  readonly requestId: string;
  readonly kind: BridgeOperationStatusKind;
  readonly error?: { readonly kind: "failed"; readonly message: string };
  readonly result?: TResult;
  readonly delivery?: { readonly kind: BridgeOperationDeliveryKind; readonly message: string };
  readonly stream?: true;
}
export type BridgeOperationCancelKind = "cancellation-requested" | "not-running" | "unknown" | "expired";
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
  wait(): Promise<BridgeOperationStatus<TResult>>;
  cancel(): Promise<BridgeOperationCancelResult>;
}

/** An operation whose command yields a stream of results. */
export interface BridgeStreamOperation<TResult = never> extends BridgeOperation<TResult> {
  stream(cursor?: number): Promise<BridgeOperationStreamPage<TResult>>;
}

const statusKinds: readonly BridgeOperationStatusKind[] = ["running", "succeeded", "failed", "cancelled", "expired", "unknown"];

/** Operation helpers bound to one route contract. */
export class OperationChannel {
  constructor(private readonly bridge: RunicBridgeClient, private readonly contract: string) {}

  parseStatus<TResult>(json: string, requestId: string, decode: (value: unknown) => TResult): BridgeOperationStatus<TResult> {
    let status: BridgeOperationStatus<TResult>;
    try { status = JSON.parse(json) as BridgeOperationStatus<TResult>; }
    catch { throw new BridgeError("failed", "The operation service returned invalid JSON."); }
    if (status.contract !== this.contract || status.requestId !== requestId || !statusKinds.includes(status.kind))
      throw new BridgeError("failed", "The operation service returned a mismatched status.");
    if (status.result !== undefined) status = { ...status, result: decode(status.result) };
    return status;
  }

  async status<TResult>(member: string, requestId: string, wait: boolean, decode: (value: unknown) => TResult): Promise<BridgeOperationStatus<TResult>> {
    const identity = JSON.stringify({ contract: this.contract, member, requestId });
    let reply: string;
    try { reply = await this.bridge.call(wait ? "__runicOperationWait" : "__runicOperationStatus", identity); }
    catch { throw new BridgeOperationUncertainError(this.contract, requestId, "The operation status could not be observed."); }
    return this.parseStatus(reply, requestId, decode);
  }

  async cancel(member: string, requestId: string): Promise<BridgeOperationCancelResult> {
    let reply: string;
    try { reply = await this.bridge.call("__runicOperationCancel", JSON.stringify({ contract: this.contract, member, requestId })); }
    catch { throw new BridgeOperationUncertainError(this.contract, requestId, "The cancellation request could not be observed."); }
    let result: BridgeOperationCancelResult;
    try { result = JSON.parse(reply) as BridgeOperationCancelResult; }
    catch { throw new BridgeError("failed", "The cancellation service returned invalid JSON."); }
    if (result.contract !== this.contract || result.requestId !== requestId)
      throw new BridgeError("failed", "The cancellation service returned a mismatched result.");
    return result;
  }

  async streamPage<TResult>(member: string, requestId: string, cursor: number, decode: (value: unknown) => TResult): Promise<BridgeOperationStreamPage<TResult>> {
    let reply: string;
    try { reply = await this.bridge.call("__runicOperationStream", JSON.stringify({ contract: this.contract, member, requestId, cursor })); }
    catch { throw new BridgeOperationUncertainError(this.contract, requestId, "The operation stream could not be observed."); }
    let page: BridgeOperationStreamPage<TResult>;
    try { page = JSON.parse(reply) as BridgeOperationStreamPage<TResult>; }
    catch { throw new BridgeError("failed", "The operation stream returned invalid JSON."); }
    if (page.contract !== this.contract || page.requestId !== requestId)
      throw new BridgeError("failed", "The operation stream returned a mismatched identity.");
    const items = page.items?.map(item => ({ ...item, value: decode(item.value) }));
    return { ...page, items } as BridgeOperationStreamPage<TResult>;
  }

  handle<TResult>(member: string, requestId: string, decode: (value: unknown) => TResult, stream: boolean,
    terminal?: BridgeOperationStatus<TResult>): BridgeStreamOperation<TResult> {
    let completion: Promise<BridgeOperationStatus<TResult>> | undefined;
    // A failed wait is not cached: the next wait() or completion read retries.
    const wait = () => completion ??= (terminal === undefined ? this.status(member, requestId, true, decode) : Promise.resolve(terminal))
      .catch(error => { completion = undefined; throw error; });
    return {
      requestId,
      status: () => terminal === undefined ? this.status(member, requestId, false, decode) : Promise.resolve(terminal),
      get completion() { return wait(); },
      wait,
      cancel: () => this.cancel(member, requestId),
      ...(stream ? { stream: (cursor = 0) => this.streamPage(member, requestId, cursor, decode) } : {}),
    } as BridgeStreamOperation<TResult>;
  }

  async recover<TResult>(member: string, requestId: string, decode: (value: unknown) => TResult, stream: boolean): Promise<BridgeStreamOperation<TResult>> {
    const status = await this.status(member, requestId, false, decode);
    if (status.kind === "unknown" || status.kind === "expired")
      throw new BridgeOperationUncertainError(this.contract, requestId, "The operation admission could not be recovered.");
    return this.handle(member, requestId, decode, stream, status.kind === "running" ? undefined : status);
  }
}
