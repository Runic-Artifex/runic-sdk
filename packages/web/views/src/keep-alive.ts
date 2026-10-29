// Keep-alive leases (README, "Client lifetime"): a client that a binding lets go of stays connected for a
// short grace period, keyed by its connector's `connect` function, and the next binding that follows the same
// connector adopts it. StrictMode's second mount, Fast Refresh and HMR, `<Activity>`, an `{#if}` that toggles and
// A→B→A navigation then neither reopen the route nor show "Connecting…" again.
import { refreshPresentation, type ViewClient } from "./connection.js";

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
}

const pool = new WeakMap<object, Kept[]>();

/**
 * Checks a `keepAlive` option: a whole number of milliseconds from 0 to 2147483647, where 0 releases a client at
 * once. `undefined` is the default, {@link defaultKeepAlive}.
 */
export function checkKeepAlive(value: number | undefined, owner = "keepAlive"): number {
  if (value === undefined) return defaultKeepAlive;
  if (Number.isInteger(value) && value >= 0 && value <= maximumKeepAlive) return value;
  throw new RangeError(`${owner} must be a whole number of milliseconds from 0 to ${maximumKeepAlive}, not ${String(value)}.`);
}

function remove(key: object, kept: Kept): boolean {
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
export function keepConnection<TClient extends ViewClient>(connector: Connector, connecting: Promise<TClient>,
  client: TClient | undefined, keepAlive: number, release: (client: TClient) => void): void {
  const free = release as (client: ViewClient) => void;
  if (keepAlive === 0) {
    if (client) free(client);
    else connecting.then(free, () => {});
    return;
  }
  const key = connector.connect;
  const kept: Kept = { connecting, client, release: free, timer: undefined };
  let entries = pool.get(key);
  if (!entries) pool.set(key, entries = []);
  entries.push(kept);
  kept.timer = setTimeout(() => {
    if (!remove(key, kept)) return;
    if (kept.client) kept.release(kept.client);
    else kept.connecting.then(kept.release, () => {});
  }, keepAlive);
  if (!client) connecting.then(connected => { kept.client = connected; }, () => { remove(key, kept); });
}

/**
 * Takes the connection most recently kept for `connector`, or returns undefined. The caller owns it: it follows
 * `connecting` (or `client` at once, when it already connected) and releases it as if it had connected it.
 */
export function adoptConnection<TClient extends ViewClient>(connector: Connector):
  { readonly connecting: Promise<TClient>; readonly client: TClient | undefined } | undefined {
  const key = connector.connect;
  const kept = latest(pool.get(key));
  if (!kept) return undefined;
  remove(key, kept);
  // The route may have been suspended and resumed meanwhile, which pushes no state.
  if (kept.client) refreshPresentation(kept.client);
  return { connecting: kept.connecting as Promise<TClient>, client: kept.client as TClient | undefined };
}

/**
 * The client most recently kept for `connector`, if it has connected, without taking it. A binding renders its state
 * before the effect that adopts it runs, so a component that mounts again shows its View at once.
 */
export function peekConnection<TClient extends ViewClient>(connector: Connector): TClient | undefined {
  return latest(pool.get(connector.connect))?.client as TClient | undefined;
}

// A connected client before one still on its way, the most recently kept first.
function latest(entries: readonly Kept[] | undefined): Kept | undefined {
  if (!entries) return undefined;
  for (let index = entries.length - 1; index >= 0; index--) if (entries[index]!.client) return entries[index];
  return entries[entries.length - 1];
}
