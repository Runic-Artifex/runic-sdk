import type { BridgeOutcome } from "./outcome.js";
import { BridgeError } from "./errors.js";
// Interactions, operations and keyed collections are injected by the generated
// clients that use them, so this module imports only their types.
import type { BridgeCollections } from "./collections.js";
import type { BridgeInteractions, InteractionSurface } from "./interactions.js";
import type { BridgeOperationCodec, BridgeOperationRuntimeHandle, BridgeOperations } from "./operations.js";

/** The framework-neutral surface every generated client shares. */
export interface ViewClient<TState = unknown> {
  /**
   * The latest state, with the value of each setter call that is still on its way (specs/application, "Client
   * overlay"): a member shows the latest value the page set until the producer's state includes that edit, or the
   * edit failed. After `dispose()` or a Bridge session change it stays the last state the client exposed.
   */
  readonly snapshot: TState;
  /**
   * Delivers the current state, then each change of `snapshot`, until the returned
   * function is called. After `dispose()` it delivers the last state once and
   * returns a no-op.
   */
  subscribe(listener: (state: TState) => void): () => void;
  /** Releases this connection. Further calls reject with a `released` BridgeError, or `disconnected` while the Bridge is disconnected. */
  dispose(): void;
}

export type FieldBaseline<T> = { readonly value: T; readonly version: number };
export type FieldWriteOptions<T> = { readonly requestId: string; readonly baseline: FieldBaseline<T> };
export type FieldWriteReceipt<T> =
  | { readonly kind: "applied"; readonly snapshot: FieldBaseline<T>; readonly validation?: string }
  | { readonly kind: "committed-with-error"; readonly snapshot: FieldBaseline<T>; readonly message: string }
  | { readonly kind: "rejected"; readonly message: string }
  | { readonly kind: "conflict"; readonly incoming: FieldBaseline<T>; readonly message: string };

/** Options of a generated `connect<Name>()` function. */
export interface BridgeConnectOptions {
  /**
   * Milliseconds to wait for the host Bridge to connect to .NET before the
   * client rejects with a `timeout` BridgeError. Defaults to 5000.
   */
  readonly timeout?: number | undefined;
  /**
   * Abandons the connection when it aborts before the client is returned: the
   * call rejects with `signal.reason`, an `AbortError` DOMException unless
   * `abort(reason)` gave another one, and releases what it connected. An already
   * aborted signal rejects at once. Aborting after the client was returned has no
   * effect; the call removes its abort listeners when it settles.
   */
  readonly signal?: AbortSignal | undefined;
}

/**
 * One decoder per state member, by wire name: it validates the member's wire value and returns the client value,
 * and throws for an invalid one. A `set` change in a frame decodes only its member.
 */
export type BridgeMemberDecoders<TState> = { readonly [K in keyof TState]-?: (wire: unknown) => TState[K] };

/** How a generated module connects one View route. Generated code is its only intended caller. */
export interface ViewConnectOptions<TState> extends BridgeConnectOptions {
  /** The View's operation contract, `{view}:{fingerprint}`. */
  readonly contract: string;
  /** The route: the root name, or `content{id}` for presented content. */
  readonly route: string;
  /** The state members' decoders, by wire name. Members of a state that are not listed are ignored. */
  readonly members: BridgeMemberDecoders<TState>;
  /** Wire names of checked fields (`write` and `fieldBaseline`). */
  readonly checkedFields?: readonly string[];
  /** The client's interactions, from `defineInteractions`. */
  readonly interactions?: BridgeInteractions;
  /** The keyed collections and append-only text fields, from `defineCollections`. */
  readonly collections?: BridgeCollections;
  /** `bridgeOperations`, for a client with operations. */
  readonly operations?: BridgeOperations;
}

/** A checked write's request members, with the values already encoded as the field's wire type. */
export interface BridgeFieldWriteRequest {
  readonly requestId: string;
  readonly expectedVersion: number;
  readonly expectedValue: unknown;
  readonly value: unknown;
}

/**
 * The runtime half of a generated client: one presentation of a route, registered with `open`. Generated code is
 * its only intended caller. Values and inputs are encoded as their wire types; `undefined` as an input means the
 * command takes none. Calls that change state resolve once the route's state includes their effects (the reply's
 * revision), with the exposed state at that moment.
 */
export interface ViewConnection<TState> extends ViewClient<TState> {
  /** The route of this presentation. */
  readonly route: string;
  readonly interactions: Readonly<Record<string, InteractionSurface>>;
  /**
   * A generated setter: sends `set` with the member's next edit number and exposes `value` (decoded with the
   * member's decoder) in `snapshot` until the edit's revision arrives or the edit fails. A superseded edit resolves
   * like an applied one.
   */
  set(member: string, value: unknown): Promise<TState>;
  /** Runs a command after the presentation's interaction handlers are registered. */
  command(member: string, input?: unknown): Promise<TState>;
  /**
   * Runs a command that declares a failure type, like `command`. Its declared failure resolves
   * `bridgeFailure(decodeFailure(failure))`; every other failure still rejects with `BridgeError`.
   */
  commandOutcome<TFailure>(member: string, decodeFailure: (value: unknown) => TFailure, input?: unknown): Promise<BridgeOutcome<TState, TFailure>>;
  /** Asks whether a command is available for `input` without running it. */
  can(member: string, input?: unknown): Promise<boolean>;
  /** A checked write of `member`; `decode` decodes the field values of the receipt. */
  write<T>(member: string, request: BridgeFieldWriteRequest, decode: (value: unknown) => T): Promise<FieldWriteReceipt<T>>;
  /** The value and version of a checked field in the producer's state, which a `write` call is based on. */
  fieldBaseline(field: string): FieldBaseline<unknown>;
  /** Starts `member` as an operation with the id `operation`. */
  start<TResult, TFailure = never>(member: string, operation: string, input: unknown,
    codec: BridgeOperationCodec<TResult, TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
  /** Returns the operation of `member` with the id `operation`, for example after the page reloaded. */
  recover<TResult, TFailure = never>(member: string, operation: string,
    codec: BridgeOperationCodec<TResult, TFailure>): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
}

/**
 * Connects one generated client presentation to its route: sends `open` with a new presentation id and returns the
 * connection once the state arrived. Presentations of one route share its state, revision and recovery; each has
 * its own subscribers and interaction handlers.
 */
export async function connectView<TState>(options: ViewConnectOptions<TState>): Promise<ViewConnection<TState>> {
  throw new BridgeError("failed", `Not implemented yet: ${options.route}.`);
}

/**
 * Returns a per-module cache of content references. A reference stays the same
 * object while it is reachable, so frameworks can key Views by identity.
 */
export function viewReferences<R extends object>(create: (id: string) => R): (id: string) => R {
  const references = new Map<string, WeakRef<R>>();
  const finalizer = new FinalizationRegistry<{ readonly id: string; readonly reference: WeakRef<R> }>(entry => {
    if (references.get(entry.id) === entry.reference) references.delete(entry.id);
  });
  return id => {
    let reference = references.get(id)?.deref();
    if (reference) return reference;
    reference = create(id);
    const weak = new WeakRef(reference);
    references.set(id, weak);
    finalizer.register(reference, { id, reference: weak });
    return reference;
  };
}
