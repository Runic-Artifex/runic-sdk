/// <reference types="@vitejs/devtools-kit" />

import { createRequire } from "node:module";
import { join } from "node:path";
import type { Plugin, ResolvedConfig, ViteDevServer } from "vite";
import type { JsonRenderer, JsonRenderSpec } from "@vitejs/devtools-kit";
import type {
  RunicToolkitRuntimeState,
  RunicToolkitTraceEntry,
} from "./client.js";

export type {
  RunicToolkitDevtoolsObserver,
  RunicToolkitRuntimeState,
  RunicToolkitTraceEntry,
  RunicToolkitTraceKind,
} from "./client.js";

const virtualClientId = "virtual:runic-toolkit/client";
const resolvedVirtualClientId = `\0${virtualClientId}`;
const stateEvent = "runic-toolkit:state";
const traceEvent = "runic-toolkit:trace";
const stateKey = "runic-toolkit:state";

export interface RunicToolkitViteOptions {
  readonly contract?: Readonly<{
    identity?: string;
    version?: string;
    fingerprint?: string;
  }>;
  readonly devtools?: boolean | "auto";
  readonly devtoolsVisibility?: "normal" | "passive" | "hidden";
  readonly maxTimelineEntries?: number;
}

export interface RunicToolkitDevelopmentState {
  readonly contract: Readonly<{
    identity: string;
    version: string;
    fingerprint: string;
  }>;
  readonly vite: Readonly<{
    command: string;
    mode: string;
    root: string;
  }>;
  readonly connection: Readonly<{
    state: string;
    transport: string;
    sessionId: string;
    revision: number;
    sequence: number;
  }>;
  readonly operations: readonly Readonly<{
    id: string;
    state: string;
    completed: number;
    total: number;
  }>[];
  readonly timeline: readonly Readonly<Required<Pick<RunicToolkitTraceEntry, "id" | "timestamp" | "kind" | "label">> & {
    detail: Readonly<Record<string, string | number | boolean | null>>;
  }>[];
}

export function runicToolkit(options: RunicToolkitViteOptions = {}): Plugin {
  const maxTimelineEntries = options.maxTimelineEntries ?? 200;
  let config: ResolvedConfig | undefined;
  let server: ViteDevServer | undefined;
  let injectDevtoolsClient = false;
  let sharedState: { mutate: (update: (draft: RunicToolkitDevelopmentState) => void) => void } | undefined;
  let jsonRenderer: JsonRenderer | undefined;
  let state = initialState(options);

  const publish = (): void => {
    sharedState?.mutate((draft) => Object.assign(draft, structuredClone(state)));
    void jsonRenderer?.updateSpec(createDevtoolsSpec(state));
    server?.ws.send({ type: "custom", event: stateEvent, data: state });
  };

  const applyRuntimeState = (candidate: unknown): void => {
    const update = sanitizeRuntimeState(candidate);
    state = {
      ...state,
      ...(update.contract ? { contract: { ...state.contract, ...update.contract } } : {}),
      ...(update.connection ? { connection: { ...state.connection, ...update.connection } } : {}),
      ...(update.operations ? {
        operations: update.operations.map((operation) => ({
          id: operation.id,
          state: operation.state,
          completed: operation.completed ?? 0,
          total: operation.total ?? 0,
        })),
      } : {}),
    };
    publish();
  };

  const appendTrace = (candidate: unknown): void => {
    const entry = sanitizeTrace(candidate);
    if (!entry) return;
    state = {
      ...state,
      timeline: [...state.timeline, entry].slice(-maxTimelineEntries),
    };
    publish();
  };

  return {
    name: "runic-toolkit",
    enforce: "pre",
    configResolved(resolved) {
      config = resolved;
      injectDevtoolsClient = resolved.command === "serve" &&
        resolveDevtoolsAvailability(resolved.root, options.devtools ?? "auto");
      state = {
        ...state,
        vite: { command: resolved.command, mode: resolved.mode, root: resolved.root },
      };
    },
    configureServer(viteServer) {
      server = viteServer;
      viteServer.ws.on(stateEvent, (payload) => applyRuntimeState(payload));
      viteServer.ws.on(traceEvent, (payload) => appendTrace(payload));
      viteServer.middlewares.use("/__runic-toolkit/state", (_request, response) => {
        response.statusCode = 200;
        response.setHeader("Content-Type", "application/json; charset=utf-8");
        response.setHeader("Cache-Control", "no-store");
        response.end(JSON.stringify(state));
      });
      viteServer.httpServer?.once("close", () => {
        server = undefined;
      });
    },
    resolveId(id) {
      return id === virtualClientId ? resolvedVirtualClientId : undefined;
    },
    load(id) {
      if (id !== resolvedVirtualClientId) return undefined;
      const injector = injectDevtoolsClient
        ? `import ${JSON.stringify(devtoolsInjector(options.devtoolsVisibility ?? "passive"))};\n`
        : "";
      return `${injector}export * from ${JSON.stringify("@runic-artifex/vite-plugin-runic-toolkit/client")};`;
    },
    devtools: {
      async setup(context) {
        sharedState = await context.rpc.sharedState.get(stateKey, { initialValue: state });
        const renderer = context.createJsonRenderer(createDevtoolsSpec(state));
        jsonRenderer = renderer;
        context.docks.register({
          id: "runic-toolkit:overview",
          title: "Runic Toolkit",
          icon: "ph:diamond-duotone",
          type: "json-render",
          ui: renderer,
        });
        context.commands.register({
          id: "runic-toolkit:copy-diagnostic-state",
          title: "Runic Toolkit: Copy sanitized diagnostic state",
          handler: () => JSON.stringify(state, null, 2),
        });
      },
    },
  };
}

