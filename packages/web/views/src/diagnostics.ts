import { BridgeError, BridgeOperationUncertainError, type BridgeErrorKind, type BridgeFailureDetail } from "./errors.js";
import { currentSlots, type Slots } from "./scope.js";

/**
 * What a {@link BridgeDiagnostic} reports: a `BridgeErrorKind` other than `released`
 * (a call of a released View is expected after navigation and is not reported),
 * `uncertain` for a `BridgeOperationUncertainError`, `timed-out` for an operation
 * wait that passed its timeout, `protocol` for a host that speaks another Views
 * protocol version, `version` for a .NET `Runic.Application.Views` of another release
 * than this `@runic-artifex/views` (no other producer is compared), `revision-timeout` for a reply
 * whose revision the route did not reach within 5 seconds (a producer bug: the client then
 * reads a snapshot), `stale-input` for script that wrote an older value into a focused input
 * while a request was in flight (`@runic-artifex/vite-plugin-runic` in development), or `reported`
 * for an application error the runtime caught. Open: a minor release may add a code, so handle
 * unknown codes.
 */
export type BridgeDiagnosticCode =
  | Exclude<BridgeErrorKind, "released"> | "uncertain" | "timed-out" | "protocol" | "version" | "revision-timeout"
  | "stale-input" | "reported";

/** A failure or terminal event the Views runtime observed. */
export interface BridgeDiagnostic {
  readonly kind: "error" | "operation";
  /** What happened; see {@link BridgeDiagnosticCode}. */
  readonly code: BridgeDiagnosticCode;
  readonly message: string;
  /** The View route, such as `counter`, when known. */
  readonly route?: string;
  /** Development-only producer failure detail, or the type and stack of a caught browser error. */
  readonly detail?: BridgeFailureDetail;
  /** For an operation diagnostic: the command member and request id. */
  readonly member?: string;
  readonly requestId?: string;
  /** The error object, for local observers only. Never serialize it unfiltered. */
  readonly error?: unknown;
}

export type BridgeDiagnosticListener = (diagnostic: BridgeDiagnostic) => void;

// One listener set per instance, shared by every copy of this package and, for the page instance, read by
// `@runic-artifex/vite-plugin-runic/client` through the same registered symbol on `globalThis`.
// The key and the Set shape are a compatibility contract between the packages.
const channelKey = Symbol.for("runic.views.diagnostics");

function listeners(slots: Slots): Set<BridgeDiagnosticListener> {
  return (slots[channelKey] ??= new Set()) as Set<BridgeDiagnosticListener>;
}

/**
 * Observes Views runtime failures of the current instance (the page's, outside `RunicInstance.run`): failed routes
 * (with their .NET detail in development), a missing or unconnected Bridge, operation timeouts and errors the runtime
 * caught from listeners. The Vite plugin forwards the page's to the Runic DevTools dock. Returns a function that stops
 * observing.
 */
export function onBridgeDiagnostic(listener: BridgeDiagnosticListener): () => void {
  const set = listeners(currentSlots());
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

/** Marks an error as reported without a diagnostic, because another diagnostic already stated its cause. */
export function suppressErrorDiagnostic(error: object): void {
  markReported(error);
}

/** @internal Reports `diagnostic` to the listeners of the instance with `slots`. */
export function emitBridgeDiagnostic(slots: Slots, diagnostic: BridgeDiagnostic): void {
  const error = diagnostic.error;
  if (typeof error === "object" && error !== null && !markReported(error)) return;
  for (const listener of [...listeners(slots)]) {
    try { listener(diagnostic); } catch (failure) { console.error(failure); }
  }
}

/** Emits a diagnostic for an error that leaves the runtime. A `released` BridgeError is not reported. */
export function emitErrorDiagnostic(slots: Slots, error: unknown, route?: string): void {
  if (error instanceof BridgeError) {
    if (error.kind === "released") return;
    const errorRoute = error.route ?? route;
    emitBridgeDiagnostic(slots, {
      kind: "error", code: error.kind, message: error.message, error,
      ...(errorRoute === undefined ? {} : { route: errorRoute }),
      ...(error.detail === undefined ? {} : { detail: error.detail }),
    });
  } else if (error instanceof BridgeOperationUncertainError) {
    const errorRoute = error.route ?? route;
    emitBridgeDiagnostic(slots, { kind: "error", code: "uncertain", message: error.message, error, ...(errorRoute === undefined ? {} : { route: errorRoute }) });
  } else {
    // A browser error already has its stack; DevTools shows it like .NET detail.
    const detail = error instanceof Error
      ? { type: error.name, message: error.message, ...(typeof error.stack === "string" ? { stack: error.stack } : {}) }
      : undefined;
    emitBridgeDiagnostic(slots, {
      kind: "error", code: "reported", message: error instanceof Error ? error.message : String(error), error,
      ...(route === undefined ? {} : { route }),
      ...(detail === undefined ? {} : { detail }),
    });
  }
}
