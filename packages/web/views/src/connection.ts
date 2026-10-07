import { emitBridgeDiagnostic, emitErrorDiagnostic } from "./diagnostics.js";
import { BridgeError, type BridgeErrorKind, decodeFailureDetail } from "./errors.js";
import { bridgeFailure, bridgeSuccess, type BridgeOutcome } from "./outcome.js";
import { errorMessage, sameWire, sharedRouteFor, sharedRuntimeFor, type SharedEntry, type SharedLease, type SharedRoute } from "./runtime.js";
import { hostCallbacks, reportBridgeError, waitForBridge, type RunicBridgeClient } from "./transport.js";
import * as bridgeWire from "./wire.js";
// Interactions, operations and keyed collections are injected by the generated
// clients that use them, so this module imports only their types.
import type { BridgeCollections } from "./collections.js";
import type { BridgeInteractions, InteractionSession, InteractionSurface } from "./interactions.js";
import type { BridgeOperationRuntimeHandle, BridgeOperations, OperationScope } from "./operations.js";

/** The framework-neutral surface every generated client shares. */
export interface ViewClient<TState = unknown> {
  /**
   * The latest accepted state. After `dispose()` or a Bridge session change it
   * stays the last state the client accepted.
   */
  readonly snapshot: TState;
  /**
   * Delivers the current state, then each accepted state, until the returned
   * function is called. After `dispose()` it delivers the last state once and
   * returns a no-op.
   */
  subscribe(listener: (state: TState) => void): () => void;
  /** Releases this connection. Further calls reject with a disconnected BridgeError. */
  dispose(): void;
}

export type FieldBaseline<T> = { readonly value: T; readonly version: number };
export type FieldWriteOptions<T> = { readonly requestId: string; readonly baseline: FieldBaseline<T> };
export type FieldWriteReceipt<T> =
  | { readonly kind: "applied"; readonly snapshot: FieldBaseline<T>; readonly validation?: string }
  | { readonly kind: "committed-with-error"; readonly snapshot: FieldBaseline<T>; readonly message: string }
  | { readonly kind: "rejected"; readonly message: string }
  | { readonly kind: "conflict"; readonly incoming: FieldBaseline<T>; readonly message: string };

/** How a generated module connects one ViewModel route. */
export interface ViewConnectOptions<TState> {
  /** `{ViewModel full name}:{contract fingerprint}`. */
  readonly contract: string;
  /** The route prefix: the root name, or `content{id}` for presented content. */
  readonly route: string;
  /** Acknowledge this presentation with a mount token, so .NET attaches a View. */
  readonly mount: boolean;
  /** Decodes a wire state, without its revision and field metadata. Throws for an invalid state. */
  readonly hydrate: (wire: never) => TState;
  /** Wire names of fields with checked writes. */
  readonly checkedFields?: readonly string[];
  /** The client's interactions, from `defineInteractions`. */
  readonly interactions?: BridgeInteractions;
  /** Generated codecs for opt-in keyed collection changes, from `defineCollections`. */
  readonly collections?: BridgeCollections;
  /** `bridgeOperations`, for a client with operations. */
  readonly operations?: BridgeOperations;
}

/** The runtime half of a generated client. Generated code is its only intended caller. */
export interface ViewConnection<TState> extends ViewClient<TState> {
  readonly interactions: Readonly<Record<string, InteractionSurface>>;
  /** Calls a state-returning route. */
  invoke(name: string, ...args: unknown[]): Promise<TState>;
  /** Calls a command route after .NET acknowledged this View's interaction handlers. */
  command(name: string, ...args: unknown[]): Promise<TState>;
  /**
   * Calls a command route that declares a failure type, like `command`. Its
   * declared failure resolves `bridgeFailure(decodeFailure(failure))`; every
   * other failure still rejects with `BridgeError`.
   */
  commandOutcome<TFailure>(name: string, decodeFailure: (value: unknown) => TFailure, ...args: unknown[]): Promise<BridgeOutcome<TState, TFailure>>;
  /** Calls a `Can{Command}` route. */
  query(name: string, ...args: unknown[]): Promise<boolean>;
  writeField<T>(name: string, payload: string, decode: (value: unknown) => T): Promise<FieldWriteReceipt<T>>;
  fieldBaseline(field: string): FieldBaseline<unknown>;
  /** `decodeFailure` is given for an operation that declares a failure type. */
  startOperation<TResult, TFailure = never>(member: string, requestId: string, payload: () => string, decode: (value: unknown) => TResult,
    stream?: boolean, decodeFailure?: (value: unknown) => TFailure): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
  recoverOperation<TResult, TFailure = never>(member: string, requestId: string, decode: (value: unknown) => TResult,
    stream?: boolean, decodeFailure?: (value: unknown) => TFailure): Promise<BridgeOperationRuntimeHandle<TResult, TFailure>>;
}