function initialState(options: RunicToolkitViteOptions): RunicToolkitDevelopmentState {
  return {
    contract: {
      identity: cleanString(options.contract?.identity),
      version: cleanString(options.contract?.version),
      fingerprint: cleanString(options.contract?.fingerprint),
    },
    vite: { command: "serve", mode: "development", root: "" },
    connection: {
      state: "disconnected",
      transport: "unknown",
      sessionId: "",
      revision: 0,
      sequence: 0,
    },
    operations: [],
    timeline: [],
  };
}

function resolveDevtoolsAvailability(root: string, requested: boolean | "auto"): boolean {
  if (requested === false) return false;
  const require = createRequire(join(root, "package.json"));
  try {
    require.resolve("@vitejs/devtools/client/inject-passive");
    return true;
  } catch {
    return false;
  }
}

function devtoolsInjector(visibility: "normal" | "passive" | "hidden"): string {
  return visibility === "normal"
    ? "@vitejs/devtools/client/inject"
    : `@vitejs/devtools/client/inject-${visibility}`;
}

function sanitizeRuntimeState(candidate: unknown): RunicToolkitRuntimeState {
  if (!isRecord(candidate)) return {};
  const contract = isRecord(candidate.contract) ? {
    identity: cleanString(candidate.contract.identity),
    version: cleanString(candidate.contract.version),
    fingerprint: cleanString(candidate.contract.fingerprint),
  } : undefined;
  const connection = isRecord(candidate.connection) ? {
    state: connectionState(candidate.connection.state),
    transport: cleanString(candidate.connection.transport),
    sessionId: cleanString(candidate.connection.sessionId),
    revision: finiteNumber(candidate.connection.revision),
    sequence: finiteNumber(candidate.connection.sequence),
  } : undefined;
  const operations: RunicToolkitDevelopmentState["operations"] | undefined = Array.isArray(candidate.operations)
    ? candidate.operations.slice(0, 64).flatMap((operation) => isRecord(operation) ? [{
        id: cleanString(operation.id),
        state: cleanString(operation.state),
        completed: finiteNumber(operation.completed),
        total: finiteNumber(operation.total),
      }] : [])
    : undefined;
  return {
    ...(contract ? { contract } : {}),
    ...(connection ? { connection } : {}),
    ...(operations ? { operations } : {}),
  };
}

