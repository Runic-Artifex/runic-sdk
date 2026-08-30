/// <reference types="@vitejs/devtools-kit" />

import { createRequire } from "node:module";
import { join } from "node:path";
import type { Plugin, ResolvedConfig, ViteDevServer } from "vite";
import type { JsonRenderer, JsonRenderSpec } from "@vitejs/devtools-kit";
import type {
  RunicDiagnosticEntry,
  RunicDiagnosticDetail,
  RunicDiagnosticSource,
  RunicRuntimeState,
  RunicTraceEntry,
} from "./client.js";
import { sanitizeDiagnosticSummary } from "./diagnostics.js";

export type {
  RunicDevtoolsObserver,
  RunicDiagnosticDetail,
  RunicDiagnosticDetailValue,
  RunicDiagnosticEntry,
  RunicDiagnosticReporter,
  RunicDiagnosticSource,
  RunicRuntimeState,
  RunicTraceEntry,
  RunicTraceKind,
} from "./client.js";

const virtualClientId = "virtual:runic/client";
const resolvedVirtualClientId = `\0${virtualClientId}`;
const legacyVirtualClientId = "virtual:runic-toolkit/client";
const stateEvent = "runic:state";
const diagnosticEvent = "runic:diagnostic";
const traceEvent = "runic:trace";
const stateKey = "runic:state";
const defaultTimelineLimit = 200;
const maximumTimelineLimit = 500;

export interface RunicVitePlugin extends Plugin {
  /** Server-side input for display-safe companion diagnostics. */
  readonly diagnostics: Readonly<{
    report(entry: RunicDiagnosticEntry): void;
  }>;
}

export interface RunicViteOptions {
  readonly contract?: Readonly<{
    identity?: string;
    version?: string;
    fingerprint?: string;
  }>;
  readonly devtools?: boolean | "auto";
  readonly devtoolsVisibility?: "normal" | "passive" | "hidden";
  readonly maxTimelineEntries?: number;
  /** Injects the Runic Desktop bootstrap without taking ownership of Vite or HMR. */
  readonly desktop?: boolean | Readonly<{
    /** Absolute development bootstrap URL; production uses the surface-relative default. */
    bootstrapUrl?: string;
  }>;
}

export interface RunicDevelopmentState {
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
  readonly timeline: readonly Readonly<Required<Pick<RunicTraceEntry, "id" | "timestamp" | "kind" | "label">> & {
    source?: RunicDiagnosticSource;
    detail: RunicDiagnosticDetail;
  }>[];
}

