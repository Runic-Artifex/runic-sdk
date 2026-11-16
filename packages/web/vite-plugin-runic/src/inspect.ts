// Runic Inspect: the development client's select mode. Alt+Shift+X (or `toggle()`) turns it on; hovering outlines an
// element, and clicking it shows an overview of how it is connected: its framework component, the ViewModel members
// it binds with their values and C# members, the commands it calls with their C# handlers, its recent Bridge calls,
// and the texts its ViewModel sent, each location with an "open in editor" link. While it is on, the page's own
// handlers see no pointer event: a transparent layer takes them, and the window's capture phase stops them.
//
// What an element calls is found before any click in its component's source (handlers.ts and inspect-handlers.ts),
// and learned from the Bridge-call channel (`runic.views.calls`): its `start` report arrives in the caller's stack, so
// the DOM event being dispatched (`globalThis.event`) names the element. States come from the state inspector channel
// (`runic.views.inspector`), C# locations from the development source map, and every editor link opens through
// `/__runic/open`'s decision: a C# location by its source map id, a Translations message by its reference, a frontend
// file by path (only files Vite serves). The source map, the handler analysis, message sources and opening go over
// Vite's HMR connection when the client has it, so they reach the Vite server also from the .NET host's page under
// `dotnet runic dev`; else to `/__runic/sources`, `/__runic/handlers`, `/__runic-translations/message` and
// `/__runic/open` relative to the page.
//
// Text from generated `m.*()` calls carries Runic Translations' invisible message markers while select mode is on: turning
// it on calls `markers.set(true)` on the Translations text-change channel (`Symbol.for("runic.translations.text")`,
// vite-plugin-runic-translations 0.12 under `vite dev`), which renders the page again with a marker in front of every
// message's text; turning it off renders the plain text again. The overview decodes the markers of the selected
// element's text and attributes, and reads each message's source location from the Translations dev server's message API
// (`/__runic-translations/message`). Outside select mode the page's text has no markers.
//
// "Edit" on a message opens the translation editor (inspect-editor.ts): every locale's MF2 source, written back through
// the same API with the file hash it read. The page shows the saved text in place through the text-change channel, and
// the overview, which listens to that channel, describes the selected element again without losing the selection.
//
// From another machine (a page not on localhost), the data endpoints answer when the development server allows that
// machine (`runic({ allow })`, `RUNIC_DEV_ALLOW`); `/__runic/open` never does, so the overview says so.
//
// `globalThis[Symbol.for("runic.vite.inspect")]` is the API for agents and tests: `describe(element)` returns the
// overview as JSON-safe data. Everything here is part of the development client, which a build replaces with an empty
// module; it uses only the DOM, so every engine under `dotnet runic dev` runs it.

import type { RunicSourceMap } from "./sources.js";
import { handlersPath, openPath, sourcesPath } from "./channel.js";
import type { RunicHandlerAnalysis } from "./handlers.js";
import { findProbes, matchBindings, probeFiles, type RequestedAnalysis, type SourceBindings } from "./inspect-handlers.js";
import {
  assembleDescription, groupMessages, messageSource, normalizeText, stripMarkers, type LearnedBinding, type RunicInspectCall,
  type RunicInspectDescription, type RunicInspectLocation, type RunicInspectMessage, type TextReferenceLike,
} from "./inspect-data.js";
import { findComponent, findComponentSync, type AngularDebug, type NodeLike } from "./inspect-frameworks.js";
import { createMessageEditor, editorStyles, type MessageEditor } from "./inspect-editor.js";
import { hasEdits } from "./inspect-edit.js";

/** The value at `globalThis[Symbol.for("runic.vite.inspect")]`. */
export interface RunicInspectApi {
  readonly version: 1;
  /** Whether select mode is on. */
  readonly active: boolean;
  /**
   * Turns select mode on or off (the opposite of now without an argument); returns whether it is on. While it is on, the
   * text of generated `m.*()` calls carries Runic Translations' message markers, so `describe` can name the messages.
   */
  toggle(on?: boolean): boolean;
  /** The overview of an element, as JSON-safe data. */
  describe(element: Element): Promise<RunicInspectDescription>;
  /** Shows the overview of an element, as clicking it in select mode does, and returns it. */
  select(element: Element): Promise<RunicInspectDescription>;
  /** Opens a location of an overview in the editor; resolves to whether the server accepted it. */
  open(location: RunicInspectLocation): Promise<boolean>;
}

/**
 * A request to the development server over Vite's HMR connection (channel.ts, `requestEvent`): the source map, or
 * opening a location. Resolves to the server's status (0 when it did not answer) and, for the map, its body.
 */
export type InspectRequest = (kind: "sources" | "handlers" | "message" | "open", target?: unknown) =>
  Promise<{ readonly status: number; readonly body?: unknown }>;

export interface InspectOptions {
  /** The server's base path, below which the Translations message API is (without `request`). */
  readonly base?: string | undefined;
  readonly root?: string | undefined;
  /**
   * The development client's request over Vite's connection. With it, the source map and opening go to the Vite server
   * whichever origin the page has (under `dotnet runic dev` the .NET host's); without it, to `/__runic/sources` and
   * `/__runic/open` relative to the page.
   */
  readonly request?: InspectRequest | undefined;
}

