// Keep-alive leases (README, "Client lifetime"): a client that a binding lets go of stays connected for a
// short grace period, keyed by its connector's `connect` function, and the next binding that follows the same
// connector adopts it. StrictMode's second mount, Fast Refresh and HMR, `<Activity>`, an `{#if}` that toggles and
// A→B→A navigation then neither reopen the route nor show "Connecting…" again. A kept client belongs to the Bridge it
// was connected over: once its instance uses another Bridge (a new mock Bridge in the next test, a page runtime
// replaced at runtime), no binding takes it over, and it is released.
import { presentationBridge, refreshPresentation, type ViewClient } from "./connection.js";
import { bridgeOf, type Slots } from "./scope.js";

/** The grace period, in milliseconds, of a binding without a `keepAlive` option. */
export const defaultKeepAlive = 2_000;
// setTimeout runs a larger delay at once.
const maximumKeepAlive = 2_147_483_647;

/** A connector as the pool sees it: its `connect` function is the key. */
interface Connector { connect(): Promise<unknown> }

interface Kept {
  readonly connecting: Promise<ViewClient>;
  client: ViewClient | undefined;
  readonly release: (client: ViewClient) => void;
  timer: ReturnType<typeof setTimeout> | undefined;
  // The Bridge the client was connected over: its own once it connected, else the instance's when it was kept;
  // undefined while that is not known (a connection kept while it waits for the page's Bridge).
  bridge: unknown;
}

// One pool per instance: a connector of one instance never adopts a client of another. An entry leaves the pool when
// it is taken over or released, and a connector without entries leaves it too.
const pools = new WeakMap<Slots, Map<object, Kept[]>>();

function poolOf(slots: Slots): Map<object, Kept[]> {
  let pool = pools.get(slots);
  if (!pool) pools.set(slots, pool = new Map());
  return pool;
}

// Whether a binding of the instance with `slots` may take `kept` over: the instance still uses its Bridge.
function current(slots: Slots, kept: Kept): boolean {
  return kept.bridge === undefined || kept.bridge === bridgeOf(slots);
}

function releaseKept(kept: Kept): void {
  if (kept.client) kept.release(kept.client);
  else kept.connecting.then(kept.release, () => {});
}

/**
 * Checks a `keepAlive` option: a whole number of milliseconds from 0 to 2147483647, where 0 releases a client at
 * once. `undefined` is the default, {@link defaultKeepAlive}.
 */
export function checkKeepAlive(value: number | undefined, owner = "keepAlive"): number {
  if (value === undefined) return defaultKeepAlive;
  if (Number.isInteger(value) && value >= 0 && value <= maximumKeepAlive) return value;
  throw new RangeError(`${owner} must be a whole number of milliseconds from 0 to ${maximumKeepAlive}, not ${String(value)}.`);
}

function remove(pool: Map<object, Kept[]>, key: object, kept: Kept): boolean {
  const entries = pool.get(key);
  const index = entries?.indexOf(kept) ?? -1;
  if (index < 0) return false;
  entries!.splice(index, 1);
  if (entries!.length === 0) pool.delete(key);
  clearTimeout(kept.timer);
  return true;
}

/**
 * Keeps a connection that its binding no longer follows for `keepAlive` milliseconds, then calls `release`. A
 * connection that is still on its way is kept too: it is released when it resolves after its grace period ended.
 */
export function keepConnection<TClient extends ViewClient>(slots: Slots, connector: Connector, connecting: Promise<TClient>,
  client: TClient | undefined, keepAlive: number, release: (client: TClient) => void): void {
  const pool = poolOf(slots);
  const free = release as (client: ViewClient) => void;
  if (keepAlive === 0) {
    if (client) free(client);
    else connecting.then(free, () => {});
    return;
  }
  const key = connector.connect;
  const kept: Kept = {
    connecting, client, release: free, timer: undefined,
    bridge: (client && presentationBridge(client)) ?? bridgeOf(slots),
  };
  let entries = pool.get(key);
  if (!entries) pool.set(key, entries = []);
  entries.push(kept);
  kept.timer = setTimeout(() => {
    if (remove(pool, key, kept)) releaseKept(kept);
  }, keepAlive);
  if (!client) connecting.then(connected => {
    kept.client = connected;
    kept.bridge = presentationBridge(connected) ?? kept.bridge;
  }, () => { remove(pool, key, kept); });
}

/**
 * Releases the clients kept in the instance with `slots` at once: every one, or with `stale`, those of a Bridge the
 * instance no longer uses. Returns how many it released.
 */
export function releaseKeptConnections(slots: Slots, stale = false): number {
  const pool = pools.get(slots);
  if (!pool) return 0;
  let released = 0;
  for (const [key, entries] of [...pool]) {
    for (const kept of [...entries]) {
      if (stale && current(slots, kept)) continue;
      remove(pool, key, kept);
      releaseKept(kept);
      released++;
    }
  }
  return released;
}

// Releases the clients kept for `key` whose Bridge the instance no longer uses.
function releaseStale(slots: Slots, pool: Map<object, Kept[]>, key: object): void {
  for (const kept of [...pool.get(key) ?? []]) {
    if (current(slots, kept)) continue;
    remove(pool, key, kept);
    releaseKept(kept);
  }
}

/**
 * Takes the connection most recently kept for `connector`, or returns undefined. The caller owns it: it follows
 * `connecting` (or `client` at once, when it already connected) and releases it as if it had connected it.
 */
export function adoptConnection<TClient extends ViewClient>(slots: Slots, connector: Connector):
  { readonly connecting: Promise<TClient>; readonly client: TClient | undefined } | undefined {
  const pool = poolOf(slots);
  const key = connector.connect;
  releaseStale(slots, pool, key);
  const kept = latest(slots, pool.get(key));
  if (!kept) return undefined;
  remove(pool, key, kept);
  // The route may have been suspended and resumed meanwhile, which pushes no state.
  if (kept.client) refreshPresentation(kept.client);
  return { connecting: kept.connecting as Promise<TClient>, client: kept.client as TClient | undefined };
}

/**
 * The client most recently kept for `connector`, if it has connected, without taking it. A binding renders its state
 * before the effect that adopts it runs, so a component that mounts again shows its View at once.
 */
export function peekConnection<TClient extends ViewClient>(slots: Slots, connector: Connector): TClient | undefined {
  return latest(slots, pools.get(slots)?.get(connector.connect))?.client as TClient | undefined;
}

// Of the clients the instance's Bridge connected, a connected one before one still on its way, the most recently kept
// first.
function latest(slots: Slots, entries: readonly Kept[] | undefined): Kept | undefined {
  if (!entries) return undefined;
  let pending: Kept | undefined;
  for (let index = entries.length - 1; index >= 0; index--) {
    const kept = entries[index]!;
    if (!current(slots, kept)) continue;
    if (kept.client) return kept;
    pending ??= kept;
  }
  return pending;
}
