/// <reference types="@vitejs/devtools-kit" />

import type { Plugin, UserConfig, ViteDevServer } from "vite";
import type { JsonRenderer, JsonRenderSpec } from "@vitejs/devtools-kit";
import type { RunicDiagnosticEntry } from "./diagnostics.js";
import type { RunicDevelopmentState } from "./timeline.js";
import { isAbsolute, relative } from "node:path";
import { rewriteHostScripts, webRootPrefix } from "./host-scripts.js";
import { callEvent, clientOptionsKey, connectionEvent, diagnosticEvent, diagnosticsPath, viewEvent } from "./channel.js";
import { answerClientMessage, answerState, devSessionValue, devSessionVariable, statePath } from "./dev-session.js";
import { createDevelopmentTimeline, initialDevelopmentState, timelineLimit, withoutFailures } from "./timeline.js";
import type { RunicSourceMap } from "./sources.js";
import {
  answerOpen, answerSources, createSourceMapReader, editorRequestUrl, listsFile, openPath, openThroughEndpoint, sourcesPath,
} from "./sources.js";
import type { RunicBridgeCall } from "./calls.js";
import { formatMilliseconds, stackFrames } from "./calls.js";
import {
  buildComponent, componentEnvironment, componentEnvironmentOptions, componentHooks, resolveComponentOptions,
  type RunicComponentOptions,
} from "./component.js";

export type { RunicComponentOptions } from "./component.js";

export { runicDevSessionProxy } from "./dev-session.js";
export type { RunicDevSessionProxyOptions } from "./dev-session.js";
export type { RunicDevelopmentState, RunicViewSnapshot } from "./timeline.js";
export type { RunicBridgeCall, RunicBridgeCallHandler, RunicBridgeCallOutcome } from "./calls.js";
export type {
  RunicSourceLocation, RunicSourceMap, RunicSourceMember, RunicSourceMemberKind, RunicSourceViewModel,
} from "./sources.js";

export type {
  RunicDiagnosticDetail,
  RunicDiagnosticDetailValue,
  RunicDiagnosticFailure,
  RunicDiagnosticEntry,
  RunicDiagnosticSource,
  RunicTraceKind,
  RunicViewStateValue,
} from "./diagnostics.js";

const virtualClientId = "virtual:runic/client";
const resolvedVirtualClientId = `\0${virtualClientId}`;
const clientPackageId = "@runic-artifex/vite-plugin-runic/client";
// Sets the client's options (the HMR channel, the `stale-input` warning) on `globalThis`. A separate module, because
// the import of the client is hoisted above any statement in the virtual client module.
const clientOptionsId = "virtual:runic/client-options";
const resolvedClientOptionsId = `\0${clientOptionsId}`;
// What an import of the client resolves to where it must not run: the build, the server, `devtools: false`.
const resolvedEmptyClientId = "\0virtual:runic/client-disabled";
const stateEvent = "runic:state";
const stateKey = "runic:state";

/** The API another Vite plugin reaches through `plugin.api`, Vite's inter-plugin convention. */
export interface RunicVitePluginApi {
  /** Adds a display-safe entry to the DevTools timeline, for example from a companion plugin. */
  reportDiagnostic(entry: RunicDiagnosticEntry): void;
}

export interface RunicVitePlugin extends Plugin {
  readonly api: RunicVitePluginApi;
}

