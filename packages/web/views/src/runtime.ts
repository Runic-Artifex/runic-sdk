import { emitBridgeDiagnostic, suppressErrorDiagnostic } from "./diagnostics.js";
import { BridgeError } from "./errors.js";
import { hostCallbacks, reportBridgeError, type RunicBridgeClient } from "./transport.js";
import { bridgeProtocol, viewsVersion } from "./version.js";

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
  mountToken: string | undefined;
  /**
   * Moves the lease's mount token to a new browser session and returns it. Absent in leases made by older copies
   * of this package, which re-send their former token.
   */
  renewMountToken?: ((session: string) => string) | undefined;
  /** Restarts the lease's interaction handlers after a reconnect remounted it. Absent in leases made by older copies of this package. */
  resumeInteractions?: (() => void) | undefined;
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
// live route and mount each mounted presentation again.
function watchReconnect(runtime: SharedRuntime, bridge: RunicBridgeClient): void {
  runtime.reconnect ??= bridge.onReconnect?.(() => resumeAfterReconnect(runtime, bridge));
}

function resumeAfterReconnect(runtime: SharedRuntime, bridge: RunicBridgeClient): void {
  if (runtime.bridge !== bridge) return;
  // .NET rejects a browser session once a connection that used it closed. A
  // CS-WebUI reconnect can keep the connection key, so only a new session tells
  // this page's mounts from late calls of the closed connection.
  runtime.mountSession = globalThis.crypto.randomUUID();
  for (const route of runtime.routes.values()) {
    if (!route.active || route.bridge !== bridge) continue;
    for (const entry of route.entries.values()) {
      if (!entry.active) continue;
      const snapshotRoute = `${route.route}Snapshot`;
      void bridge.call(snapshotRoute).then(json => {
        const reply = JSON.parse(json) as { readonly ok?: unknown; readonly state?: unknown; readonly protocol?: unknown; readonly version?: unknown };
        // A host rebuilt with packages of another release fails the read loudly instead of drifting.
        if (reply !== null && typeof reply === "object") checkSnapshotProtocol(reply, bridge, snapshotRoute);
        if (entry.active && reply.state !== null && reply.state !== undefined) entry.accept(reply.state);
      }).catch(cause => {
        // The route keeps its last state and the next push or call refreshes it.
        if (!entry.active || runtime.bridge !== bridge) return;
        reportBridgeError(cause instanceof BridgeError ? cause : new BridgeError(bridge.isConnected() ? "failed" : "disconnected",
          "The state after a reconnect could not be read.", { cause, route: snapshotRoute }), snapshotRoute);
      });
      for (const lease of entry.leases)
        if (lease.mounted && lease.mountToken && !lease.disposed)
          void remountLease(bridge, route.route, lease, lease.renewMountToken?.(runtime.mountSession) ?? lease.mountToken);
    }
  }
}

async function remountLease(bridge: RunicBridgeClient, route: string, lease: SharedLease, token: string): Promise<void> {
  // .NET answers "ignored" while the former connection still owns a re-sent token.
  for (let attempt = 0; attempt < 20 && !lease.disposed && lease.mountToken === token; attempt++) {
    let reply: string;
    try { reply = await bridge.call(`${route}Mount`, token); }
    catch (cause) {
      // The View stays unmounted until the next reconnect re-sends the token.
      if (!lease.disposed) reportBridgeError(new BridgeError(bridge.isConnected() ? "failed" : "disconnected",
        "The View could not be mounted again after a reconnect.", { cause, route: `${route}Mount` }));
      return;
    }
    // The interaction handlers resume under the token's presentation id.
    if (reply === "ok" && !lease.disposed && lease.mountToken === token) lease.resumeInteractions?.();
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

const protocolReported = new WeakSet<object>();
const versionReported = new WeakSet<object>();

/**
 * Checks the `protocol` and `version` of a snapshot reply. This client accepts only a host of its own
 * protocol: a reply that states another protocol, or a successful one that states none, fails the read
 * as `failed`. The `protocol` diagnostic, once per Bridge, is the only report of such failures. A reply
 * without `protocol` that is an error, such as a host's `released` envelope of an unbound route, is
 * handled as an error. The package version is informational: another one is a `version` diagnostic,
 * once per Bridge.
 */
export function checkSnapshotProtocol(reply: { readonly ok?: unknown; readonly protocol?: unknown; readonly version?: unknown },
  bridge: object, route: string): void {
  if (reply.protocol !== bridgeProtocol && (reply.protocol !== undefined || reply.ok === true)) {
    const host = reply.protocol === undefined ? "does not state a Views protocol" : `uses Views protocol ${String(reply.protocol)}`;
    const error = new BridgeError("failed", `The .NET host ${host}, but this client implements protocol ${bridgeProtocol}, so the View ` +
      "cannot connect. Use Runic.Application.Views and @runic-artifex/views of one release, rebuild so the generated clients " +
      "are regenerated, and reload the page.", { route });
    if (protocolReported.has(bridge)) suppressErrorDiagnostic(error);
    else {
      protocolReported.add(bridge);
      emitBridgeDiagnostic({ kind: "error", code: "protocol", route, message: error.message, error });
    }
    throw error;
  }
  if (typeof reply.version === "string" && reply.version !== viewsVersion && !versionReported.has(bridge)) {
    versionReported.add(bridge);
    emitBridgeDiagnostic({
      kind: "error", code: "version", route,
      message: `The .NET host runs Runic.Application.Views ${reply.version}, but this page uses @runic-artifex/views ${viewsVersion}. ` +
        "Use the .NET and npm packages of one release.",
    });
  }
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
