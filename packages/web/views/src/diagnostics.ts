import { BridgeError, BridgeOperationUncertainError, type BridgeFailureDetail } from "./errors.js";

/** A failure or terminal event the Views runtime observed. */
export interface BridgeDiagnostic {
  readonly kind: "error" | "operation";
  /** A `BridgeErrorKind`, `uncertain`, `timedOut`, or `reported` for an application error the runtime caught. */
  readonly code: string;
  readonly message: string;
  /** The Bridge route, such as `counterIncrement`, when known. */
  readonly route?: string;
  /** Development-only .NET failure detail, or the type and stack of a caught browser error. */
  readonly detail?: BridgeFailureDetail;
  /** For an operation diagnostic: the command member and request id. */
  readonly member?: string;
  readonly requestId?: string;
  /** The error object, for local observers only. Never serialize it unfiltered. */
  readonly error?: unknown;
}

export type BridgeDiagnosticListener = (diagnostic: BridgeDiagnostic) => void;

// One listener set per page, shared by every copy of this package and read by
// `@runic-artifex/vite-plugin-runic/client` through the same registered symbol.
// The key and the Set shape are a compatibility contract between the packages.
const channelKey = Symbol.for("runic.views.diagnostics");

function listeners(): Set<BridgeDiagnosticListener> {
  const host = globalThis as unknown as Record<symbol, Set<BridgeDiagnosticListener> | undefined>;
  return host[channelKey] ??= new Set();
}

/**
 * Observes Views runtime failures: failed routes (with their .NET detail in
 * development), a missing or unconnected Bridge, operation timeouts and
 * errors the runtime caught from listeners. The Vite plugin forwards these to
 * the Runic DevTools dock. Returns a function that stops observing.
 */
export function onBridgeDiagnostic(listener: BridgeDiagnosticListener): () => void {
  const set = listeners();
  set.add(listener);
  return () => { set.delete(listener); };
}

// An error passes several layers (route call, connection, framework adapter)
// and possibly several copies of this package; it is reported at most once.
// The mark is a registered symbol on the error, shared by every copy. A frozen
// error cannot be marked, so it falls back to this copy's set.
const reportedMark = Symbol.for("runic.views.diagnosticReported");
const reportedFrozen = new WeakSet<object>();

function markReported(error: object): boolean {
  if ((error as Record<symbol, unknown>)[reportedMark] === true || reportedFrozen.has(error)) return false;
  if (!Reflect.defineProperty(error, reportedMark, { value: true })) reportedFrozen.add(error);
  return true;
}

export function emitBridgeDiagnostic(diagnostic: BridgeDiagnostic): void {
  const error = diagnostic.error;
  if (typeof error === "object" && error !== null && !markReported(error)) return;
  for (const listener of [...listeners()]) {
    try { listener(diagnostic); } catch (failure) { console.error(failure); }
  }
}

/** Emits a diagnostic for an error that leaves the runtime. */
export function emitErrorDiagnostic(error: unknown, route?: string): void {
  if (error instanceof BridgeError) {
    const errorRoute = error.route ?? route;
    emitBridgeDiagnostic({
      kind: "error", code: error.kind, message: error.message, error,
      ...(errorRoute === undefined ? {} : { route: errorRoute }),
      ...(error.detail === undefined ? {} : { detail: error.detail }),
    });
  } else if (error instanceof BridgeOperationUncertainError) {
    emitBridgeDiagnostic({ kind: "error", code: "uncertain", message: error.message, error, ...(route === undefined ? {} : { route }) });
  } else {
    // A browser error already has its stack; DevTools shows it like .NET detail.
    const detail = error instanceof Error
      ? { type: error.name, message: error.message, ...(typeof error.stack === "string" ? { stack: error.stack } : {}) }
      : undefined;
    emitBridgeDiagnostic({
      kind: "error", code: "reported", message: error instanceof Error ? error.message : String(error), error,
      ...(route === undefined ? {} : { route }),
      ...(detail === undefined ? {} : { detail }),
    });
  }
}
