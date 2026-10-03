import { BridgeError, BridgeOperationUncertainError, type BridgeErrorKind } from "./errors.js";
import { InteractionRuntime, type InteractionDefinition, type InteractionSurface } from "./interactions.js";
import { OperationChannel, type BridgeStreamOperation } from "./operations.js";
import { sharedRouteFor, sharedRuntimeFor, type SharedEntry, type SharedLease, type SharedRoute } from "./runtime.js";
import { hostCallbacks, reportBridgeError, waitForBridge, type RunicBridgeClient } from "./transport.js";
import { bridgeWire } from "./wire.js";

/** The framework-neutral surface every generated client shares. */
export interface ViewClient<TState> {
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
  readonly interactions?: Readonly<Record<string, InteractionDefinition>>;
}

/** The runtime half of a generated client. Generated code is its only intended caller. */
export interface ViewConnection<TState> extends ViewClient<TState> {
  readonly interactions: Readonly<Record<string, InteractionSurface>>;
  /** Calls a state-returning route. */
  invoke(name: string, ...args: unknown[]): Promise<TState>;
  /** Calls a command route after .NET acknowledged this View's interaction handlers. */
  command(name: string, ...args: unknown[]): Promise<TState>;
  /** Calls a `Can{Command}` route. */
  query(name: string, ...args: unknown[]): Promise<boolean>;
  writeField<T>(name: string, payload: string, decode: (value: unknown) => T): Promise<FieldWriteReceipt<T>>;
  fieldBaseline(field: string): FieldBaseline<unknown>;
  startOperation<TResult>(member: string, requestId: string, payload: () => string, decode: (value: unknown) => TResult,
    stream?: boolean): Promise<BridgeStreamOperation<TResult>>;
  recoverOperation<TResult>(member: string, requestId: string, decode: (value: unknown) => TResult,
    stream?: boolean): Promise<BridgeStreamOperation<TResult>>;
}

type Envelope = { readonly ok: boolean; readonly state: unknown; readonly error: { readonly kind: BridgeErrorKind; readonly message: string } | null };

const callFailed = (bridge: RunicBridgeClient, message: string) =>
  new BridgeError(bridge.isConnected() ? "failed" : "disconnected", message);

function unpack(json: string, entry: SharedEntry): unknown {
  let reply: Envelope;
  try { reply = JSON.parse(json) as Envelope; }
  catch { throw new BridgeError("failed", "The Bridge returned an invalid response."); }
  if (reply === null || typeof reply !== "object") throw new BridgeError("failed", "The Bridge returned an invalid response.");
  let state: unknown;
  try { state = reply.state === null ? undefined : entry.accept(reply.state); }
  catch { throw new BridgeError("failed", "The Bridge returned an invalid state."); }
  if (!reply.ok) throw new BridgeError(reply.error?.kind ?? "failed", reply.error?.message ?? "The call failed.");
  if (state === undefined) throw new BridgeError("failed", "The Bridge returned no state.");
  return state;
}

