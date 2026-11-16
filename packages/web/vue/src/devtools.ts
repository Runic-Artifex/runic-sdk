// The Vue DevTools integration (#1041), in the development build only (src/development-build.d.ts): a "Runic Views" custom
// inspector that lists the page's Views with their state and recent calls, and a "Runic Bridge calls" timeline layer.
// It is a reader of two development channels that every copy of @runic-artifex/views shares on the page
// (specs/application/README.md, "Shared globals"): the state inspector (`runic.views.inspector`) and the Bridge-call
// channel (`runic.views.calls`, development build of views only).
//
// It attaches when the first composable of an app runs and Vue DevTools is on the page (its global hook), and loads
// `@vue/devtools-api`, an optional peer dependency, with a dynamic import: an app without Vue DevTools, or without the
// package, loads nothing more. The production build does not contain this module.
import type { App } from "vue";

const inspectorKey = Symbol.for("runic.views.inspector");
const callsKey = Symbol.for("runic.views.calls");
const inspectorId = "runic-views";
const layerId = "runic-bridge-calls";
/** Calls kept per View for the inspector. */
const recentCalls = 20;

interface InspectorEvent {
  readonly kind: string; readonly route: string; readonly contract?: string; readonly revision?: number;
  readonly epoch?: number; readonly presentations?: number; readonly state?: unknown; readonly member?: string;
}

interface CallEvent {
  readonly phase: string; readonly id: number; readonly kind: string; readonly request: string; readonly route: string;
  readonly contract: string; readonly member: string; readonly start: number; readonly end?: number;
  readonly duration?: number; readonly outcome?: string; readonly revision?: number; readonly reason?: string;
  readonly error?: unknown; readonly handler?: { readonly name: string; readonly duration: number };
}

interface Channel<T> { readonly listeners: Set<(event: T) => void>; requested?: boolean }

/** The part of the Vue DevTools plugin API (`@vue/devtools-api`, the v6-compatible plugin API) this module uses. */
interface DevtoolsApi {
  addInspector(options: { id: string; label: string; icon?: string; treeFilterPlaceholder?: string }): void;
  addTimelineLayer(options: { id: string; label: string; color: number }): void;
  addTimelineEvent(options: { layerId: string; event: TimelineEvent }): void;
  sendInspectorTree(id: string): void;
  sendInspectorState(id: string): void;
  now?(): number;
  on: {
    getInspectorTree(handler: (payload: { inspectorId: string; filter: string; rootNodes: unknown[] }) => void): void;
    getInspectorState(handler: (payload: { inspectorId: string; nodeId: string; state: unknown }) => void): void;
  };
}

interface TimelineEvent {
  time: number; title: string; subtitle?: string; data: Record<string, unknown>; groupId?: number | string;
  logType?: "default" | "warning" | "error";
}

interface PluginDescriptor { id: string; label: string; app: App; packageName?: string; homepage?: string }
type SetupDevtoolsPlugin = (descriptor: PluginDescriptor, setup: (api: DevtoolsApi) => void) => void;

/** A View the inspector lists. */
export interface InspectedView {
  readonly route: string;
  contract: string | undefined;
  revision: number | undefined;
  epoch: number | undefined;
  presentations: number;
  state: unknown;
  closed: boolean;
  readonly calls: CallEvent[];
}

const attached = new WeakSet<object>();

/** The C# view model's name of a contract `Demo.EditorViewModel:3F2A…`. */
function viewName(view: InspectedView): string {
  return view.contract?.split(":", 1)[0]!.split(".").pop() || view.route;
}

function errorText(error: unknown): string | undefined {
  if (error === undefined) return undefined;
  return error instanceof Error ? `${error.name}: ${error.message}` : String((error as { message?: unknown } | null)?.message ?? error);
}

function callTitle(call: CallEvent): string {
  return `${call.kind} ${call.member}`;
}

/**
 * The model of the inspector: the Views of the page and their recent calls, from the two channels. `changed` runs after
 * each change, and `call` after each call event.
 */
