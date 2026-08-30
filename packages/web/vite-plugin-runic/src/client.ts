import {
  sanitizeDiagnosticSummary,
  type RunicDiagnosticEntry,
  type RunicDiagnosticSource,
  type RunicTraceEntry,
} from "./diagnostics.js";

export type {
  RunicDiagnosticDetail,
  RunicDiagnosticDetailValue,
  RunicDiagnosticEntry,
  RunicDiagnosticSource,
  RunicTraceEntry,
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

export interface RunicDevtoolsObserver {
  readonly state: (state: RunicRuntimeState) => void;
  readonly trace: (entry: RunicTraceEntry) => void;
  readonly diagnostic?: (entry: Omit<RunicDiagnosticEntry, "source">) => void;
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

export function traceRunicEvent(entry: RunicTraceEntry): void {
  sendDiagnostic({ ...entry, source: "application-bridge" });
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

export function createRunicDevtoolsObserver(): RunicDevtoolsObserver {
  return {
    state: reportRunicState,
    trace: traceRunicEvent,
    diagnostic: createRunicDiagnosticReporter("application-bridge").report,
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

hot?.on("runic:state", () => undefined);
hot?.prune(async () => {
  for (const key of [...resources.keys()]) await disposeRunicHmrResource(key);
});