type EnvelopeError = { readonly kind: string; readonly message: string; readonly detail?: unknown; readonly failure?: unknown };
type Envelope = { readonly ok: boolean; readonly state: unknown; readonly error: EnvelopeError | null; readonly protocol?: unknown };

/** The Views wire protocol this runtime implements (specs/application/README.md). */
export const bridgeProtocol = 2;

const callFailed = (bridge: RunicBridgeClient, message: string, route: string, cause: unknown) =>
  new BridgeError(bridge.isConnected() ? "failed" : "disconnected", message, { cause, route });

const errorKinds: readonly string[] = ["rejected", "cancelled", "failed", "disconnected", "timeout", "unavailable"] satisfies readonly BridgeErrorKind[];

// A kind this client does not know, including `domain-failed` on a route that
// declares no failure type, is `failed`: a newer host cannot surprise it.
function replyError(error: EnvelopeError | null, route: string, fallback: string): BridgeError {
  const detail = decodeFailureDetail(error?.detail);
  const kind = typeof error?.kind === "string" && errorKinds.includes(error.kind) ? error.kind as BridgeErrorKind : "failed";
  return new BridgeError(kind, error?.message ?? fallback, { route, ...(detail === undefined ? {} : { detail }) });
}

// The protocol version is informational. A snapshot reply from a host that
// speaks another version is reported once per Bridge, as a diagnostic.
const protocolReported = new WeakSet<object>();
function checkProtocol(reply: Envelope, entry: SharedEntry, route: string): void {
  if (reply.protocol === undefined || reply.protocol === bridgeProtocol || protocolReported.has(entry.bridge)) return;
  protocolReported.add(entry.bridge);
  emitBridgeDiagnostic({
    kind: "error", code: "protocol", route,
    message: `The .NET host uses Views protocol ${String(reply.protocol)}, but this client was built for protocol ${bridgeProtocol}. ` +
      "Use the same Runic.Application and @runic-artifex/views version.",
  });
}

// Parses a reply and accepts its state. Returns the envelope and the accepted
// state, which is undefined when the reply carries none.
function parse(json: string, entry: SharedEntry, route: string): { readonly reply: Envelope; readonly state: unknown } {
  let reply: Envelope;
  try { reply = JSON.parse(json) as Envelope; }
  catch (cause) { throw new BridgeError("failed", "The Bridge returned an invalid response.", { cause, route }); }
  if (reply === null || typeof reply !== "object") throw new BridgeError("failed", "The Bridge returned an invalid response.", { route });
  if (route.endsWith("Snapshot")) checkProtocol(reply, entry, route);
  let state: unknown;
  try { state = reply.state === null ? undefined : entry.accept(reply.state); }
  catch (cause) { throw new BridgeError("failed", `The Bridge returned an invalid state: ${errorMessage(cause)}`, { cause, route }); }
  return { reply, state };
}

function unpack(json: string, entry: SharedEntry, route: string): unknown {
  const { reply, state } = parse(json, entry, route);
  if (!reply.ok) throw replyError(reply.error, route, "The call failed.");
  if (state === undefined) throw new BridgeError("failed", "The Bridge returned no state.", { route });
  return state;
}

// .NET appends this to a reply's message when the call ran but its state
// could not be sent because of invalid collection keys (D-13).
const stateNotSent = " The updated state could not be sent: ";