export function inspectViews(slots: Record<string | symbol, unknown>, changed: () => void, call?: (event: CallEvent) => void): {
  readonly views: Map<string, InspectedView>;
  stop(): void;
} {
  const views = new Map<string, InspectedView>();
  const view = (route: string): InspectedView => {
    let found = views.get(route);
    if (!found) {
      found = { route, contract: undefined, revision: undefined, epoch: undefined, presentations: 0, state: undefined, closed: false, calls: [] };
      views.set(route, found);
    }
    return found;
  };
  const onState = (event: InspectorEvent) => {
    if (typeof event?.route !== "string") return;
    if (event.kind === "state") {
      const found = view(event.route);
      found.closed = false;
      if (typeof event.contract === "string") found.contract = event.contract;
      found.revision = event.revision;
      found.epoch = event.epoch;
      found.presentations = event.presentations ?? found.presentations;
      found.state = event.state;
    } else if (event.kind === "closed") {
      const found = views.get(event.route);
      if (found) { found.closed = true; found.presentations = 0; }
    } else return;
    changed();
  };
  const onCall = (event: CallEvent) => {
    if (typeof event?.route !== "string") return;
    call?.(event);
    if (event.phase !== "end") return;
    const found = view(event.route);
    found.contract ??= event.contract;
    found.calls.unshift(event);
    if (found.calls.length > recentCalls) found.calls.length = recentCalls;
    changed();
  };
  const listen = <T>(key: symbol, listener: (event: T) => void) => {
    let channel = slots[key] as Channel<T> | undefined;
    if (!channel || !(channel.listeners instanceof Set)) slots[key] = channel = { listeners: new Set(), requested: false };
    channel.listeners.add(listener);
    channel.requested = true;
    return () => { channel.listeners.delete(listener); };
  };
  const stops = [listen(inspectorKey, onState), listen(callsKey, onCall)];
  return { views, stop: () => { for (const stop of stops) stop(); } };
}

/** The inspector's tree: one node per View, the closed ones last. */
export function inspectorTree(views: ReadonlyMap<string, InspectedView>, filter = ""): unknown[] {
  const needle = filter.trim().toLowerCase();
  return [...views.values()]
    .filter(view => !needle || view.route.toLowerCase().includes(needle) || viewName(view).toLowerCase().includes(needle))
    .sort((left, right) => Number(left.closed) - Number(right.closed) || left.route.localeCompare(right.route))
    .map(view => ({
      id: view.route,
      label: viewName(view),
      tags: [
        { label: view.route, textColor: 0xffffff, backgroundColor: 0x6d28d9 },
        ...view.revision === undefined ? [] : [{ label: `rev ${view.revision}`, textColor: 0x000000, backgroundColor: 0xe5e7eb }],
        ...view.closed ? [{ label: "closed", textColor: 0xffffff, backgroundColor: 0x6b7280 }] : [],
      ],
    }));
}

/** The inspector's state of one View: the View, its state and its recent calls. */
export function inspectorState(view: InspectedView | undefined): unknown {
  if (!view) return {};
  const item = (key: string, value: unknown) => ({ key, value, editable: false });
  const state = view.state !== null && typeof view.state === "object"
    ? Object.entries(view.state as Record<string, unknown>).map(([key, value]) => item(key, value))
    : [item("state", view.state)];
  return {
    View: [
      item("view model", viewName(view)), item("route", view.route), item("contract", view.contract),
      item("revision", view.revision), item("epoch", view.epoch), item("presentations", view.presentations),
      ...view.closed ? [item("closed", true)] : [],
    ],
    State: state,
    "Recent calls": view.calls.map(call => item(`#${call.id} ${callTitle(call)}`, {
      outcome: call.outcome, ...call.reason === undefined ? {} : { reason: call.reason },
      duration: `${Math.round((call.duration ?? 0) * 10) / 10} ms`,
      ...call.revision === undefined ? {} : { revision: call.revision },
      ...call.handler === undefined ? {} : { handler: `${call.handler.name} ${Math.round(call.handler.duration * 10) / 10} ms` },
      ...call.error === undefined ? {} : { error: errorText(call.error) },
    })),
  };
}

