export type RunicToolkitTraceKind =
  | "command"
  | "receipt"
  | "event"
  | "operation"
  | "connection"
  | "error";

export interface RunicToolkitTraceEntry {
  readonly id?: string;
  readonly timestamp?: string;
  readonly kind: RunicToolkitTraceKind;
  readonly label: string;
  readonly detail?: Readonly<Record<string, unknown>>;
}

export interface RunicToolkitRuntimeState {
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

export interface RunicToolkitDevtoolsObserver {
  readonly state: (state: RunicToolkitRuntimeState) => void;
  readonly trace: (entry: RunicToolkitTraceEntry) => void;
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
const resourcesKey = "runicToolkit:resources";
const resources = (hot?.data[resourcesKey] as Map<string, unknown> | undefined) ?? new Map<string, unknown>();
if (hot) hot.data[resourcesKey] = resources;

export function reportRunicToolkitState(state: RunicToolkitRuntimeState): void {
  hot?.send("runic-toolkit:state", state);
}

export function traceRunicToolkitEvent(entry: RunicToolkitTraceEntry): void {
  hot?.send("runic-toolkit:trace", entry);
}

export function createRunicToolkitDevtoolsObserver(): RunicToolkitDevtoolsObserver {
  return {
    state: reportRunicToolkitState,
    trace: traceRunicToolkitEvent,
  };
}

export function preserveRunicToolkitHmrResource<T>(key: string, create: () => T): T {
  if (resources.has(key)) return resources.get(key) as T;
  const resource = create();
  resources.set(key, resource);
  return resource;
}

export async function disposeRunicToolkitHmrResource(
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

hot?.on("runic-toolkit:state", () => undefined);
hot?.prune(async () => {
  for (const key of [...resources.keys()]) await disposeRunicToolkitHmrResource(key);
});