// A declared failure resolves the outcome. When .NET could not send the state
// after the call (D-13), the failure still resolves and the missing state is
// reported, so the key problem is not lost behind a resolved outcome. A reply
// without state from a detached Bridge needs no report.
function unpackOutcome<TFailure>(json: string, entry: SharedEntry, route: string,
  decodeFailure: (value: unknown) => TFailure): BridgeOutcome<unknown, TFailure> {
  const { reply, state } = parse(json, entry, route);
  if (!reply.ok && reply.error?.kind === "domain-failed") {
    let failure: TFailure;
    try { failure = decodeFailure(reply.error.failure); }
    catch (cause) { throw new BridgeError("failed", `The Bridge returned an invalid failure: ${errorMessage(cause)}`, { cause, route }); }
    if (state === undefined && reply.error.message?.includes(stateNotSent)) reportBridgeError(new BridgeError("failed", reply.error.message ?? "The Bridge returned no state.", { route }), route);
    return bridgeFailure(failure);
  }
  if (!reply.ok) throw replyError(reply.error, route, "The call failed.");
  if (state === undefined) throw new BridgeError("failed", "The Bridge returned no state.", { route });
  return bridgeSuccess(state);
}

// Recovery reads a snapshot up to four times, waiting 250, 500 and 1,000 ms
// between failed reads. Frames that arrive meanwhile are kept, up to the
// producer's own bound of 64 pending frames, and applied after the read.
const recoveryAttempts = 4;
const recoveryDelay = 250;
const maximumBufferedFrames = 64;