/** The timeline event of a call event: the start, and the end with its outcome, grouped by the call. */
export function timelineEvent(event: CallEvent, time: number): TimelineEvent {
  const data: Record<string, unknown> = { route: event.route, member: event.member, request: event.request };
  if (event.phase === "end") {
    data["outcome"] = event.outcome;
    data["duration"] = `${Math.round((event.duration ?? 0) * 10) / 10} ms`;
    if (event.revision !== undefined) data["revision"] = event.revision;
    if (event.reason !== undefined) data["reason"] = event.reason;
    if (event.handler !== undefined) data["handler"] = event.handler.name;
    if (event.error !== undefined) data["error"] = errorText(event.error);
  }
  const model = event.contract.split(":", 1)[0]!.split(".").pop() || event.route;
  return {
    time, title: `${event.kind} ${model}.${event.member}`, subtitle: event.phase === "end" ? event.outcome ?? "" : "start",
    data, groupId: event.id, logType: event.phase === "end" && event.outcome === "error" ? "error" : "default",
  };
}

function devtoolsPresent(): boolean {
  return (globalThis as { __VUE_DEVTOOLS_GLOBAL_HOOK__?: unknown }).__VUE_DEVTOOLS_GLOBAL_HOOK__ !== undefined;
}

/**
 * Registers the Runic plugin of Vue DevTools for `app` once, when Vue DevTools is on the page. `load` imports
 * `@vue/devtools-api` (a test passes its own).
 */
export function attachDevtools(app: App, load: () => Promise<{ setupDevtoolsPlugin: SetupDevtoolsPlugin }> = loadDevtoolsApi): void {
  if (attached.has(app) || !devtoolsPresent()) return;
  attached.add(app);
  // Read the channels from now on, before the View connects: DevTools may connect much later, when its panel opens,
  // and then shows every View the page has. The page's plugins (one per app) share one reader.
  const page = pageModel();
  load().then(({ setupDevtoolsPlugin }) => {
    setupDevtoolsPlugin({
      id: "dev.runic-artifex.views", label: "Runic Views", packageName: "@runic-artifex/vue",
      homepage: "https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/vue#vue-devtools", app,
    }, api => {
      api.addInspector({ id: inspectorId, label: "Runic Views", icon: "view_quilt", treeFilterPlaceholder: "Route or view model" });
      api.addTimelineLayer({ id: layerId, label: "Runic Bridge calls", color: 0x6d28d9 });
      let timer: ReturnType<typeof setTimeout> | undefined;
      const now = () => typeof api.now === "function" ? api.now() : Date.now();
      page.plugins.add({
        // The tree and the selected View's state follow the channels at most every 100 ms.
        changed: () => {
          timer ??= setTimeout(() => {
            timer = undefined;
            api.sendInspectorTree(inspectorId);
            api.sendInspectorState(inspectorId);
          }, 100);
        },
        call: event => { api.addTimelineEvent({ layerId, event: timelineEvent(event, now()) }); },
      });
      api.on.getInspectorTree(payload => {
        if (payload.inspectorId === inspectorId) payload.rootNodes = inspectorTree(page.views, payload.filter);
      });
      api.on.getInspectorState(payload => {
        if (payload.inspectorId === inspectorId) payload.state = inspectorState(page.views.get(payload.nodeId));
      });
    });
  }).catch(() => {
    // @vue/devtools-api is not installed: nothing to show, so stop reading the channels.
    attached.delete(app);
    if (page.plugins.size === 0) stopPageModel();
  });
}

interface Plugin { changed(): void; call(event: CallEvent): void }
let model: { readonly views: Map<string, InspectedView>; readonly plugins: Set<Plugin>; stop(): void } | undefined;

// The page's reader of the channels, which every app's plugin shows.
function pageModel(): NonNullable<typeof model> {
  if (model) return model;
  const plugins = new Set<Plugin>();
  const { views, stop } = inspectViews(globalThis as unknown as Record<string | symbol, unknown>,
    () => { for (const plugin of plugins) plugin.changed(); },
    event => { for (const plugin of plugins) plugin.call(event); });
  return model = { views, plugins, stop };
}

/** @internal Stops reading the channels (tests). */
export function stopPageModel(): void {
  model?.stop();
  model = undefined;
}

async function loadDevtoolsApi(): Promise<{ setupDevtoolsPlugin: SetupDevtoolsPlugin }> {
  return await import("@vue/devtools-api") as unknown as { setupDevtoolsPlugin: SetupDevtoolsPlugin };
}
