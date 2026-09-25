/// <reference types="@vitejs/devtools-kit" />

import type { Plugin, ResolvedConfig, ViteDevServer } from "vite";
import type { JsonRenderer, JsonRenderSpec } from "@vitejs/devtools-kit";
import type {
  RunicDiagnosticEntry,
  RunicDiagnosticDetail,
  RunicDiagnosticSource,
  RunicRuntimeState,
} from "./client.js";
import { sanitizeDiagnosticSummary } from "./diagnostics.js";

export type {
  RunicDiagnosticDetail,
  RunicDiagnosticDetailValue,
  RunicDiagnosticEntry,
  RunicDiagnosticReporter,
  RunicDiagnosticSource,
  RunicRuntimeState,
  RunicTraceKind,
} from "./client.js";

const virtualClientId = "virtual:runic/client";
const resolvedVirtualClientId = `\0${virtualClientId}`;
const stateEvent = "runic:state";
const diagnosticEvent = "runic:diagnostic";
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
  /** Display-only metadata supplied by the application contract owner. */
  readonly contract?: Readonly<{
    identity?: string;
    version?: string;
    fingerprint?: string;
  }>;
  readonly devtools?: boolean | "auto";
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
  readonly timeline: readonly Readonly<Required<Pick<RunicDiagnosticEntry, "id" | "timestamp" | "kind" | "label">> & {
    source?: RunicDiagnosticSource;
    detail: RunicDiagnosticDetail;
  }>[];
}

export function runic(options: RunicViteOptions = {}): RunicVitePlugin {
  const maxTimelineEntries = timelineLimit(options.maxTimelineEntries);
  let server: ViteDevServer | undefined;
  let sharedState: { mutate: (update: (draft: RunicDevelopmentState) => void) => void } | undefined;
  let jsonRenderer: JsonRenderer | undefined;
  let state = initialState(options);
  let nextDiagnosticSequence = 1;
  let resolvedRoot = process.cwd();
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
      if (resolved.command === "serve" && options.devtools === true &&
          !resolved.plugins.some((plugin) => plugin.name === "vite:devtools:server")) {
        throw new Error('RUNICP007: devtools: true requires DevTools() from @vitejs/devtools in the Vite plugins array.');
      }
      resolvedRoot = resolved.root;
      state = {
        ...state,
        vite: { command: resolved.command, mode: resolved.mode, root: resolved.root },
      };
    },
    async configureServer(viteServer) {
      server = viteServer;
      viteServer.ws.on(stateEvent, (payload) => applyRuntimeState(payload));
      viteServer.ws.on(diagnosticEvent, (payload) => appendDiagnostic(payload));
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
      return id === virtualClientId ? resolvedVirtualClientId : undefined;
    },
    load(id) {
      if (id !== resolvedVirtualClientId) return undefined;
      return `export * from ${JSON.stringify("@runic-artifex/vite-plugin-runic/client")};`;
    },
    devtools: {
      async setup(context) {
        if (options.devtools === false) return;
        sharedState = await context.rpc.sharedState.get(stateKey, { initialValue: state });
        const renderer = context.createJsonRenderer(createDevtoolsSpec(state));
        jsonRenderer = renderer;
        context.docks.register({
          id: "runic:overview",
          title: "Runic",
          icon: "ph:diamond-duotone",
          type: "json-render",
          view: renderer.view,
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
  const section = (id: string, title: string, data: Record<string, string>): JsonRenderSpec["elements"] => ({
    [id]: { type: "Card", props: { title }, children: [`${id}-data`] },
    [`${id}-data`]: { type: "KeyValueTable", props: { data } },
  });
  return {
    root: "root",
    elements: {
      root: {
        type: "Stack",
        props: { direction: "column", gap: 12 },
        children: ["heading", "connection", "contract", "operations", "timeline"],
      },
      heading: { type: "Text", props: { text: "Runic", variant: "heading" } },
      ...section("connection", "Connection", {
        State: current.connection.state,
        Transport: current.connection.transport,
        Session: current.connection.sessionId || "not initialized",
        Revision: String(current.connection.revision),
        Sequence: String(current.connection.sequence),
      }),
      ...section("contract", "Contract", {
        Identity: current.contract.identity || "not reported",
        Version: current.contract.version || "not reported",
        Fingerprint: current.contract.fingerprint || "not reported",
      }),
      ...section("operations", `Operations (${current.operations.length})`, current.operations.length === 0
        ? { Active: "none" }
        : Object.fromEntries(current.operations.map((operation) => [
            operation.id, `${operation.state} ${operation.completed}/${operation.total}`,
          ]))),
      ...section("timeline", "Recent timeline", current.timeline.length === 0
        ? { Events: "none" }
        : Object.fromEntries(current.timeline.slice(-12).reverse().map((entry) => [
            `${entry.id} · ${entry.source} · ${entry.kind} · ${entry.timestamp}`, entry.label,
          ]))),
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
