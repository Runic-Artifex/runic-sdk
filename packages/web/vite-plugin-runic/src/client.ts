import {
  sanitizeDiagnosticSummary,
  type RunicDiagnosticEntry,
  type RunicDiagnosticSource,
} from "./diagnostics.js";

export type {
  RunicDiagnosticDetail,
  RunicDiagnosticFailure,
  RunicDiagnosticDetailValue,
  RunicDiagnosticEntry,
  RunicDiagnosticSource,
  RunicTraceKind,
} from "./diagnostics.js";

export interface RunicRuntimeState {
  readonly contract?: Readonly<{
    identity?: string;
    version?: string;
    fingerprint?: string;
  }>;
  readonly connection?: Readonly<{
    state?: "connecting" | "connected" | "disconnected" | "closed";
    transport?: string;
    sessionId?: string;
    revision?: number;
    sequence?: number;
  }>;
  readonly operations?: readonly Readonly<{
    id: string;
    state: string;
    completed?: number;
    total?: number;
  }>[];
}

export interface RunicDiagnosticReporter {
  readonly report: (entry: Omit<RunicDiagnosticEntry, "source">) => void;
}

interface ViteHotContextLike {
  readonly data: Record<string, unknown>;
  send(event: string, data?: unknown): void;
  on(event: string, callback: (data: unknown) => void): void;
  prune(callback: () => void | Promise<void>): void;
}

interface ImportMetaWithHot extends ImportMeta {
  readonly hot?: ViteHotContextLike;
}

const hot = (import.meta as ImportMetaWithHot).hot;
const resourcesKey = "runic:resources";
const diagnosticEvent = "runic:diagnostic";
const resources = (hot?.data[resourcesKey] as Map<string, unknown> | undefined) ?? new Map<string, unknown>();
if (hot) hot.data[resourcesKey] = resources;

export function reportRunicState(state: RunicRuntimeState): void {
  hot?.send("runic:state", state);
}

/**
 * Reports a display-safe summary from an authoritative Runic subsystem.
 * This intentionally accepts no subsystem state or artifact model.
 */
export function reportRunicDiagnostic(entry: RunicDiagnosticEntry): void {
  sendDiagnostic(entry);
}

function sendDiagnostic(candidate: unknown): void {
  const summary = sanitizeDiagnosticSummary(candidate);
  if (summary) hot?.send(diagnosticEvent, summary);
}

export function createRunicDiagnosticReporter(
  source: RunicDiagnosticSource,
): RunicDiagnosticReporter {
  return {
    report: (entry) => reportRunicDiagnostic({ ...entry, source }),
  };
}

export function preserveRunicHmrResource<T>(key: string, create: () => T): T {
  if (resources.has(key)) return resources.get(key) as T;
  const resource = create();
  resources.set(key, resource);
  return resource;
}

export async function disposeRunicHmrResource(
  key: string,
  dispose: (resource: unknown) => void | Promise<void> = defaultDispose,
): Promise<void> {
  const resource = resources.get(key);
  if (resource === undefined) return;
  resources.delete(key);
  await dispose(resource);
}

async function defaultDispose(resource: unknown): Promise<void> {
  if (typeof resource !== "object" || resource === null || !("dispose" in resource)) return;
  const dispose = (resource as { dispose?: unknown }).dispose;
  if (typeof dispose === "function") await dispose.call(resource);
}

// @runic-artifex/views publishes runtime failures on a page-wide listener set
// under this registered symbol, so this client needs no dependency on it.
const viewsDiagnosticsKey = Symbol.for("runic.views.diagnostics");
const viewsListenerKey = "runic:views-listener";

interface ViewsDiagnostic {
  readonly kind?: unknown;
  readonly code?: unknown;
  readonly message?: unknown;
  readonly route?: unknown;
  readonly detail?: unknown;
  readonly member?: unknown;
  readonly requestId?: unknown;
}

// The error class a code comes from, for a failure without exception detail.
function fallbackType(code: string): string {
  if (code === "reported") return "Error";
  if (code === "uncertain") return "BridgeOperationUncertainError";
  return "BridgeError";
}

/** Forwards a Views runtime diagnostic to the DevTools timeline. */
function reportViewsDiagnostic(diagnostic: ViewsDiagnostic): void {
  const route = typeof diagnostic.route === "string" ? diagnostic.route.slice(0, 120) : undefined;
  const code = typeof diagnostic.code === "string" ? diagnostic.code.slice(0, 32) : "error";
  const message = typeof diagnostic.message === "string" ? diagnostic.message : "";
  const error = diagnostic.kind !== "operation";
  const member = typeof diagnostic.member === "string" ? diagnostic.member.slice(0, 80) : undefined;
  const requestId = typeof diagnostic.requestId === "string" ? diagnostic.requestId.slice(0, 80) : undefined;
  // The label stays short and path-free, so the timeline never redacts it.
  // An error's message, which may name files or packages, goes to the
  // failure, whose bounds keep paths.
  sendDiagnostic({
    source: "views",
    kind: error ? "error" : "operation",
    label: `${member ?? route ?? "Bridge"} ${code}`,
    detail: {
      code, ...(route ? { route } : {}), ...(member ? { member } : {}), ...(requestId ? { requestId } : {}),
      ...(error ? {} : { message }),
    },
    ...(error ? { failure: diagnostic.detail ?? { type: fallbackType(code), message } } : {}),
  });
}

function connectViewsDiagnostics(): void {
  if (!hot) return;
  const host = globalThis as unknown as Record<symbol, Set<(diagnostic: ViewsDiagnostic) => void> | undefined>;
  const listeners = host[viewsDiagnosticsKey] ??= new Set();
  // HMR re-evaluates this module; replace the listener of the previous copy.
  const previous = hot.data[viewsListenerKey] as ((diagnostic: ViewsDiagnostic) => void) | undefined;
  if (previous) listeners.delete(previous);
  listeners.add(reportViewsDiagnostic);
  hot.data[viewsListenerKey] = reportViewsDiagnostic;
}

connectViewsDiagnostics();
hot?.on("runic:state", () => undefined);
hot?.prune(async () => {
  for (const key of [...resources.keys()]) await disposeRunicHmrResource(key);
});