export const inspectKey = Symbol.for("runic.vite.inspect");
const callsKey = Symbol.for("runic.views.calls");
const inspectorKey = Symbol.for("runic.views.inspector");
const translatedTextKey = Symbol.for("runic.views.translated-text");
const translationsTextKey = Symbol.for("runic.translations.text");
const messagePath = "__runic-translations/message";
/** The attributes whose text the overview reads message markers from, besides the element's text. */
const messageAttributes = ["title", "aria-label", "aria-description", "aria-placeholder", "placeholder", "alt", "label"] as const;
const tagName = "runic-inspect";
const interactiveSelector = "button, a[href], input, select, textarea, summary, [role=button], [role=link], " +
  "[role=checkbox], [role=switch], [role=menuitem], [role=tab], [role=option], [contenteditable=true]";
const maximumCalls = 10;
const swallowed = ["pointerdown", "pointerup", "mousedown", "mouseup", "click", "dblclick", "auxclick", "contextmenu",
  "touchstart", "touchend", "pointermove", "mousemove", "mouseover", "mouseout", "pointerover", "pointerout"] as const;

interface CallEvent {
  readonly phase?: unknown; readonly id?: unknown; readonly kind?: unknown; readonly request?: unknown;
  readonly route?: unknown; readonly contract?: unknown; readonly member?: unknown; readonly start?: unknown;
  readonly end?: unknown; readonly duration?: unknown; readonly outcome?: unknown; readonly reason?: unknown;
  readonly revision?: unknown;
}

interface Channel<T> { readonly listeners: Set<(event: T) => void>; requested?: boolean }

interface TranslatedText {
  readonly windowLocale?: string | null;
  readonly translations?: {
    resolve?(reference: TextReferenceLike, locale: string): string | undefined;
    getLocale?(): string;
    setLocale?: unknown;
  };
}

/** The `markers` member of the Runic Translations text-change channel (its ESM guide, "Message markers"). */
interface TranslationMarkers {
  readonly enabled: boolean;
  set(on: boolean): number;
  decode(text: string): readonly unknown[];
}

type MutableCall = { -readonly [K in keyof RunicInspectCall]: RunicInspectCall[K] };

const styles = `
:host { all: initial; }
* { box-sizing: border-box; }
.glass { position: fixed; inset: 0; pointer-events: auto; cursor: crosshair; background: transparent; }
.box { position: fixed; pointer-events: none; border: 2px solid #7c5cff; background: rgb(124 92 255 / 0.12);
  border-radius: 2px; display: none; }
.tip, .bar { position: fixed; pointer-events: none; font: 12px/1.4 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  color: #f4f2ff; background: #241f3d; border: 1px solid #4b3f8f; border-radius: 4px; padding: 2px 6px; white-space: nowrap; }
.tip { display: none; max-width: calc(100vw - 16px); overflow: hidden; text-overflow: ellipsis; }
.bar { left: 50%; top: 8px; transform: translateX(-50%); }
.card { position: fixed; right: 12px; bottom: 12px; width: min(420px, calc(100vw - 24px)); max-height: min(70vh, 640px);
  overflow: auto; pointer-events: auto; font: 12px/1.45 ui-sans-serif, system-ui, sans-serif; color: #ecebf5;
  background: #17152a; border: 1px solid #4b3f8f; border-radius: 8px; box-shadow: 0 8px 28px rgb(0 0 0 / 0.35); padding: 10px 12px; }
.card[hidden], .glass[hidden], .bar[hidden] { display: none; }
.card header { display: flex; gap: 8px; align-items: baseline; justify-content: space-between; margin-bottom: 6px; }
.card h2 { font: 600 13px/1.3 ui-sans-serif, system-ui, sans-serif; margin: 0; color: #fff; }
.card h3 { font: 600 11px/1.3 ui-sans-serif, system-ui, sans-serif; margin: 10px 0 4px; color: #b9b0ff; text-transform: uppercase; letter-spacing: 0.04em; }
.card ul { list-style: none; margin: 0; padding: 0; }
.card li { margin: 2px 0; overflow-wrap: anywhere; }
.card code { font: 11.5px/1.4 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; color: #ffd98e; }
.card .muted { color: #a29fbd; }
.card .note { color: #c8c3e6; font-style: italic; }
.card a { color: #8fd3ff; text-decoration: underline; cursor: pointer; }
.card button.close { all: unset; cursor: pointer; color: #c8c3e6; padding: 0 4px; font-size: 14px; }
.card button.close:focus-visible, .card a:focus-visible { outline: 2px solid #8fd3ff; outline-offset: 1px; }
.card .overview { display: contents; }
` + editorStyles;

function text(value: unknown): string {
  if (typeof value === "string") return JSON.stringify(value);
  try { return JSON.stringify(value) ?? String(value); } catch { return String(value); }
}

/** Whether a page's host name is this machine's loopback, as `/__runic/open` requires of its client. */
export function isLocalHostname(hostname: string): boolean {
  const name = hostname.toLowerCase().replace(/^\[|\]$/gu, "");
  return name === "localhost" || name.endsWith(".localhost") || name === "::1" || /^127(?:\.\d{1,3}){3}$/u.test(name);
}

function shortFile(file: string, root: string | undefined): string {
  if (root !== undefined && file.startsWith(`${root.replace(/[\\/]+$/u, "")}/`)) return file.slice(root.replace(/[\\/]+$/u, "").length + 1);
  const parts = file.split(/[\\/]/u);
  return parts.length > 3 ? `…/${parts.slice(-3).join("/")}` : file;
}