export interface RunicViteOptions {
  /** Display-only metadata supplied by the application contract owner. */
  readonly contract?: Readonly<{
    identity?: string;
    version?: string;
    fingerprint?: string;
  }>;
  /**
   * `false` disables the DevTools dock and the development client that
   * forwards `@runic-artifex/views` failures to the timeline.
   */
  readonly devtools?: boolean | "auto";
  readonly maxTimelineEntries?: number;
  /**
   * `false` turns off the development client's `stale-input` warning: script that sets a
   * focused text field to a value from before the user's last edit while a Bridge request
   * is in flight. Defaults to `true`.
   */
  readonly staleInputWarning?: boolean;
  /**
   * `false` leaves HTML alone: the plugin has no `transformIndexHtml` hook, which SvelteKit warns about, injects
   * neither the development client nor the Desktop bootstrap, and does not rewrite host scripts. The page loads the
   * client itself with `import "@runic-artifex/vite-plugin-runic/client"`, which the plugin serves while serving and
   * replaces with an empty module in the build. Cannot be combined with `desktop`. Defaults to `true`.
   */
  readonly html?: boolean;
  /** Injects the Runic Desktop bootstrap without taking ownership of Vite or HMR. */
  readonly desktop?: boolean | Readonly<{
    /** Absolute development bootstrap URL; production uses the surface-relative default. */
    bootstrapUrl?: string;
  }>;
  /**
   * Adds the `component` build target (trusted component mode): `vite build` also builds the guest's Views as an ES
   * module that defines a custom element, into `_runic_component/` of the build output. Requires base `./`. The
   * development server serves the page only.
   */
  readonly component?: RunicComponentOptions;
  /**
   * The development source map that the Bridge generator writes to `obj` (`runic-bridge/sources.json`), which
   * `vite dev` serves at `/__runic/sources` and opens in the editor through `/__runic/open`. A path names the file or
   * a directory to search, relative to the Vite root. By default the plugin takes the path `dotnet runic dev` passes,
   * else searches `obj` of the project, the Vite root and its parent. `false` turns both endpoints off. The build
   * never reads it.
   */
  readonly sourceMap?: string | false;
}