function createEntry(contract: string, route: string, bridge: RunicBridgeClient, routeEntry: SharedRoute,
  hydrate: (wire: unknown) => unknown, collections: BridgeCollections | undefined): SharedEntry {
  let recovering = false;
  let buffered: unknown[] = [];
  let missedRevision: number | undefined;
  let backoff: { readonly timer: ReturnType<typeof setTimeout>; readonly resolve: () => void } | undefined;
  const live = () => entry.active && routeEntry.active;
  const wait = (milliseconds: number) => new Promise<void>(resolve => {
    backoff = { timer: setTimeout(() => { backoff = undefined; resolve(); }, milliseconds), resolve };
  });

  // Reads the snapshot, retrying a failed read with backoff. Returns whether a state was accepted.
  async function readRecoveryState(): Promise<boolean> {
    const snapshotRoute = `${route}Snapshot`;
    for (let attempt = 1; ; attempt++) {
      try {
        let reply: string;
        try { reply = await bridge.call(snapshotRoute); }
        catch (cause) { throw callFailed(bridge, "The state could not be re-read after an unusable collection change.", snapshotRoute, cause); }
        if (!live()) return false;
        unpack(reply, entry, snapshotRoute);
        return true;
      } catch (error) {
        if (!live()) return false;
        // A reconnect re-reads every route itself; a disconnected Bridge cannot answer a retry.
        const disconnected = !bridge.isConnected() || error instanceof BridgeError && error.kind === "disconnected";
        if (disconnected || attempt === recoveryAttempts) { reportBridgeError(error, snapshotRoute); return false; }
        emitErrorDiagnostic(error, snapshotRoute);
        await wait(recoveryDelay * 2 ** (attempt - 1));
        if (!live()) return false;
      }
    }
  }

  function recover(): void {
    if (recovering || !live()) return;
    recovering = true;
    void readRecoveryState().catch(error => { reportBridgeError(error); return false; }).then(recovered => {
      recovering = false;
      const frames = buffered;
      const missed = missedRevision;
      buffered = [];
      missedRevision = undefined;
      // After a failed recovery the client keeps its last state; the next frame that needs one recovers again.
      if (!recovered || !live()) return;
      for (const frame of frames) entry.accept(frame);
      if (missed !== undefined && (entry.revision === undefined || missed > entry.revision)) recover();
    }).catch(error => { reportBridgeError(error); });
  }

  function notify(current: unknown): void {
    for (const lease of entry.leases) if (!lease.disposed) {
      lease.current = current;
      for (const listener of lease.listeners) {
        try { listener(current); } catch (error) { reportBridgeError(error); }
      }
    }
  }

  const entry: SharedEntry = {
    contract, route, bridge, routeEntry, leases: new Set(), hydrate, current: undefined, wire: undefined, revision: undefined,
    initializing: undefined, active: true,
    close() {
      // Ends a recovery backoff at once; the recovery then sees the route inactive.
      if (!backoff) return;
      clearTimeout(backoff.timer);
      backoff.resolve();
      backoff = undefined;
    },
    accept(wire) {
      const revision = (wire as { readonly revision: number }).revision;
      if (!live()) return entry.current ?? entry.hydrate(wire);
      const failure = (wire as { readonly __runicFailure?: unknown }).__runicFailure;
      if (failure !== undefined) {
        // The producer cannot publish a valid state for now and withholds this
        // route until it can; the client keeps its last state.
        const error = (wire as { readonly error?: EnvelopeError | null }).error ?? null;
        reportBridgeError(replyError(error, `__${route}Changed`, `The Bridge cannot publish the state of ${route}.`));
        return entry.current;
      }
      const delta = wire as { readonly __runicDelta?: unknown; readonly baseRevision?: unknown; readonly changes?: unknown };
      if (delta.__runicDelta !== undefined) {
        // Keep frames that arrive during a recovery read and apply them against the state it returns.
        if (recovering) {
          // A repeated frame is kept once.
          if (buffered.some(frame => (frame as { readonly revision?: unknown }).revision === revision)) return entry.current;
          if (buffered.length < maximumBufferedFrames) buffered.push(wire);
          else if (typeof revision === "number") missedRevision = Math.max(missedRevision ?? revision, revision);
          return entry.current;
        }
        if (typeof revision !== "number" || !Number.isSafeInteger(revision) || delta.__runicDelta !== 1) { recover(); return entry.current; }
        if (entry.revision !== undefined && revision <= entry.revision) return entry.current;
        if (entry.current === undefined || delta.baseRevision !== entry.revision ||
          typeof delta.baseRevision !== "number" || revision <= delta.baseRevision) { recover(); return entry.current; }
        let current: unknown;
        let currentWire: Record<string, unknown>;
        try {
          if (!collections) throw new Error("This View has no keyed collections.");
          current = collections.apply(entry.current, delta.changes);
          // Checked field baselines are unchanged by collection-only frames.
          currentWire = collections.applyWire(entry.wire, delta.changes as readonly unknown[]);
        }
        catch (cause) {
          // Report why the change was unusable, then recover from a full snapshot.
          reportBridgeError(new BridgeError("failed", `A collection change for ${route} could not be applied: ${errorMessage(cause)}`,
            { cause, route: `__${route}Changed` }));
          recover();
          return entry.current;
        }
        entry.current = current;
        entry.revision = revision;
        entry.wire = { ...currentWire, revision };
        notify(current);
        return current;
      }
      // A full state at the current revision that matches the current one is a repeat.
      if (entry.current === undefined || entry.revision === undefined || revision > entry.revision ||
        revision === entry.revision && !sameWire(wire, entry.wire)) {
        // Decode first: a state that fails validation must not advance the revision.
        const current = entry.hydrate(wire);
        collections?.validate(current);
        entry.revision = revision;
        entry.wire = wire;
        entry.current = current;
        notify(current);
      }
      return entry.current;
    },
  };
  routeEntry.entries.set(contract, entry);
  const initialization = (async () => {
    let reply: string;
    try { reply = await bridge.call(`${route}Snapshot`); }
    catch (cause) { throw callFailed(bridge, "The Bridge call could not complete.", `${route}Snapshot`, cause); }
    unpack(reply, entry, `${route}Snapshot`);
  })();
  entry.initializing = initialization;
  const settle = () => { if (entry.initializing === initialization) entry.initializing = undefined; };
  initialization.then(settle, settle);
  return entry;
}

/**
 * Connects one generated client lease to its route. Leases of one route share
 * a single snapshot, push callback and revision; each lease has its own
 * listeners, mount token and interaction handlers.
 */
export async function connectView<TState>(options: ViewConnectOptions<TState>): Promise<ViewConnection<TState>> {
  try { return await connectRoute(options); }
  catch (error) { emitErrorDiagnostic(error, `${options.route}Snapshot`); throw error; }
}

// Reports a failure that leaves a client method once, with its route, then rethrows it.
async function observed<T>(route: string, run: () => Promise<T>): Promise<T> {
  try { return await run(); }
  catch (error) { emitErrorDiagnostic(error, route); throw error; }
}

