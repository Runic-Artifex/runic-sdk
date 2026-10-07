import { BridgeError } from "./errors.js";
import { hostCallbacks, reportBridgeError, type RunicBridgeClient } from "./transport.js";

// The page-wide runtime lives on the window under a registered symbol. Every
// generated module, every copy of this package, and a runtime created by an
// earlier generated client (which inlined this code) share one instance, so a
// route has one callback and one revision no matter how many modules load.
// Field names are part of that compatibility: keep them stable.

export interface SharedLease {
  disposed: boolean;
  current: unknown | undefined;
  readonly listeners: Set<(state: unknown) => void>;
  mounted: boolean;
  readonly mountToken: string | undefined;
}

export interface SharedEntry {
  readonly contract: string;
  readonly route: string;
  readonly bridge: RunicBridgeClient;
  readonly routeEntry: SharedRoute;
  readonly leases: Set<SharedLease>;
  readonly hydrate: (wire: unknown) => unknown;
  readonly accept: (wire: unknown) => unknown;
  /** Stops pending timers when the entry is released. Absent in entries made by older copies of this package. */
  readonly close?: () => void;
  current: unknown | undefined;
  wire: unknown | undefined;
  revision: number | undefined;
  initializing: Promise<void> | undefined;
  active: boolean;
}

export interface SharedRoute {
  readonly route: string;
  readonly callbackName: string;
  readonly bridge: RunicBridgeClient;
  readonly generation: symbol;
  readonly entries: Map<string, SharedEntry>;
  readonly callback: (state: unknown) => unknown;
  readonly previousCallback: unknown;
  active: boolean;
}

export interface SharedRuntime {
  bridge: RunicBridgeClient | undefined;
  generation: symbol;
  mountSession: string;
  readonly routes: Map<string, SharedRoute>;
  readonly operations: Map<string, unknown>;
  reconnect?: (() => void) | undefined;
}

const sharedRuntimeKey = Symbol.for("runic.views.generated-client-runtime");

/** Returns the page runtime for a Bridge, resetting routes when the Bridge was replaced. */
export function sharedRuntimeFor(bridge: RunicBridgeClient): SharedRuntime {
  const host = hostCallbacks();
  let runtime = host[sharedRuntimeKey] as SharedRuntime | undefined;
  if (!runtime) {
    runtime = { bridge, generation: Symbol(), mountSession: globalThis.crypto.randomUUID(), routes: new Map(), operations: new Map() };
    host[sharedRuntimeKey] = runtime;
    watchReconnect(runtime, bridge);
    return runtime;
  }
  if (runtime.bridge === bridge) {
    // HMR can retain a runtime created by an earlier generated client.
    const legacy = runtime as SharedRuntime & { operations?: Map<string, unknown> };
    legacy.operations ??= new Map();
    watchReconnect(runtime, bridge);
    return runtime;
  }
  runtime.reconnect?.();
  runtime.reconnect = undefined;
  for (const route of runtime.routes.values()) {
    route.active = false;
    if (host[route.callbackName] === route.callback) host[route.callbackName] = route.previousCallback;
  }
  runtime.routes.clear();
  runtime.operations.clear();
  runtime.bridge = bridge;
  runtime.generation = Symbol();
  runtime.mountSession = globalThis.crypto.randomUUID();
  watchReconnect(runtime, bridge);
  return runtime;
}

// A transport reconnect keeps this page, while .NET released the former
// connection's View mounts and its publications were lost. Re-read each
// live route and re-acknowledge each mounted presentation.
function watchReconnect(runtime: SharedRuntime, bridge: RunicBridgeClient): void {
  runtime.reconnect ??= bridge.onReconnect?.(() => resumeAfterReconnect(runtime, bridge));
}

function resumeAfterReconnect(runtime: SharedRuntime, bridge: RunicBridgeClient): void {
  if (runtime.bridge !== bridge) return;
  for (const route of runtime.routes.values()) {
    if (!route.active || route.bridge !== bridge) continue;
    for (const entry of route.entries.values()) {
      if (!entry.active) continue;
      const snapshotRoute = `${route.route}Snapshot`;
      void bridge.call(snapshotRoute).then(json => {
        const reply = JSON.parse(json) as { readonly state?: unknown };
        if (entry.active && reply.state !== null && reply.state !== undefined) entry.accept(reply.state);
      }).catch(cause => {
        // The route keeps its last state and the next push or call refreshes it.
        if (!entry.active || runtime.bridge !== bridge) return;
        reportBridgeError(cause instanceof BridgeError ? cause : new BridgeError(bridge.isConnected() ? "failed" : "disconnected",
          "The state after a reconnect could not be read.", { cause, route: snapshotRoute }), snapshotRoute);
      });
      for (const lease of entry.leases)
        if (lease.mounted && lease.mountToken && !lease.disposed) void remountLease(bridge, route.route, lease, lease.mountToken);
    }
  }
}

async function remountLease(bridge: RunicBridgeClient, route: string, lease: SharedLease, token: string): Promise<void> {
  // .NET answers "ignored" while the former connection still owns the token.
  for (let attempt = 0; attempt < 20 && !lease.disposed; attempt++) {
    let reply: string;
    try { reply = await bridge.call(`${route}Mount`, token); }
    catch (cause) {
      // The View stays unmounted until the next reconnect re-sends the token.
      if (!lease.disposed) reportBridgeError(new BridgeError(bridge.isConnected() ? "failed" : "disconnected",
        "The View could not be mounted again after a reconnect.", { cause, route: `${route}Mount` }));
      return;
    }
    if (reply !== "ignored") return;
    await new Promise<void>(resolve => setTimeout(resolve, 250));
  }
}

/** Returns the live route of this runtime generation and installs its push callback. */
export function sharedRouteFor(runtime: SharedRuntime, bridge: RunicBridgeClient, route: string): SharedRoute {
  const callbackName = `__${route}Changed`;
  const callbacks = hostCallbacks();
  const existing = runtime.routes.get(route);
  if (existing?.active && existing.bridge === bridge && existing.generation === runtime.generation) {
    callbacks[callbackName] = existing.callback;
    return existing;
  }
  const sharedRoute: SharedRoute = {
    route, callbackName, bridge, generation: runtime.generation, entries: new Map(), previousCallback: callbacks[callbackName], active: true,
    callback(state) {
      let accepted: unknown;
      for (const entry of sharedRoute.entries.values()) {
        try { accepted = entry.accept(state); }
        catch (cause) {
          reportBridgeError(new BridgeError("failed", `The Bridge pushed an invalid state for ${route}: ${errorMessage(cause)}`,
            { cause, route: callbackName }));
        }
      }
      return accepted;
    },
  };
  runtime.routes.set(route, sharedRoute);
  callbacks[callbackName] = sharedRoute.callback;
  return sharedRoute;
}

/** The message of a caught value, for an error that wraps it. */
export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** Compares two decoded JSON values structurally. */
export function sameWire(left: unknown, right: unknown): boolean {
  if (left === right) return true;
  if (left === null || right === null || typeof left !== "object" || typeof right !== "object") return false;
  if (Array.isArray(left)) {
    if (!Array.isArray(right) || left.length !== right.length) return false;
    for (let index = 0; index < left.length; index++) if (!sameWire(left[index], right[index])) return false;
    return true;
  }
  if (Array.isArray(right)) return false;
  const leftKeys = Object.keys(left);
  if (leftKeys.length !== Object.keys(right).length) return false;
  for (const key of leftKeys)
    if (!Object.hasOwn(right, key) || !sameWire((left as Record<string, unknown>)[key], (right as Record<string, unknown>)[key])) return false;
  return true;
}