function createEntry(contract: string, route: string, bridge: RunicBridgeClient, routeEntry: SharedRoute,
  hydrate: (wire: unknown) => unknown): SharedEntry {
  const entry: SharedEntry = {
    contract, route, bridge, routeEntry, leases: new Set(), hydrate, current: undefined, wire: undefined, revision: undefined,
    initializing: undefined, active: true,
    accept(wire) {
      const revision = (wire as { readonly revision: number }).revision;
      if (!entry.active || !routeEntry.active) return entry.current ?? entry.hydrate(wire);
      if (entry.current === undefined || entry.revision === undefined || revision >= entry.revision) {
        // Decode first: a state that fails validation must not advance the revision.
        const current = entry.hydrate(wire);
        entry.revision = revision;
        entry.wire = wire;
        entry.current = current;
        for (const lease of entry.leases) if (!lease.disposed) {
          lease.current = current;
          for (const listener of lease.listeners) {
            try { listener(current); }
            catch (error) { reportBridgeError(error); }
          }
        }
      }
      return entry.current;
    },
  };
  routeEntry.entries.set(contract, entry);
  const initialization = (async () => {
    let reply: string;
    try { reply = await bridge.call(`${route}Snapshot`); }
    catch { throw new BridgeError(bridge.isConnected() ? "failed" : "disconnected", "The Bridge call could not complete."); }
    unpack(reply, entry);
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
  const { route } = options;
  const bridge = await waitForBridge();
  const runtime = sharedRuntimeFor(bridge);
  const contractId = `${options.contract}:${route}`;
  const routeEntry = sharedRouteFor(runtime, bridge, route);
  if (routeEntry.entries.size !== 0 && !routeEntry.entries.has(contractId))
    throw new BridgeError("failed", "This route already has an incompatible Bridge contract.");
  const shared = routeEntry.entries.get(contractId)
    ?? createEntry(contractId, route, bridge, routeEntry, wire => options.hydrate(wire as never));
  const mountToken = options.mount ? `${runtime.mountSession}:${globalThis.crypto.randomUUID()}` : undefined;
  const lease: SharedLease = { disposed: false, current: undefined, listeners: new Set(), mounted: false, mountToken };
  shared.leases.add(lease);
  const operations = new OperationChannel(bridge, contractId);
  let interactions: InteractionRuntime | undefined;

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
    catch { throw callFailed(bridge, "The Bridge call could not complete."); }
    if (lease.disposed || !isLive()) throw new BridgeError("disconnected", "This view was disposed. Reconnect for the current state.");
    return reply;
  }
  async function invoke(name: string, ...args: unknown[]): Promise<TState> {
    return unpack(await call(name, args), shared) as TState;
  }
  function dispose(): void {
    if (lease.disposed) return;
    lease.disposed = true;
    interactions?.dispose();
    if (lease.mounted && lease.mountToken) void bridge.call(`${route}Unmount`, lease.mountToken).catch(() => {});
    lease.listeners.clear();
    shared.leases.delete(lease);
    if (shared.leases.size !== 0 || routeEntry.entries.get(contractId) !== shared) return;
    shared.active = false;
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
      if (acknowledged !== "ok") throw new BridgeError("disconnected", "The View mount was not accepted.");
    } catch (cause) {
      dispose();
      throw cause instanceof BridgeError ? cause : new BridgeError("disconnected", "The View mount could not complete.");
    }
  }
  if (!isLive()) { dispose(); throw new BridgeError("disconnected", "The Bridge session changed during connection."); }
  if (options.interactions && Object.keys(options.interactions).length !== 0)
    interactions = new InteractionRuntime({ bridge, route, presentationId: mountToken, live: () => !lease.disposed && isLive() }, options.interactions);
  const ready = async () => { await interactions?.ready(); };

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
    invoke,
    async command(name, ...args) {
      await ready();
      return invoke(name, ...args);
    },
    async query(name, ...args) {
      assertConnected();
      let reply: string;
      try { reply = await bridge.call(name, ...args); }
      catch { throw callFailed(bridge, "The command availability query could not complete."); }
      if (reply === "true") return true;
      if (reply === "false") return false;
      throw new BridgeError("failed", "The command availability query returned an invalid response.");
    },
    async writeField(name, payload, decode) {
      const json = await call(name, [payload]);
      let reply: Envelope & { readonly receipt: unknown };
      try { reply = bridgeWire.object(JSON.parse(json), value => value) as unknown as typeof reply; }
      catch { throw new BridgeError("failed", "The Bridge returned an invalid response."); }
      if (reply.state !== null) shared.accept(reply.state);
      if (!reply.ok) throw new BridgeError(reply.error?.kind ?? "failed", reply.error?.message ?? "The checked write failed.");
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
      } catch { throw new BridgeError("failed", "The Bridge returned an invalid checked write receipt."); }
    },
    fieldBaseline(field) {
      if (lease.disposed || !isLive() || lease.current === undefined) throw new BridgeError("disconnected", "ViewModel is not connected.");
      if (!options.checkedFields?.includes(field)) throw new BridgeError("failed", "The requested checked field is unavailable.");
      const fields = (shared.wire as { readonly __runicFields?: Record<string, { readonly version?: unknown } | undefined> } | undefined)?.__runicFields;
      const version = fields && Object.hasOwn(fields, field) ? fields[field]?.version : undefined;
      if (typeof version !== "number" || !Number.isSafeInteger(version) || version < 0)
        throw new BridgeError("failed", "The checked field baseline is unavailable for this connection.");
      return { value: (lease.current as Record<string, unknown>)[field], version };
    },
    async startOperation(member, requestId, payload, decode, stream = false) {
      if (requestId.length === 0) throw new RangeError("Operation requestId is required.");
      assertConnected();
      await ready();
      let reply: string;
      try { reply = await bridge.call(`${route}Start${member}`, payload()); }
      catch {
        const recovered = await operations.status(member, requestId, false, decode);
        if (recovered.kind === "unknown" || recovered.kind === "expired")
          throw new BridgeOperationUncertainError(contractId, requestId, "The operation admission could not be recovered.");
        return operations.handle(member, requestId, decode, stream, recovered.kind === "running" ? undefined : recovered);
      }
      let admission: { readonly kind?: string; readonly reason?: string; readonly terminal?: unknown };
      try { admission = JSON.parse(reply) as typeof admission; }
      catch { throw new BridgeError("failed", "The operation service returned invalid JSON."); }
      if (admission.kind === "accepted" || admission.kind === "duplicate") {
        const terminal = admission.terminal === null || admission.terminal === undefined
          ? undefined : operations.parseStatus(JSON.stringify(admission.terminal), requestId, decode);
        return operations.handle(member, requestId, decode, stream, terminal);
      }
      throw new BridgeError(admission.kind === "rejected" ? "rejected" : "failed", admission.reason ?? "The operation was not accepted.");
    },
    recoverOperation: (member, requestId, decode, stream = false) => operations.recover(member, requestId, decode, stream),
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