async function connectRoute<TState>(options: ViewConnectOptions<TState>): Promise<ViewConnection<TState>> {
  const { route } = options;
  const bridge = await waitForBridge();
  const runtime = sharedRuntimeFor(bridge);
  const contractId = `${options.contract}:${route}`;
  const routeEntry = sharedRouteFor(runtime, bridge, route);
  if (routeEntry.entries.size !== 0 && !routeEntry.entries.has(contractId))
    throw new BridgeError("failed", "This route already has an incompatible Bridge contract.");
  const shared = routeEntry.entries.get(contractId)
    ?? createEntry(contractId, route, bridge, routeEntry, wire => options.hydrate(wire as never), options.collections);
  const mountToken = options.mount ? `${runtime.mountSession}:${globalThis.crypto.randomUUID()}` : undefined;
  const lease: SharedLease = { disposed: false, current: undefined, listeners: new Set(), mounted: false, mountToken };
  shared.leases.add(lease);
  let interactions: InteractionSession | undefined;

  function isLive(): boolean {
    return shared.active && routeEntry.active && runtime.bridge === bridge && routeEntry.generation === runtime.generation;
  }
  function assertConnected(): void {
    if (lease.disposed || !isLive() || !bridge.isConnected()) throw new BridgeError("disconnected", "The Bridge is disconnected.");
  }
  async function call(name: string, args: readonly unknown[]): Promise<string> {
    assertConnected();
    let reply: string;
    try { reply = await bridge.call(name, ...args); }
    catch (cause) { throw callFailed(bridge, "The Bridge call could not complete.", name, cause); }
    if (lease.disposed || !isLive()) throw new BridgeError("disconnected", "This view was disposed. Reconnect for the current state.", { route: name });
    return reply;
  }
  async function invoke(name: string, ...args: unknown[]): Promise<TState> {
    return unpack(await call(name, args), shared, name) as TState;
  }
  function dispose(): void {
    if (lease.disposed) return;
    lease.disposed = true;
    interactions?.dispose();
    if (lease.mounted && lease.mountToken) {
      // .NET also releases the mount with the connection, so a closed transport is expected here.
      void bridge.call(`${route}Unmount`, lease.mountToken).catch(cause => {
        if (bridge.isConnected()) reportBridgeError(new BridgeError("failed", "The View could not be unmounted.", { cause, route: `${route}Unmount` }));
      });
    }
    lease.listeners.clear();
    shared.leases.delete(lease);
    if (shared.leases.size !== 0 || routeEntry.entries.get(contractId) !== shared) return;
    shared.active = false;
    shared.close?.();
    routeEntry.entries.delete(contractId);
    if (routeEntry.entries.size !== 0) return;
    routeEntry.active = false;
    if (runtime.routes.get(route) === routeEntry) runtime.routes.delete(route);
    const callbacks = hostCallbacks();
    if (callbacks[routeEntry.callbackName] === routeEntry.callback) callbacks[routeEntry.callbackName] = routeEntry.previousCallback;
  }

  try { await shared.initializing; }
  catch (error) { dispose(); throw error; }
  if (!isLive()) { dispose(); throw new BridgeError("disconnected", "The Bridge session changed during connection."); }
  lease.current = shared.current;
  if (mountToken) {
    try {
      lease.mounted = true;
      const acknowledged = await bridge.call(`${route}Mount`, mountToken);
      if (acknowledged !== "ok") throw new BridgeError("disconnected", "The View mount was not accepted.", { route: `${route}Mount` });
    } catch (cause) {
      dispose();
      throw cause instanceof BridgeError ? cause : new BridgeError("disconnected", "The View mount could not complete.", { cause, route: `${route}Mount` });
    }
  }
  if (!isLive()) { dispose(); throw new BridgeError("disconnected", "The Bridge session changed during connection."); }
  if (options.interactions)
    interactions = options.interactions({ bridge, route, presentationId: mountToken, live: () => !lease.disposed && isLive() });
  const ready = async () => { await interactions?.ready(); };
  const operationScope: OperationScope = { bridge, contract: contractId, route, assertConnected, ready };
  const operationSupport = () => {
    if (!options.operations) throw new BridgeError("failed", "This View was connected without operation support.");
    return options.operations;
  };

  return {
    get snapshot() { return lease.current as TState; },
    subscribe(listener) {
      const typed = listener as (state: unknown) => void;
      if (lease.disposed || !isLive()) { typed(lease.current); return () => {}; }
      lease.listeners.add(typed);
      // The caller sees a failing initial delivery and gets no unsubscribe, so do not retain it.
      try { typed(lease.current); }
      catch (error) { lease.listeners.delete(typed); throw error; }
      return () => { lease.listeners.delete(typed); };
    },
    dispose,
    interactions: interactions?.surface ?? {},
    invoke: (name, ...args) => observed(name, () => invoke(name, ...args)),
    command: (name, ...args) => observed(name, async () => {
      await ready();
      return invoke(name, ...args);
    }),
    commandOutcome<TFailure>(name: string, decodeFailure: (value: unknown) => TFailure, ...args: unknown[]) {
      return observed(name, async () => {
        await ready();
        return unpackOutcome(await call(name, args), shared, name, decodeFailure) as BridgeOutcome<TState, TFailure>;
      });
    },
    query: (name, ...args) => observed(name, async () => {
      assertConnected();
      let reply: string;
      try { reply = await bridge.call(name, ...args); }
      catch (cause) { throw callFailed(bridge, "The command availability query could not complete.", name, cause); }
      if (reply === "true") return true;
      if (reply === "false") return false;
      throw new BridgeError("failed", "The command availability query returned an invalid response.", { route: name });
    }),
    writeField: (name, payload, decode) => observed(name, async () => {
      const json = await call(name, [payload]);
      let reply: Envelope & { readonly receipt: unknown };
      try { reply = bridgeWire.object(JSON.parse(json), value => value) as unknown as typeof reply; }
      catch (cause) { throw new BridgeError("failed", "The Bridge returned an invalid response.", { cause, route: name }); }
      if (reply.state !== null) shared.accept(reply.state);
      if (!reply.ok) throw replyError(reply.error, name, "The checked write failed.");
      try {
        const receipt = bridgeWire.object(reply.receipt, value => value);
        const baseline = (value: unknown) => bridgeWire.object(value, field => ({
          value: decode(field["value"]), version: bridgeWire.integer(field["version"], 0, Number.MAX_SAFE_INTEGER),
        }));
        switch (receipt["kind"]) {
          case "applied": return { kind: "applied", snapshot: baseline(receipt["snapshot"]),
            ...(receipt["validation"] === undefined ? {} : { validation: bridgeWire.string(receipt["validation"]) }) };
          case "committed-with-error": return { kind: "committed-with-error", snapshot: baseline(receipt["snapshot"]), message: bridgeWire.string(receipt["message"]) };
          case "conflict": return { kind: "conflict", incoming: baseline(receipt["incoming"]), message: bridgeWire.string(receipt["message"]) };
          case "rejected": return { kind: "rejected", message: bridgeWire.string(receipt["message"]) };
          default: throw new TypeError("Unknown checked write receipt kind.");
        }
      } catch (cause) { throw new BridgeError("failed", `The Bridge returned an invalid checked write receipt: ${errorMessage(cause)}`, { cause, route: name }); }
    }),
    fieldBaseline(field) {
      if (lease.disposed || !isLive() || lease.current === undefined) throw new BridgeError("disconnected", "ViewModel is not connected.");
      if (!options.checkedFields?.includes(field)) throw new BridgeError("failed", "The requested checked field is unavailable.");
      const fields = (shared.wire as { readonly __runicFields?: Record<string, { readonly version?: unknown } | undefined> } | undefined)?.__runicFields;
      const version = fields && Object.hasOwn(fields, field) ? fields[field]?.version : undefined;
      if (typeof version !== "number" || !Number.isSafeInteger(version) || version < 0)
        throw new BridgeError("failed", "The checked field baseline is unavailable for this connection.");
      return { value: (lease.current as Record<string, unknown>)[field], version };
    },
    async startOperation(member, requestId, payload, decode, stream = false, decodeFailure) {
      if (requestId.length === 0) throw new RangeError("Operation requestId is required.");
      return observed(`${route}Start${member}`, () => operationSupport().start(operationScope, member, requestId, payload, decode, stream, decodeFailure));
    },
    recoverOperation: (member, requestId, decode, stream = false, decodeFailure) =>
      observed(`${route}Start${member}`, () => operationSupport().recover(operationScope, member, requestId, decode, stream, decodeFailure)),
  };
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