/** Installs Runic Inspect on the page; returns a function that removes it (for HMR). */
export function installInspect(options: InspectOptions = {}): () => void {
  const host = globalThis as unknown as Record<symbol, unknown> & { event?: Event; ng?: AngularDebug };
  if (typeof document === "undefined" || typeof Element === "undefined") return () => {};
  const { base = "/", root, request } = options;
  // A page of another machine: /__runic/open refuses it, so links show their location without opening it.
  const remote = typeof location === "object" && typeof location.hostname === "string" && location.hostname !== "" && !isLocalHostname(location.hostname);
  // The last refusals of the data endpoints (403), for the notes.
  let handlersForbidden = false;

  const learned = new WeakMap<Element, Map<string, LearnedBinding>>();
  const elementCalls = new WeakMap<Element, MutableCall[]>();
  const pendingCalls = new Map<number, MutableCall>();
  const states = new Map<string, unknown>();
  const contracts = new Map<string, string>();
  const modules = new Map<string, Promise<string | undefined>>();

  // Learning: a call's start arrives in the stack of the code that made it, so the event being dispatched names the
  // element. The event's target, not its currentTarget: React handles events on its root container.
  function onCall(event: CallEvent): void {
    if (typeof event !== "object" || event === null || typeof event.id !== "number") return;
    if (event.phase === "start") {
      if (typeof event.route !== "string" || typeof event.member !== "string") return;
      if (typeof event.contract === "string") contracts.set(event.route, event.contract);
      const dispatched = host.event;
      const origin = dispatched?.composedPath?.()[0] ?? dispatched?.target;
      if (!(origin instanceof Element) || overlay?.host.contains(origin)) return;
      const element = bindingElement(origin);
      const binding: LearnedBinding = { route: event.route, contract: String(event.contract ?? ""), kind: String(event.kind), member: event.member };
      let bindings = learned.get(element);
      if (bindings === undefined) learned.set(element, bindings = new Map());
      bindings.set(`${binding.route}\u0000${binding.kind}\u0000${binding.member}`, binding);
      const call: MutableCall = {
        id: event.id, kind: String(event.kind), request: String(event.request), route: event.route, member: event.member,
        start: typeof event.start === "number" ? event.start : 0,
      };
      let calls = elementCalls.get(element);
      if (calls === undefined) elementCalls.set(element, calls = []);
      calls.unshift(call);
      if (calls.length > maximumCalls) calls.length = maximumCalls;
      pendingCalls.set(event.id, call);
      if (pendingCalls.size > 200) pendingCalls.delete(pendingCalls.keys().next().value!);
    } else if (event.phase === "end") {
      const call = pendingCalls.get(event.id);
      if (call === undefined) return;
      pendingCalls.delete(event.id);
      if (typeof event.duration === "number") call.duration = event.duration;
      if (typeof event.outcome === "string") call.outcome = event.outcome;
      if (typeof event.reason === "string") call.reason = event.reason;
      if (typeof event.revision === "number") call.revision = event.revision;
    }
  }

  function onInspector(event: { kind?: unknown; route?: unknown; state?: unknown }): void {
    if (typeof event !== "object" || event === null || typeof event.route !== "string") return;
    if (event.kind === "state") states.set(event.route, event.state);
    else if (event.kind === "closed") states.delete(event.route);
  }

  const calls = (host[callsKey] ??= { listeners: new Set() }) as Channel<CallEvent>;
  calls.listeners.add(onCall);
  calls.requested = true;
  const inspector = (host[inspectorKey] ??= { listeners: new Set() }) as Channel<{ kind?: unknown; route?: unknown; state?: unknown }>;
  inspector.listeners.add(onInspector);
  inspector.requested = true;

  function bindingElement(element: Element): Element {
    return element.closest(interactiveSelector) ?? element;
  }

  function fetchText(url: string): Promise<string | undefined> {
    let pending = modules.get(url);
    if (pending === undefined) {
      pending = fetch(url, { credentials: "same-origin" })
        .then((response) => response.ok ? response.text() : undefined)
        .catch(() => undefined);
      modules.set(url, pending);
      if (modules.size > 100) modules.delete(modules.keys().next().value!);
    }
    return pending;
  }

  async function readSourceMap(): Promise<{ map?: RunicSourceMap; status?: "missing" | "unavailable" | "forbidden" }> {
    try {
      let status: number;
      let map: RunicSourceMap | undefined;
      if (request !== undefined) {
        const answer = await request("sources");
        status = answer.status;
        map = answer.body as RunicSourceMap | undefined;
      } else {
        const response = await fetch(sourcesPath, { credentials: "same-origin", cache: "no-store" });
        status = response.status;
        map = response.ok ? await response.json() as RunicSourceMap : undefined;
      }
      if (status === 404) return { status: "missing" };
      if (status === 403) return { status: "forbidden" };
      if (status !== 200) return { status: "unavailable" };
      return Array.isArray(map?.viewModels) && Array.isArray(map.members) ? { map } : { status: "unavailable" };
    } catch {
      return { status: "unavailable" };
    }
  }

  async function readAnalysis(file: string): Promise<RequestedAnalysis | undefined> {
    try {
      let analysis: RunicHandlerAnalysis | undefined;
      if (request !== undefined) {
        const answer = await request("handlers", file);
        handlersForbidden = answer.status === 403;
        analysis = answer.status === 200 ? answer.body as RunicHandlerAnalysis : undefined;
      } else {
        const response = await fetch(`${handlersPath}?file=${encodeURIComponent(file)}`, { credentials: "same-origin", cache: "no-store" });
        handlersForbidden = response.status === 403;
        if (!response.ok) return undefined;
        analysis = await response.json() as RunicHandlerAnalysis;
      }
      return analysis?.format === "runic-handlers/1" && Array.isArray(analysis.bindings) ? { ...analysis, requested: file } : undefined;
    } catch {
      return undefined;
    }
  }

  // The element's event bindings in its components' source (`/__runic/handlers`), before any click.
  async function readSourceBindings(element: Element): Promise<SourceBindings | undefined> {
    try {
      const probes = await findProbes(element as unknown as NodeLike, { root, ng: host.ng, fetchText });
      if (probes.length === 0) return undefined;
      const analyses = (await Promise.all(probeFiles(probes).slice(0, 6).map(readAnalysis)))
        .filter((analysis): analysis is RequestedAnalysis => analysis !== undefined);
      return matchBindings(probes, analyses);
    } catch {
      return undefined;
    }
  }

  function resolveText(reference: TextReferenceLike): { text: string; locale?: string } | undefined {
    const channel = host[translatedTextKey] as TranslatedText | undefined;
    const translations = channel?.translations;
    if (typeof translations?.resolve !== "function") return undefined;
    const windowLocale = channel?.windowLocale ?? null;
    const locale = windowLocale !== null && translations.setLocale === undefined ? windowLocale : translations.getLocale?.();
    if (typeof locale !== "string") return undefined;
    try {
      const resolved = translations.resolve(reference, locale);
      return typeof resolved === "string" ? { text: resolved, locale } : undefined;
    } catch {
      return undefined;
    }
  }

  function translationMarkers(): TranslationMarkers | undefined {
    const markers = (host[translationsTextKey] as { markers?: Partial<TranslationMarkers> } | undefined)?.markers;
    return typeof markers?.set === "function" && typeof markers.decode === "function" ? markers as TranslationMarkers : undefined;
  }

  // The messages whose markers are in the element's text or its attributes, with their sources.
  async function readMessages(element: Element): Promise<{
    messages: RunicInspectMessage[]; status: "on" | "off" | "unsupported" | "none"; sources?: "unavailable" | "forbidden";
  }> {
    const markers = translationMarkers();
    if (markers === undefined) return { messages: [], status: host[translationsTextKey] === undefined ? "none" : "unsupported" };
    const found: { message: unknown; in: string }[] = [];
    const decode = (text: string | null | undefined, where: string): void => {
      if (typeof text !== "string" || text.length === 0) return;
      try {
        for (const message of markers.decode(text)) found.push({ message, in: where });
      } catch { /* a foreign channel */ }
    };
    decode(element.textContent, "text");
    for (const name of messageAttributes) decode(element.getAttribute(name), name);
    if ((element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) && element.type !== "password") decode(element.value, "value");
    const messages = groupMessages(found);
    const status = markers.enabled ? "on" : "off";
    if (messages.length === 0) return { messages, status };
    let unavailable = false, forbidden = false;
    const replies = new Map<string, Promise<unknown>>();
    const withSources = await Promise.all(messages.map(async (message) => {
      const query = `key=${encodeURIComponent(message.key)}&catalog=${encodeURIComponent(message.catalog)}`;
      let reply = replies.get(query);
      if (reply === undefined) {
        reply = request !== undefined
          ? request("message", { catalog: message.catalog, key: message.key })
            .then((answer) => {
              if (answer.status === 403) forbidden = true;
              return answer.status === 200 ? answer.body : undefined;
            }).catch(() => undefined)
          : fetch(`${base.endsWith("/") ? base : `${base}/`}${messagePath}?${query}`, { credentials: "same-origin", cache: "no-store" })
            .then((response) => {
              if (response.status === 403) forbidden = true;
              return response.ok ? response.json() as Promise<unknown> : undefined;
            })
            .catch(() => undefined);
        replies.set(query, reply);
      }
      const value = await reply;
      if (value === undefined) unavailable = true;
      return { ...message, ...messageSource(value, message) };
    }));
    return { messages: withSources, status, ...(forbidden ? { sources: "forbidden" as const } : unavailable ? { sources: "unavailable" as const } : {}) };
  }

  async function describe(element: Element): Promise<RunicInspectDescription> {
    if (!(element instanceof Element)) throw new TypeError("describe needs an element.");
    const targets = new Set<Element>([bindingElement(element)]);
    if (element instanceof HTMLLabelElement && element.control) targets.add(element.control);
    const bindings: LearnedBinding[] = [];
    const elementCallList: RunicInspectCall[] = [];
    for (const target of targets) {
      bindings.push(...learned.get(target)?.values() ?? []);
      elementCallList.push(...(elementCalls.get(target) ?? []).map((call) => ({ ...call })));
    }
    elementCallList.sort((a, b) => b.id - a.id);
    // The text as the page shows it outside select mode: without message markers.
    const ownText = stripMarkers(element.children.length === 0 ? element.textContent ?? ""
      : [...element.childNodes].filter((node) => node.nodeType === 3).map((node) => node.textContent).join(" "));
    const control = element as Partial<HTMLInputElement>;
    const hasValue = (element instanceof HTMLInputElement && element.type !== "password" && element.type !== "checkbox" && element.type !== "radio")
      || element instanceof HTMLSelectElement || element instanceof HTMLTextAreaElement;
    const [sourceMap, component, source, messages] = await Promise.all([
      readSourceMap(),
      findComponent(element as unknown as NodeLike, { root, ng: host.ng, fetchText }),
      readSourceBindings(bindingElement(element)),
      readMessages(element),
    ]);
    const role = element.getAttribute("role");
    return assembleDescription({
      element: {
        tag: element.tagName.toLowerCase(),
        ...(element.id === "" ? {} : { id: element.id }),
        classes: [...element.classList].slice(0, 8),
        ...(role === null ? {} : { role }),
        text: normalizeText(stripMarkers(element.textContent ?? "")),
        ownText,
        ...(hasValue && typeof control.value === "string" ? { value: stripMarkers(control.value) } : {}),
        interactive: element.matches(interactiveSelector) || element.closest(interactiveSelector) !== null,
      },
      learned: bindings,
      calls: elementCallList,
      states,
      contracts,
      sourceMap: sourceMap.map,
      ...(sourceMap.status === undefined ? {} : { sourceMapStatus: sourceMap.status }),
      component,
      source,
      resolveText,
      messages: messages.messages,
      markerStatus: messages.status,
      messageSourcesStatus: messages.sources,
      handlersStatus: handlersForbidden ? "forbidden" : undefined,
      remote,
    });
  }

  async function open(location: RunicInspectLocation): Promise<boolean> {
    try {
      // One open path for every location: the endpoint validates each kind (sources.ts, resolveOpenLocation).
      const target = location.editor === "runic" && location.id !== undefined ? { id: location.id }
        : location.editor === "translations" && location.message !== undefined
          ? { message: { catalog: location.message.catalog, key: location.message.key, locale: location.message.locale } }
          : { file: location.file, line: location.line, column: location.column };
      if (request !== undefined) return (await request("open", target)).status === 200;
      const response = await fetch(openPath, {
        method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(target),
      });
      return response.ok;
    } catch {
      return false;
    }
  }

  // The overlay, created when select mode is first turned on.
  interface Overlay {
    readonly host: HTMLElement;
    readonly glass: HTMLElement;
    readonly box: HTMLElement;
    readonly tip: HTMLElement;
    readonly bar: HTMLElement;
    readonly card: HTMLElement;
    /** The overview's part of the card, rendered again for each description. */
    readonly overview: HTMLElement;
    /** The translation editor, which stays in the card while the overview is rendered again. */
    readonly editor: MessageEditor;
  }
  let overlay: Overlay | undefined;
  /** The element the card describes. */
  let selected: Element | undefined;
  /** The text-change channel the overview listens to, once there is one. */
  let textChannel: { listeners: Set<() => void> } | undefined;
  let refreshTimer: ReturnType<typeof setTimeout> | undefined;
  let active = false;
  let hovered: Element | undefined;
  let frame: number | undefined;
  let selection = 0;

  function createOverlay(): Overlay {
    const element = document.createElement(tagName);
    element.setAttribute("style", "all: initial; position: fixed; inset: 0; pointer-events: none; z-index: 2147483647;");
    const shadow = element.attachShadow({ mode: "open" });
    const style = document.createElement("style");
    style.textContent = styles;
    const make = (className: string, tag = "div"): HTMLElement => {
      const part = document.createElement(tag);
      part.className = className;
      shadow.append(part);
      return part;
    };
    shadow.append(style);
    const glass = make("glass");
    const box = make("box");
    const tip = make("tip");
    const bar = make("bar");
    bar.textContent = "Runic Inspect: click an element · Esc or Alt+Shift+X to exit";
    const card = make("card", "section");
    // Only select mode shows the glass, which takes the page's pointer events, and the bar.
    glass.hidden = bar.hidden = card.hidden = true;
    card.setAttribute("aria-label", "Runic Inspect");
    const overview = document.createElement("div");
    overview.className = "overview";
    const editor = createMessageEditor({
      base,
      location: link,
      onSaved: scheduleRefresh,
      onClose: (returnFocus) => {
        if (!returnFocus) return;
        const opener = [...overview.querySelectorAll<HTMLElement>("button.edit")].find((button) => button.dataset["message"] === editing);
        (opener ?? overview.querySelector<HTMLElement>("button.close"))?.focus();
      },
    });
    card.append(overview, editor.root);
    // Typing in the editor is not the page's: keys stop at the card instead of reaching the page's own handlers.
    for (const type of ["keydown", "keyup", "keypress"] as const) card.addEventListener(type, (event) => event.stopPropagation());
    document.documentElement.append(element);
    return { host: element, glass, box, tip, bar, card, overview, editor };
  }

  /** The `catalog:key` the editor was opened on, to return the focus to its Edit button. */
  let editing: string | undefined;

  function openEditor(message: RunicInspectMessage): void {
    if (overlay === undefined) return;
    listenToText();
    editing = `${message.catalog}:${message.key}`;
    void overlay.editor.open({ catalog: message.catalog, key: message.key, locale: message.locale });
  }

  // The page's text changed (an in-place update after a save, an edit in an editor, a language switch, marker mode):
  // once the frameworks rendered it, the editor reads its message again and the overview describes the same element.
  function listenToText(): void {
    const channel = host[translationsTextKey] as { listeners?: unknown } | undefined;
    if (textChannel !== undefined || !(channel?.listeners instanceof Set)) return;
    textChannel = channel as { listeners: Set<() => void> };
    textChannel.listeners.add(scheduleRefresh);
  }

  function scheduleRefresh(): void {
    if (overlay === undefined || overlay.card.hidden || refreshTimer !== undefined) return;
    refreshTimer = setTimeout(() => {
      refreshTimer = undefined;
      void refresh();
    }, 60);
  }

  async function refresh(): Promise<void> {
    if (overlay === undefined || overlay.card.hidden) return;
    void overlay.editor.refresh();
    // Outside select mode the page has no markers, so describing it again would lose the messages the card lists.
    if (selected === undefined || !selected.isConnected || translationMarkers()?.enabled !== true) return;
    const current = ++selection;
    const description = await describe(selected);
    if (current === selection && !overlay.card.hidden) render(description);
  }

  function elementAt(x: number, y: number): Element | undefined {
    const hostElement = overlay?.host;
    let element = document.elementsFromPoint(x, y).find((candidate) => candidate !== hostElement && !hostElement?.contains(candidate));
    // Into the page's own open shadow roots.
    for (let depth = 0; element?.shadowRoot && depth < 10; depth++) {
      const inner = element.shadowRoot.elementFromPoint(x, y);
      if (inner === null || inner === element) break;
      element = inner;
    }
    return element;
  }

  function outline(element: Element | undefined): void {
    if (overlay === undefined) return;
    const { box, tip } = overlay;
    if (element === undefined) {
      box.style.display = tip.style.display = "none";
      return;
    }
    const rect = element.getBoundingClientRect();
    Object.assign(box.style, { display: "block", left: `${rect.left}px`, top: `${rect.top}px`, width: `${rect.width}px`, height: `${rect.height}px` });
    const component = findComponentSync(element as unknown as NodeLike, root, host.ng)?.component;
    const id = element.id === "" ? "" : `#${element.id}`;
    tip.textContent = `${element.tagName.toLowerCase()}${id}${component === undefined ? "" : ` · ${component.name}`}`;
    tip.style.display = "block";
    const top = rect.top > 28 ? rect.top - 24 : Math.min(rect.bottom + 4, innerHeight - 24);
    Object.assign(tip.style, { left: `${Math.max(4, Math.min(rect.left, innerWidth - 240))}px`, top: `${top}px` });
  }

  function link(location: RunicInspectLocation): HTMLElement {
    if (remote) {
      // /__runic/open answers pages of the development machine only; the location is still worth knowing.
      const plain = document.createElement("span");
      plain.className = "muted";
      plain.textContent = `${shortFile(location.file, root)}:${location.line}`;
      plain.title = `${location.file}:${location.line}:${location.column} (opens in the editor only from the development machine)`;
      plain.dataset["location"] = location.editor;
      return plain;
    }
    const anchor = document.createElement("a");
    anchor.href = "#";
    anchor.textContent = `${shortFile(location.file, root)}:${location.line}`;
    anchor.title = `Open ${location.file}:${location.line}:${location.column} in the editor`;
    anchor.dataset["open"] = location.editor;
    anchor.addEventListener("click", (event) => {
      event.preventDefault();
      void open(location);
    });
    bound.set(anchor, JSON.stringify(location));
    return anchor;
  }

  // An overview control as it is rendered again: the same kind, message and text find its counterpart.
  function controlKey(element: Element): string {
    const control = element as HTMLElement;
    return [control.tagName, control.className, control.dataset["message"] ?? "", control.dataset["open"] ?? "",
      control.getAttribute("title") ?? "", control.textContent ?? ""].join("\u0000");
  }

  /** What an overview control's click handler acts on (a link's location, an Edit button's message). */
  const bound = new WeakMap<Element, string>();
  /** A control and what it acts on: a control with the same identity can stand in for another. */
  const identity = (control: Element): string => `${controlKey(control)}\u0000${bound.get(control) ?? ""}`;

  function render(description: RunicInspectDescription): void {
    const { host: overlayHost, card, overview: shown, editor } = overlay!;
    // The overview is drawn apart and replaces the shown one only where it differs. A refresh after each text change of
    // the page (scheduleRefresh) mostly describes the same thing; replacing the controls then would make a click whose
    // mousedown and mouseup straddle the refresh reach no link (#1062).
    const overview = document.createElement("div");
    const header = document.createElement("header");
    const title = document.createElement("h2");
    const { element } = description;
    title.textContent = `<${element.tag}${element.id === undefined ? "" : `#${element.id}`}>${element.text === "" ? "" : ` ${element.text.slice(0, 40)}`}`;
    const close = document.createElement("button");
    close.className = "close";
    close.textContent = "×";
    close.title = "Close";
    close.addEventListener("click", () => {
      editor.close();
      card.hidden = true;
    });
    header.append(title, close);
    overview.append(header);

    const section = (heading: string, items: (string | Node)[][]): void => {
      if (items.length === 0) return;
      const h3 = document.createElement("h3");
      h3.textContent = heading;
      const list = document.createElement("ul");
      for (const parts of items) {
        const item = document.createElement("li");
        item.append(...parts);
        list.append(item);
      }
      overview.append(h3, list);
    };
    const code = (value: string): HTMLElement => {
      const element = document.createElement("code");
      element.textContent = value;
      return element;
    };
    const muted = (value: string): HTMLElement => {
      const element = document.createElement("span");
      element.className = "muted";
      element.textContent = value;
      return element;
    };
    const component = description.component;
    if (component !== undefined) {
      section("Component", [[
        code(component.name), muted(` ${component.framework}`),
        ...(component.owners.length === 0 ? [] : [muted(` in ${component.owners.slice(0, 3).join(" › ")}`)]),
        ...(component.location === undefined ? [] : [" ", link(component.location)]),
      ]]);
    }
    section("Commands", description.commands.map((command) => [
      code(`${command.member}()`), " → ", code(command.symbol ?? "?"),
      ...(command.location === undefined ? [] : [" ", link(command.location)]),
      command.evidence === "source" ? muted(" · from the source, not called yet") : muted(` · ${command.calls} call${command.calls === 1 ? "" : "s"}`),
      ...(command.frontend === undefined ? [] : [muted(" · called at "), link(command.frontend)]),
      ...(command.canExecute === undefined ? [] : [muted(` · can run: ${text(command.canExecute)}`)]),
    ]));
    section("ViewModel members", description.members.map((member) => [
      code(member.member), muted(` ${member.kind}`), " = ", code(text(member.value)),
      muted(member.evidence === "call" ? "" : member.evidence === "text" ? " · matches the text" : " · matches the value"),
      ...(member.symbol === undefined ? [] : [" → ", code(member.symbol)]),
      ...(member.location === undefined ? [] : [" ", link(member.location)]),
    ]));
    section("ViewModels", description.viewModels.map((viewModel) => [
      code(viewModel.name ?? viewModel.id ?? viewModel.route), muted(` route ${viewModel.route}`),
      ...(viewModel.id === undefined ? [] : [muted(` · ${viewModel.id}`)]),
      ...(viewModel.location === undefined ? [] : [" ", link(viewModel.location)]),
    ]));
    section("Recent Bridge calls", description.calls.slice(0, 5).map((call) => [
      code(`${call.kind} ${call.member}`), muted(` #${call.id}`),
      ` ${call.duration === undefined ? "running" : `${call.duration.toFixed(1)} ms`}`,
      ...(call.outcome === undefined ? [] : [` ${call.outcome}${call.reason === undefined ? "" : ` (${call.reason})`}`]),
      ...(call.revision === undefined ? [] : [muted(` · revision ${call.revision}`)]),
    ]));
    section("Messages", description.messages.map((message) => [
      code(`${message.catalog}:${message.key}`), message.locale === "" ? "" : muted(` [${message.locale}]`),
      ...(message.source === undefined ? [] : [` ${JSON.stringify(message.source.length > 80 ? `${message.source.slice(0, 79)}…` : message.source)}`]),
      ...(message.in.length === 1 && message.in[0] === "text" ? [] : [muted(` · in ${message.in.join(", ")}`)]),
      ...(message.location === undefined ? [] : [" ", link(message.location)]),
      ...(message.editable === true ? [editButton(message)] : []),
    ]));
    section("Texts from the ViewModel", description.texts.map((entry) => [
      code(`${entry.catalog}:${entry.key}`), entry.locale === undefined ? "" : muted(` [${entry.locale}]`),
      ` ${JSON.stringify(entry.text)}`, muted(` · ${entry.route}.${entry.path}`),
    ]));
    for (const note of description.notes) {
      const paragraph = document.createElement("p");
      paragraph.className = "note";
      paragraph.textContent = note;
      overview.append(paragraph);
    }
    card.hidden = false;
    const focused = overlayHost.shadowRoot?.activeElement;
    const refocus = focused instanceof Element && shown.contains(focused) ? controlKey(focused) : undefined;
    patchChildren(shown, overview);
    // A refresh can arrive while the focus is on a control that is now gone, such as the Edit button Esc returned to:
    // the focus moves to its counterpart instead of falling to the document.
    const active = overlayHost.shadowRoot?.activeElement;
    if (refocus !== undefined && !(active instanceof Element && shown.contains(active))) {
      const counterpart = [...shown.querySelectorAll<HTMLElement>("button, a")].find((control) => controlKey(control) === refocus)
        ?? shown.querySelector<HTMLElement>("button.close");
      counterpart?.focus({ preventScroll: true });
    }
  }

  /** The overview's controls in and under `node`, which carry click handlers. */
  function controlsOf(node: Node): Element[] {
    if (!(node instanceof Element)) return [];
    return [...node.matches("button, a") ? [node] : [], ...node.querySelectorAll("button, a")];
  }

  /** Whether `node` can stay for `next`: drawn the same way, and its controls act on the same things. */
  function same(node: Node, next: Node): boolean {
    if (!node.isEqualNode(next)) return false;
    const controls = controlsOf(node), nextControls = controlsOf(next);
    return controls.every((control, index) => bound.get(control) === bound.get(nextControls[index]!));
  }

  /** Whether `node` can be updated into `next` in place: the same kind of node, and not a control acting on another thing. */
  function patchable(node: Node, next: Node): boolean {
    if (node.nodeType !== next.nodeType || node.nodeName !== next.nodeName) return false;
    return !(node instanceof Element && node.matches("button, a")) || identity(node) === identity(next as Element);
  }

  /**
   * Makes `parent`'s children look like `next`'s, keeping the nodes that can stay where they are. A kept node is never
   * moved: moving a link between a click's mousedown and mouseup loses the click as replacing it does.
   */
  function patchChildren(parent: Node, next: Node): void {
    let cursor = parent.firstChild;
    for (const node of [...next.childNodes]) {
      let match: ChildNode | null = cursor;
      while (match !== null && !same(match, node)) match = match.nextSibling;
      if (match !== null) {
        while (cursor !== match) {
          const removed: ChildNode = cursor!;
          cursor = removed.nextSibling;
          removed.remove();
        }
        cursor = match.nextSibling;
      } else if (cursor !== null && patchable(cursor, node)) {
        patch(cursor, node);
        cursor = cursor.nextSibling;
      } else {
        parent.insertBefore(node, cursor);
      }
    }
    while (cursor !== null) {
      const removed: ChildNode = cursor;
      cursor = removed.nextSibling;
      removed.remove();
    }
  }

  function patch(node: ChildNode, next: ChildNode): void {
    if (!(node instanceof Element) || !(next instanceof Element)) {
      if (node.nodeValue !== next.nodeValue) node.nodeValue = next.nodeValue;
      return;
    }
    for (const { name } of [...node.attributes]) if (!next.hasAttribute(name)) node.removeAttribute(name);
    for (const { name, value } of next.attributes) if (node.getAttribute(name) !== value) node.setAttribute(name, value);
    patchChildren(node, next);
  }

  function editButton(message: RunicInspectMessage): HTMLElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "edit";
    button.textContent = "Edit";
    button.dataset["message"] = `${message.catalog}:${message.key}`;
    button.setAttribute("aria-label", `Edit ${message.catalog}:${message.key}`);
    button.addEventListener("click", () => openEditor(message));
    bound.set(button, JSON.stringify(message));
    return button;
  }

  async function select(element: Element): Promise<RunicInspectDescription> {
    overlay ??= createOverlay();
    listenToText();
    // Another element: an editor without unsaved text closes; one with unsaved text stays open.
    if (element !== selected && overlay.editor.isOpen && overlay.editor.state !== undefined && !hasEdits(overlay.editor.state)) overlay.editor.close();
    selected = element;
    const current = ++selection;
    const description = await describe(element);
    if (current === selection) render(description);
    return description;
  }

  function setActive(on: boolean): boolean {
    if (on === active) return active;
    active = on;
    // Message markers only while selecting: the page renders again with them, and again without them.
    try { translationMarkers()?.set(on); } catch { /* a foreign channel */ }
    if (on) overlay ??= createOverlay();
    if (overlay !== undefined) {
      overlay.glass.hidden = overlay.bar.hidden = !on;
      if (!on) outline(hovered = undefined);
    }
    return active;
  }

  function isOverlayUi(event: Event): boolean {
    if (overlay === undefined) return false;
    const path = event.composedPath();
    return path.includes(overlay.card);
  }

  // In select mode the glass takes every pointer event; the window's capture phase stops them before the page's
  // handlers (document, window and React's root listeners) see them, and handles them here.
  function onPointer(event: Event): void {
    if (!active || isOverlayUi(event)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    const pointer = event as MouseEvent;
    if (event.type === "pointermove" || event.type === "mousemove") {
      const { clientX, clientY } = pointer;
      if (frame !== undefined) return;
      frame = requestAnimationFrame(() => {
        frame = undefined;
        const element = elementAt(clientX, clientY);
        if (element !== hovered) outline(hovered = element);
      });
    } else if (event.type === "pointerdown" || (event.type === "mousedown" && typeof PointerEvent === "undefined")) {
      if (pointer.button !== 0) return;
      const element = elementAt(pointer.clientX, pointer.clientY);
      if (element === undefined) return;
      outline(hovered = element);
      void select(element);
    }
  }

  function onKey(event: KeyboardEvent): void {
    const toggle = event.altKey && event.shiftKey && !event.ctrlKey && !event.metaKey
      && (event.code === "KeyX" || event.key === "X" || event.key === "x");
    if (toggle) {
      event.preventDefault();
      event.stopImmediatePropagation();
      setActive(!active);
    } else if (event.key === "Escape" && overlay?.editor.isOpen === true && event.composedPath().includes(overlay.editor.root)) {
      // Esc in the editor closes the editor only, and the focus returns to its Edit button.
      event.preventDefault();
      event.stopImmediatePropagation();
      overlay.editor.close(true);
    } else if (event.key === "Escape" && (active || overlay?.card.hidden === false)) {
      event.preventDefault();
      event.stopImmediatePropagation();
      if (active) setActive(false);
      else overlay!.card.hidden = true;
    }
  }

  for (const type of swallowed) addEventListener(type, onPointer, { capture: true, passive: false });
  addEventListener("keydown", onKey, { capture: true });

  const api: RunicInspectApi = {
    version: 1,
    get active() { return active; },
    toggle: (on) => setActive(on ?? !active),
    describe,
    select,
    open,
  };
  host[inspectKey] = api;

  return () => {
    calls.listeners.delete(onCall);
    inspector.listeners.delete(onInspector);
    for (const type of swallowed) removeEventListener(type, onPointer, { capture: true });
    removeEventListener("keydown", onKey, { capture: true });
    if (frame !== undefined) cancelAnimationFrame(frame);
    if (active) try { translationMarkers()?.set(false); } catch { /* a foreign channel */ }
    textChannel?.listeners.delete(scheduleRefresh);
    if (refreshTimer !== undefined) clearTimeout(refreshTimer);
    overlay?.host.remove();
    overlay = undefined;
    if (host[inspectKey] === api) delete host[inspectKey];
  };
}
