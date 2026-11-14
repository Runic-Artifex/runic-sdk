// Runic Inspect: the development client's select mode. Alt+Shift+X (or `toggle()`) turns it on; hovering outlines an
// element, and clicking it shows an overview of how it is connected: its framework component, the ViewModel members
// it binds with their values and C# members, the commands it calls with their C# handlers, its recent Bridge calls,
// and the texts its ViewModel sent, each location with an "open in editor" link. While it is on, the page's own
// handlers see no pointer event: a transparent layer takes them, and the window's capture phase stops them.
//
// What an element calls is learned from the Bridge-call channel (`runic.views.calls`): its `start` report arrives in
// the caller's stack, so the DOM event being dispatched (`globalThis.event`) names the element. A button therefore
// shows its command once it was used. States come from the state inspector channel (`runic.views.inspector`), C#
// locations from the development source map (`/__runic/sources`), opening from `/__runic/open` (C#) and Vite's
// `/__open-in-editor` (frontend files).
//
// `globalThis[Symbol.for("runic.vite.inspect")]` is the API for agents and tests: `describe(element)` returns the
// overview as JSON-safe data. Everything here is part of the development client, which a build replaces with an empty
// module; it uses only the DOM, so every engine under `dotnet runic dev` runs it.

import type { RunicSourceMap } from "./sources.js";
import { openPath, sourcesPath } from "./channel.js";
import {
  assembleDescription, normalizeText, type LearnedBinding, type RunicInspectCall, type RunicInspectDescription,
  type RunicInspectLocation, type TextReferenceLike,
} from "./inspect-data.js";
import { findComponent, findComponentSync, type AngularDebug, type NodeLike } from "./inspect-frameworks.js";

/** The value at `globalThis[Symbol.for("runic.vite.inspect")]`. */
export interface RunicInspectApi {
  readonly version: 1;
  /** Whether select mode is on. */
  readonly active: boolean;
  /** Turns select mode on or off (the opposite of now without an argument); returns whether it is on. */
  toggle(on?: boolean): boolean;
  /** The overview of an element, as JSON-safe data. */
  describe(element: Element): Promise<RunicInspectDescription>;
  /** Shows the overview of an element, as clicking it in select mode does, and returns it. */
  select(element: Element): Promise<RunicInspectDescription>;
  /** Opens a location of an overview in the editor; resolves to whether the server accepted it. */
  open(location: RunicInspectLocation): Promise<boolean>;
}

export interface InspectOptions {
  readonly base?: string | undefined;
  readonly root?: string | undefined;
}

export const inspectKey = Symbol.for("runic.vite.inspect");
const callsKey = Symbol.for("runic.views.calls");
const inspectorKey = Symbol.for("runic.views.inspector");
const translatedTextKey = Symbol.for("runic.views.translated-text");
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
`;

function text(value: unknown): string {
  if (typeof value === "string") return JSON.stringify(value);
  try { return JSON.stringify(value) ?? String(value); } catch { return String(value); }
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
  const { base = "/", root } = options;

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

  async function readSourceMap(): Promise<{ map?: RunicSourceMap; status?: "missing" | "unavailable" }> {
    try {
      const response = await fetch(sourcesPath, { credentials: "same-origin", cache: "no-store" });
      if (response.status === 404) return { status: "missing" };
      if (!response.ok) return { status: "unavailable" };
      const map = await response.json() as RunicSourceMap;
      return Array.isArray(map?.viewModels) && Array.isArray(map.members) ? { map } : { status: "unavailable" };
    } catch {
      return { status: "unavailable" };
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
    const ownText = element.children.length === 0 ? element.textContent ?? ""
      : [...element.childNodes].filter((node) => node.nodeType === 3).map((node) => node.textContent).join(" ");
    const control = element as Partial<HTMLInputElement>;
    const hasValue = (element instanceof HTMLInputElement && element.type !== "password" && element.type !== "checkbox" && element.type !== "radio")
      || element instanceof HTMLSelectElement || element instanceof HTMLTextAreaElement;
    const [sourceMap, component] = await Promise.all([
      readSourceMap(),
      findComponent(element as unknown as NodeLike, { root, ng: host.ng, fetchText }),
    ]);
    const role = element.getAttribute("role");
    return assembleDescription({
      element: {
        tag: element.tagName.toLowerCase(),
        ...(element.id === "" ? {} : { id: element.id }),
        classes: [...element.classList].slice(0, 8),
        ...(role === null ? {} : { role }),
        text: normalizeText(element.textContent ?? ""),
        ownText,
        ...(hasValue && typeof control.value === "string" ? { value: control.value } : {}),
        interactive: element.matches(interactiveSelector) || element.closest(interactiveSelector) !== null,
      },
      learned: bindings,
      calls: elementCallList,
      states,
      contracts,
      sourceMap: sourceMap.map,
      ...(sourceMap.status === undefined ? {} : { sourceMapStatus: sourceMap.status }),
      component,
      resolveText,
    });
  }

  async function open(location: RunicInspectLocation): Promise<boolean> {
    try {
      const response = location.editor === "runic" && location.id !== undefined
        ? await fetch(openPath, {
          method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ id: location.id }),
        })
        : await fetch(`${base.endsWith("/") ? base : `${base}/`}__open-in-editor?file=${
          encodeURIComponent(`${location.file}:${location.line}:${location.column}`)}`, { credentials: "same-origin" });
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
  }
  let overlay: Overlay | undefined;
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
    document.documentElement.append(element);
    return { host: element, glass, box, tip, bar, card };
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
    const anchor = document.createElement("a");
    anchor.href = "#";
    anchor.textContent = `${shortFile(location.file, root)}:${location.line}`;
    anchor.title = `Open ${location.file}:${location.line}:${location.column} in the editor`;
    anchor.dataset["open"] = location.editor;
    anchor.addEventListener("click", (event) => {
      event.preventDefault();
      void open(location);
    });
    return anchor;
  }

  function render(description: RunicInspectDescription): void {
    const card = overlay!.card;
    card.replaceChildren();
    const header = document.createElement("header");
    const title = document.createElement("h2");
    const { element } = description;
    title.textContent = `<${element.tag}${element.id === undefined ? "" : `#${element.id}`}>${element.text === "" ? "" : ` ${element.text.slice(0, 40)}`}`;
    const close = document.createElement("button");
    close.className = "close";
    close.textContent = "×";
    close.title = "Close";
    close.addEventListener("click", () => { card.hidden = true; });
    header.append(title, close);
    card.append(header);

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
      card.append(h3, list);
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
      muted(` · ${command.calls} call${command.calls === 1 ? "" : "s"}`),
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
    section("Texts from the ViewModel", description.texts.map((entry) => [
      code(`${entry.catalog}:${entry.key}`), entry.locale === undefined ? "" : muted(` [${entry.locale}]`),
      ` ${JSON.stringify(entry.text)}`, muted(` · ${entry.route}.${entry.path}`),
    ]));
    for (const note of description.notes) {
      const paragraph = document.createElement("p");
      paragraph.className = "note";
      paragraph.textContent = note;
      card.append(paragraph);
    }
    card.hidden = false;
  }

  async function select(element: Element): Promise<RunicInspectDescription> {
    overlay ??= createOverlay();
    const current = ++selection;
    const description = await describe(element);
    if (current === selection) render(description);
    return description;
  }

  function setActive(on: boolean): boolean {
    if (on === active) return active;
    active = on;
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
    overlay?.host.remove();
    overlay = undefined;
    if (host[inspectKey] === api) delete host[inspectKey];
  };
}