export function runic(options: RunicViteOptions = {}): RunicVitePlugin {
  const maxTimelineEntries = timelineLimit(options.maxTimelineEntries);
  if (options.staleInputWarning !== undefined && typeof options.staleInputWarning !== "boolean") {
    throw new TypeError("staleInputWarning must be true or false.");
  }
  if (options.html !== undefined && typeof options.html !== "boolean") {
    throw new TypeError("html must be true or false.");
  }
  const html = options.html !== false;
  if (options.sourceMap !== undefined && options.sourceMap !== false &&
      (typeof options.sourceMap !== "string" || options.sourceMap.length === 0)) {
    throw new TypeError("sourceMap must be a path or false.");
  }
  const client = options.devtools !== false;
  let server: ViteDevServer | undefined;
  let sharedState: { mutate: (update: (draft: RunicDevelopmentState) => void) => void } | undefined;
  let jsonRenderer: JsonRenderer | undefined;
  let resolvedRoot = process.cwd();
  let base = "/";
  // Whether the Vite DevTools dock is registered (DevTools() from @vitejs/devtools), which the client then loads.
  let dock = false;
  const desktopBootstrapUrl = resolveDesktopBootstrapUrl(options.desktop);
  if (!html && desktopBootstrapUrl !== undefined) {
    throw new TypeError("desktop requires html: true: the Desktop bootstrap is a script tag the plugin adds to the HTML.");
  }
  const component = resolveComponentOptions(options.component);
  const relocatable = desktopBootstrapUrl !== undefined || component !== undefined;
  const relocatableOption = desktopBootstrapUrl !== undefined ? "desktop: true" : "component";
  const components = component === undefined ? undefined : componentHooks(component, () => resolvedRoot);
  const inComponent = (environment: { name: string } | undefined): boolean =>
    components !== undefined && environment?.name === componentEnvironment;

  // The development source map, read while serving only (sources.ts).
  const readSourceMap = options.sourceMap === false
    ? () => undefined
    : createSourceMapReader(options.sourceMap, () => resolvedRoot);
  const timeline = createDevelopmentTimeline(initialDevelopmentState(options.contract), maxTimelineEntries, (state) => {
    sharedState?.mutate((draft) => Object.assign(draft, structuredClone(state)));
    void jsonRenderer?.updateSpec(createDevtoolsSpec(state, readSourceMap()));
    server?.ws.send({ type: "custom", event: stateEvent, data: state });
  });
  const deliver = (event: string, data: unknown): void => {
    if (event === connectionEvent) timeline.applyConnection(data);
    else if (event === diagnosticEvent) timeline.appendDiagnostic(data);
    else if (event === viewEvent) timeline.applyView(data);
    else if (event === callEvent) timeline.appendCall(data, readSourceMap());
  };
  const command = (): string => timeline.state.vite.command;

  const transformIndexHtml: Plugin["transformIndexHtml"] = {
    order: "post" as const,
    handler: (document, context) => {
      const prefix = webRootPrefix(context.path);
      const tags = [
        ...(desktopBootstrapUrl === undefined ? [] : [{
          tag: "script",
          attrs: {
            src: command() === "build" || desktopBootstrapUrl === "./runic-desktop.js"
              ? `${prefix}runic-desktop.js`
              : desktopBootstrapUrl,
          },
          injectTo: "head-prepend" as const,
        }]),
        // The development client forwards Views runtime failures to the
        // timeline. It loads before application modules, so a missing Bridge
        // at startup is reported too.
        ...(command() !== "serve" || !client ? [] : [{
          tag: "script",
          attrs: { type: "module", src: `${base}@id/${virtualClientId}` },
          injectTo: "head" as const,
        }]),
      ];
      return command() === "serve"
        ? { html: rewriteHostScripts(document, context.path, false), tags }
        : tags;
    },
  };

  return {
    name: "runic",
    enforce: "pre",
    config(config, environment) {
      if (environment.command === "serve") {
        // The development client is injected into HTML as a virtual module,
        // which the dependency scanner cannot see. Pre-bundling it up front
        // keeps a cold optimizer cache from discovering it on the first page
        // load and reloading the page, which drops a host's Bridge connection.
        return client ? { optimizeDeps: { include: [clientPackageId] } } : undefined;
      }
      if (!relocatable) return undefined;
      if (config.base !== undefined && config.base !== "./") {
        throw new TypeError(`${relocatableOption} requires Vite base to be './' for relocatable surface output.`);
      }
      const result: UserConfig = { base: "./" };
      if (component !== undefined && environment.isSsrBuild !== true) {
        // The component builds as an environment of its own next to the page; `builder` makes `vite build` build
        // every environment, as `vite build --app` does.
        result.environments = { [componentEnvironment]: componentEnvironmentOptions(config.build?.outDir ?? "dist") };
        if (config.builder === undefined) result.builder = {};
      }
      return result;
    },
    buildApp: { order: "post", handler: buildComponent },
    // With html: false there is no transformIndexHtml property at all: SvelteKit warns about any plugin that has one.
    ...(html ? {
      // The build parses HTML in a later transform, so host scripts are marked
      // vite-ignore here; the dev server has no such pass and uses the hook below.
      transform: {
        filter: { id: /\.html$/ },
        handler(code: string, id: string) {
          if (command() !== "build" || !id.endsWith(".html")) return undefined;
          const documentPath = relative(resolvedRoot, id);
          if (documentPath.startsWith("..") || isAbsolute(documentPath)) return undefined;
          const rewritten = rewriteHostScripts(code, documentPath, true);
          return rewritten === code ? undefined : { code: rewritten, map: null };
        },
      },
      transformIndexHtml,
    } : {}),
    api: { reportDiagnostic: (entry) => { timeline.appendDiagnostic(entry); } },
    configResolved(resolved) {
      if (resolved.command === "build" && relocatable && resolved.base !== "./") {
        throw new TypeError(`${relocatableOption} requires the resolved Vite base to be './' for relocatable surface output.`);
      }
      if (resolved.command === "serve" && options.devtools === true &&
          !resolved.plugins.some((plugin) => plugin.name === "vite:devtools:server")) {
        throw new Error('RUNICP007: devtools: true requires DevTools() from @vitejs/devtools in the Vite plugins array.');
      }
      dock = resolved.command === "serve" && resolved.plugins.some((plugin) => plugin.name === "vite:devtools:server");
      if (dock && !html) {
        // With html: false the page owns its HTML (SvelteKit renders it and runs no transformIndexHtml, and warns about
        // a plugin that has one), so the dock's HTML loader never runs: the client loads the dock instead.
        const injection = resolved.plugins.find((plugin) => plugin.name === "vite:devtools:injection");
        if (injection !== undefined) delete (injection as { transformIndexHtml?: unknown }).transformIndexHtml;
      }
      resolvedRoot = resolved.root;
      const configuredBase = resolved.base || "/";
      base = configuredBase.endsWith("/") ? configuredBase : `${configuredBase}/`;
      timeline.configure((state) => ({
        ...state,
        vite: { command: resolved.command, mode: resolved.mode, root: resolved.root },
      }));
    },
    async configureServer(viteServer) {
      server = viteServer;
      viteServer.ws.on(connectionEvent, (payload) => deliver(connectionEvent, payload));
      viteServer.ws.on(diagnosticEvent, (payload) => deliver(diagnosticEvent, payload));
      viteServer.ws.on(viewEvent, (payload) => deliver(viewEvent, payload));
      viteServer.ws.on(callEvent, (payload) => deliver(callEvent, payload));
      const devSession = devSessionValue(process.env[devSessionVariable]);
      viteServer.middlewares.use(statePath, (_request, response) => {
        answerState(response, timeline.state, devSession);
      });
      // A client that the page loads without the HMR channel posts here (channel.ts).
      viteServer.middlewares.use(diagnosticsPath, (request, response, next) => {
        answerClientMessage(request, response, deliver).catch(next);
      });
      if (options.sourceMap !== false) {
        viteServer.middlewares.use(sourcesPath, (request, response) => {
          answerSources(request, response, readSourceMap);
        });
        // Mounted at the root, not at openPath: an accepted request continues to Vite's /__open-in-editor with its
        // URL rewritten, which a mounted handler's next() would prefix with the mount path again.
        viteServer.middlewares.use(function runicOpenInEditor(request, response, next) {
          if (request.url?.split("?", 1)[0] !== openPath) return next();
          answerOpen(request, response, readSourceMap, (location) => {
            request.url = editorRequestUrl(base, location);
            next();
          }).catch(next);
        });
      }
      viteServer.httpServer?.once("close", () => {
        server = undefined;
      });
    },
    resolveId(id, importer, resolveOptions) {
      if (inComponent(this.environment)) {
        const resolved = components!.resolveId(id);
        if (resolved !== undefined) return resolved;
      }
      if (id === virtualClientId || (id === clientPackageId && importer !== resolvedVirtualClientId)) {
        // The client runs only in a served page. An application import of the package goes through the virtual
        // module too, so that it takes this plugin's options and channel, and is empty in the build.
        const served = client && command() === "serve" && resolveOptions?.ssr !== true &&
          this.environment?.config.consumer !== "server";
        return served ? resolvedVirtualClientId : resolvedEmptyClientId;
      }
      return id === clientOptionsId ? resolvedClientOptionsId : undefined;
    },
    load(id) {
      if (inComponent(this.environment)) {
        const loaded = components!.load(id);
        if (loaded !== undefined) return loaded;
      }
      if (id === resolvedEmptyClientId) return "export {};";
      if (id === resolvedClientOptionsId) {
        const clientOptions = dock ? `{ channel: "hot", dock: true }` : `{ channel: "hot" }`;
        const statements = [`globalThis[Symbol.for(${JSON.stringify(clientOptionsKey.description)})] = ${clientOptions};`];
        if (options.staleInputWarning === false) {
          statements.push(`globalThis[Symbol.for("runic.vite.staleInputWarning")] = false;`);
        }
        return statements.join("\n");
      }
      if (id !== resolvedVirtualClientId) return undefined;
      // Imports evaluate in order, so the options are set before the client reads them.
      return `import ${JSON.stringify(clientOptionsId)};\nimport ${JSON.stringify(clientPackageId)};`;
    },
    generateBundle: {
      // After Vite's CSS plugin wrote the component's stylesheet.
      order: "post",
      handler(_output, bundle) {
        if (inComponent(this.environment)) components!.generateBundle(this, bundle);
      },
    },
    devtools: {
      async setup(context) {
        if (!client) return;
        sharedState = await context.rpc.sharedState.get(stateKey, { initialValue: timeline.state });
        const renderer = context.createJsonRenderer(createDevtoolsSpec(timeline.state, readSourceMap()));
        jsonRenderer = renderer;
        context.docks.register({
          id: "runic:overview",
          title: "Runic",
          icon: "ph:diamond-duotone",
          type: "json-render",
          view: renderer.view,
        });
        // The dock's open buttons: `{ id }` of a map entry or `{ file, line }` of a stack frame, which go through the
        // server's own /__runic/open, so the dock opens only what the source map lists.
        context.rpc.register({
          name: openInEditorAction,
          type: "action",
          setup: () => ({
            handler: async (target: unknown) => {
              const status = options.sourceMap === false ? 404
                : await openThroughEndpoint(server?.resolvedUrls?.local[0], target);
              if (status === 0) throw new Error("The development server has no local URL for /__runic/open.");
              if (status !== 200) throw new Error(`The source map lists no such location: /__runic/open answered ${status}.`);
            },
          }),
        });
        context.commands.register({
          id: "runic:copy-diagnostic-state",
          title: "Runic: Copy sanitized diagnostic state",
          handler: () => JSON.stringify(withoutFailures(timeline.state), null, 2),
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

const openInEditorAction = "runic:open-in-editor";
// The dock shows the most recent calls; the state keeps as many as the timeline.
const dockCalls = 20;
const dockFrames = 8;

type Elements = JsonRenderSpec["elements"];

function openButton(label: string, target: Record<string, string | number>): Elements[string] {
  return {
    type: "Button",
    props: { label, icon: "ph:arrow-square-out", variant: "ghost" },
    on: { press: { action: openInEditorAction, params: target } },
  } as Elements[string];
}

// A .NET stack as text, with a button for each frame in a file of the source map.
function stackElements(id: string, stack: string | undefined, map: RunicSourceMap | undefined): [string[], Elements] {
  const elements: Elements = {};
  const frames = stackFrames(stack, dockFrames).filter((frame) => listsFile(map, frame.file));
  const children = frames.map((frame, index) => {
    const name = `${id}-frame-${index}`;
    const fileName = frame.file.split(/[\\/]/u).pop();
    elements[name] = openButton(`${frame.method.replace(/^.*\.(?=[^.]+\.[^.]+$)/u, "")} · ${fileName}:${frame.line}`,
      { file: frame.file, line: frame.line });
    return name;
  });
  return [children, elements];
}

// The call timeline: the newest call first, each with its handler's open button and a failed call's frames.
function callsSection(current: RunicDevelopmentState, map: RunicSourceMap | undefined): Elements {
  const calls = current.calls.slice(-dockCalls).reverse();
  const elements: Elements = {};
  const rows = calls.map((call: RunicBridgeCall) => {
    const id = `calls-${call.id}`;
    const children = [`${id}-label`];
    elements[`${id}-label`] = { type: "Text", props: { text: call.label, variant: call.outcome === "error" ? "body" : "code" } };
    const details = [
      ...(call.handlerDuration === undefined ? [] : [`C# ${formatMilliseconds(call.handlerDuration)}`]),
      ...(call.revision === undefined ? [] : [`revision ${call.revision}`]),
      ...(call.handler?.fileName === undefined ? [] : [`${call.handler.fileName}${call.handler.line === undefined ? "" : `:${call.handler.line}`}`]),
      call.timestamp,
    ];
    elements[`${id}-detail`] = { type: "Text", props: { text: details.join(" · "), variant: "caption" } };
    children.push(`${id}-detail`);
    if (call.handler?.line !== undefined) {
      elements[`${id}-open`] = openButton(`Open ${call.handler.method}`, { id: call.handler.source });
      children.push(`${id}-open`);
    }
    if (call.failure) {
      elements[`${id}-failure`] = { type: "Text", props: { text: `${call.failure.type}: ${call.failure.message}`, variant: "caption" } };
      const [frames, frameElements] = stackElements(id, call.failure.stack, map);
      Object.assign(elements, frameElements);
      children.push(`${id}-failure`, ...frames);
    }
    elements[id] = { type: "Stack", props: { direction: "column", gap: 2 }, children };
    return id;
  });
  if (rows.length === 0) elements["calls-none"] = { type: "Text", props: { text: "No Bridge call yet" } };
  return {
    calls: { type: "Card", props: { title: "Bridge calls" }, children: rows.length === 0 ? ["calls-none"] : rows },
    ...elements,
  };
}

function createDevtoolsSpec(current: RunicDevelopmentState, map?: RunicSourceMap): JsonRenderSpec {
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
        children: ["heading", "connection", "contract", "calls", "views", "superseded", "failure", "timeline"],
      },
      heading: { type: "Text", props: { text: "Runic", variant: "heading" } },
      ...section("connection", "Connection", {
        State: current.connection.state === "unknown" ? "no View connected" : current.connection.state,
      }),
      ...section("contract", "Contract", {
        Identity: current.contract.identity || "not reported",
        Version: current.contract.version || "not reported",
        Fingerprint: current.contract.fingerprint || "not reported",
      }),
      ...callsSection(current, map),
      ...viewsSection(current),
      ...section("superseded", "Superseded commands", supersededCommands(current)),
      ...failureSection(current, map),
      ...section("timeline", "Recent timeline", current.timeline.length === 0
        ? { Events: "none" }
        : Object.fromEntries(current.timeline.slice(-12).reverse().map((entry) => [
            `${entry.id} · ${entry.source} · ${entry.kind} · ${entry.timestamp}`, entry.label,
          ]))),
    },
  };
}

// The state inspector: a card per open View with its state as a tree, the most recently changed first.
function viewsSection(current: RunicDevelopmentState): JsonRenderSpec["elements"] {
  const pages = new Set(current.views.map((view) => view.page));
  const cards = current.views.map((view, index) => {
    const revision = view.revision === undefined ? "" : ` · revision ${view.revision}`;
    const epoch = view.epoch === undefined ? "" : ` · epoch ${view.epoch}`;
    // Several windows can show the same route; the page tells them apart.
    const page = pages.size > 1 ? ` · page ${view.page.slice(0, 6)}` : "";
    return [`views-${index}`, {
      card: { type: "Card", props: { title: `${view.route}${revision}${epoch}${page}` }, children: [`views-${index}-state`] },
      state: { type: "Tree", props: { data: view.state, defaultExpanded: index === 0 } },
    }] as const;
  });
  return {
    views: {
      type: "Card",
      props: { title: "Views" },
      children: cards.length === 0 ? ["views-none"] : cards.map(([id]) => id),
    },
    ...(cards.length === 0 ? { "views-none": { type: "Text", props: { text: "No View connected" } } } : {}),
    ...Object.fromEntries(cards.flatMap(([id, { card, state }]) => [[id, card], [`${id}-state`, state]])),
  };
}

// Command replies the producer answered `superseded` (presentation-epoch/1): the call resolved without running.
function supersededCommands(current: RunicDevelopmentState): Record<string, string> {
  const entries = current.timeline.filter((entry) => entry.kind === "command" && entry.detail.code === "superseded");
  if (entries.length === 0) return { Commands: "none" };
  return Object.fromEntries(entries.slice(-12).reverse().map((entry) => [
    `${entry.id} · ${entry.timestamp}`,
    `${entry.detail.route ?? "?"}.${entry.detail.member ?? "?"} from epoch ${entry.detail.epoch ?? "?"}`,
  ]));
}

// The last failure, with an open button for each of its frames in a file of the source map.
function failureSection(current: RunicDevelopmentState, map: RunicSourceMap | undefined): Elements {
  const entry = [...current.timeline].reverse().find((candidate) => candidate.failure !== undefined);
  const data: Record<string, string> = !entry?.failure ? { Failure: "none" } : {
    Event: `${entry.id} · ${entry.source} · ${entry.timestamp}`,
    Label: entry.label,
    Type: entry.failure.type,
    Message: entry.failure.message,
    ...(entry.failure.stack ? { Stack: entry.failure.stack } : {}),
  };
  const [frames, elements] = stackElements("failure", entry?.failure?.stack, map);
  return {
    failure: { type: "Card", props: { title: "Last failure" }, children: ["failure-data", ...frames] },
    "failure-data": { type: "KeyValueTable", props: { data } },
    ...elements,
  };
}