export function runic(options: RunicViteOptions = {}): RunicVitePlugin {
  const maxTimelineEntries = timelineLimit(options.maxTimelineEntries);
  let server: ViteDevServer | undefined;
  let injectDevtoolsClient = false;
  let sharedState: { mutate: (update: (draft: RunicDevelopmentState) => void) => void } | undefined;
  let jsonRenderer: JsonRenderer | undefined;
  let state = initialState(options);
  let nextDiagnosticSequence = 1;
  const desktopBootstrapUrl = resolveDesktopBootstrapUrl(options.desktop);

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

  const appendDiagnostic = (
    candidate: unknown,
    defaultSource?: RunicDiagnosticSource,
  ): void => {
    const summary = sanitizeDiagnosticSummary(candidate, defaultSource);
    if (!summary) return;
    const entry: RunicDevelopmentState["timeline"][number] = {
      ...summary,
      id: `diagnostic-${nextDiagnosticSequence}`,
      timestamp: new Date().toISOString(),
    };
    nextDiagnosticSequence += 1;
    state = {
      ...state,
      timeline: [...state.timeline, entry].slice(-maxTimelineEntries),
    };
    publish();
  };

  return {
    name: "runic",
    enforce: "pre",
    config(config, environment) {
      if (environment.command !== "build" || desktopBootstrapUrl === undefined) {
        return undefined;
      }
      if (config.base !== undefined && config.base !== "./") {
        throw new TypeError("desktop: true requires Vite base to be './' for relocatable surface output.");
      }
      return { base: "./" };
    },
    ...(desktopBootstrapUrl === undefined ? {} : {
      transformIndexHtml: {
        order: "post" as const,
        handler: () => [{
          tag: "script",
          attrs: {
            src: state.vite.command === "build" ? "./runic-desktop.js" : desktopBootstrapUrl,
          },
          injectTo: "head-prepend" as const,
        }],
      },
    }),
    diagnostics: { report: (entry) => appendDiagnostic(entry) },
    configResolved(resolved) {
      if (resolved.command === "build" && desktopBootstrapUrl !== undefined && resolved.base !== "./") {
        throw new TypeError("desktop: true requires the resolved Vite base to be './' for relocatable surface output.");
      }
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
      viteServer.ws.on(diagnosticEvent, (payload) => appendDiagnostic(payload));
      // Compatibility for the bridge-only event emitted by preview clients.
      viteServer.ws.on(traceEvent, (payload) => appendDiagnostic(payload, "application-bridge"));
      viteServer.middlewares.use("/__runic/state", (_request, response) => {
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
      if (id === legacyVirtualClientId) {
        throw new Error(
          'RUNICP001: "virtual:runic-toolkit/client" was removed in v0.2. Import "virtual:runic/client" instead.',
        );
      }
      return id === virtualClientId ? resolvedVirtualClientId : undefined;
    },
    load(id) {
      if (id !== resolvedVirtualClientId) return undefined;
      const injector = injectDevtoolsClient
        ? `import ${JSON.stringify(devtoolsInjector(options.devtoolsVisibility ?? "passive"))};\n`
        : "";
      return `${injector}export * from ${JSON.stringify("@runic-artifex/vite-plugin-runic/client")};`;
    },
    devtools: {
      async setup(context) {
        sharedState = await context.rpc.sharedState.get(stateKey, { initialValue: state });
        const renderer = context.createJsonRenderer(createDevtoolsSpec(state));
        jsonRenderer = renderer;
        context.docks.register({
          id: "runic:overview",
          title: "Runic",
          icon: "ph:diamond-duotone",
          type: "json-render",
          ui: renderer,
        });
        context.commands.register({
          id: "runic:copy-diagnostic-state",
          title: "Runic: Copy sanitized diagnostic state",
          handler: () => JSON.stringify(state, null, 2),
        });
      },
    },
  };
}

function resolveDesktopBootstrapUrl(
  options: RunicViteOptions["desktop"],
): string | undefined {
  if (options === undefined || options === false) return undefined;
  const candidate = options === true ? "./runic-desktop.js" : options.bootstrapUrl ?? "./runic-desktop.js";
  if (candidate === "./runic-desktop.js") return candidate;
  let url: URL;
  try { url = new URL(candidate); }
  catch { throw new TypeError("desktop.bootstrapUrl must be ./runic-desktop.js or an absolute HTTP(S) URL."); }
  if ((url.protocol !== "http:" && url.protocol !== "https:") ||
      url.username !== "" || url.password !== "" || url.pathname !== "/runic-desktop.js" ||
      url.search !== "" || url.hash !== "") {
    throw new TypeError("desktop.bootstrapUrl must be ./runic-desktop.js or an absolute HTTP(S) URL.");
  }
  return url.href;
}

function initialState(options: RunicViteOptions): RunicDevelopmentState {
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

function sanitizeRuntimeState(candidate: unknown): RunicRuntimeState {
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
  const operations: RunicDevelopmentState["operations"] | undefined = Array.isArray(candidate.operations)
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

function createDevtoolsSpec(current: RunicDevelopmentState): JsonRenderSpec {
  const recent = current.timeline.slice(-12).reverse();
  return {
    root: "root",
    elements: {
      root: {
        type: "Stack",
        props: { direction: "column", gap: 12 },
        children: ["heading", "connection", "contract", "operations", "timeline"],
      },
      heading: { type: "Text", props: { content: "Runic", variant: "heading" } },
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
                key: `${entry.source} · ${entry.kind} · ${entry.timestamp || entry.id}`,
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

function timelineLimit(value: number | undefined): number {
  if (typeof value !== "number" || !Number.isFinite(value)) return defaultTimelineLimit;
  return Math.min(maximumTimelineLimit, Math.max(1, Math.floor(value)));
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