function sanitizeTrace(candidate: unknown): RunicToolkitDevelopmentState["timeline"][number] | undefined {
  if (!isRecord(candidate)) return undefined;
  const allowedKinds = new Set(["command", "receipt", "event", "operation", "connection", "error"]);
  const kind = cleanString(candidate.kind);
  const label = cleanString(candidate.label);
  if (!allowedKinds.has(kind) || label.length === 0) return undefined;
  return {
    id: cleanString(candidate.id) || crypto.randomUUID(),
    timestamp: cleanString(candidate.timestamp) || new Date().toISOString(),
    kind: kind as RunicToolkitDevelopmentState["timeline"][number]["kind"],
    label,
    detail: sanitizeDetail(candidate.detail),
  };
}

function sanitizeDetail(candidate: unknown): Readonly<Record<string, string | number | boolean | null>> {
  if (!isRecord(candidate)) return {};
  const output: Record<string, string | number | boolean | null> = {};
  for (const [key, value] of Object.entries(candidate).slice(0, 32)) {
    if (/token|secret|capability|password|path|frame|stack/iu.test(key)) continue;
    if (typeof value === "string") output[key] = cleanString(value);
    else if (typeof value === "number" && Number.isFinite(value)) output[key] = value;
    else if (typeof value === "boolean" || value === null) output[key] = value;
  }
  return output;
}

function createDevtoolsSpec(current: RunicToolkitDevelopmentState): JsonRenderSpec {
  const recent = current.timeline.slice(-12).reverse();
  return {
    root: "root",
    elements: {
      root: {
        type: "Stack",
        props: { direction: "column", gap: 12 },
        children: ["heading", "connection", "contract", "operations", "timeline"],
      },
      heading: { type: "Text", props: { content: "Runic Toolkit", variant: "heading" } },
      connection: {
        type: "KeyValueTable",
        props: {
          title: "Application Bridge",
          entries: [
            { key: "State", value: current.connection.state },
            { key: "Transport", value: current.connection.transport },
            { key: "Session", value: current.connection.sessionId || "not initialized" },
            { key: "Revision", value: String(current.connection.revision) },
            { key: "Sequence", value: String(current.connection.sequence) },
          ],
        },
      },
      contract: {
        type: "KeyValueTable",
        props: {
          title: "Contract",
          entries: [
            { key: "Identity", value: current.contract.identity || "not reported" },
            { key: "Version", value: current.contract.version || "not reported" },
            { key: "Fingerprint", value: current.contract.fingerprint || "not reported" },
          ],
        },
      },
      operations: {
        type: "KeyValueTable",
        props: {
          title: `Operations (${current.operations.length})`,
          entries: current.operations.length === 0
            ? [{ key: "Active", value: "none" }]
            : current.operations.map((operation) => ({
                key: operation.id,
                value: `${operation.state} ${operation.completed}/${operation.total}`,
              })),
        },
      },
      timeline: {
        type: "KeyValueTable",
        props: {
          title: "Recent timeline",
          entries: recent.length === 0
            ? [{ key: "Events", value: "none" }]
            : recent.map((entry) => ({
                key: `${entry.kind} · ${entry.timestamp}`,
                value: entry.label,
              })),
        },
      },
    },
  };
}

function connectionState(value: unknown): "connecting" | "connected" | "disconnected" | "closed" {
  return value === "connecting" || value === "connected" || value === "closed"
    ? value
    : "disconnected";
}

function cleanString(value: unknown): string {
  return typeof value === "string" ? value.slice(0, 512) : "";
}

function finiteNumber(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
