// The page's appearance (`appearance/1`, specs/application/README.md, "Appearance"): the colour scheme, contrast,
// reduced motion, text scale, accent colour and forced-colours palette. The engine's CSS media queries give what the
// page's CSS sees; the host pushes what it read from the desktop, including the values no engine exposes to script.
// The store reports both, and combines them: the latest change of a member wins.
//
// The page runtime keeps only the raw push (appearance-push.ts); this module reads it when a page asks, so a page that
// never reads the appearance does not bundle it. The state is page-wide and shared by every copy of this package on the
// page, like the connection state.
import { appearancePushChannel, type AppearancePushChannel } from "./appearance-push.js";
import { reportBridgeError } from "./transport.js";

export { appearanceFeature } from "./appearance-push.js";

/** A light or dark colour scheme. */
export type AppearanceColorScheme = "light" | "dark";

/**
 * The contrast preference: none, more contrast with the page's own colours, or colours that the desktop forces on the
 * page (CSS `forced-colors: active`). A later version may add values; the store reports a value it does not know as
 * `null`.
 */
export type AppearanceContrast = "no-preference" | "more" | "forced-colors";

/**
 * The palette a desktop forces while `contrast` is `"forced-colors"`, as `#rrggbb` colours named after the CSS system
 * colours. On Windows, the contrast theme's colours.
 */
export interface AppearanceSystemColors {
  readonly canvas: string;
  readonly canvasText: string;
  readonly linkText: string;
  readonly grayText: string;
  readonly highlight: string;
  readonly highlightText: string;
  readonly buttonFace: string;
  readonly buttonText: string;
}

/** The desktop's appearance as the host pushed it (`appearance/1`). `null` is a preference the host does not know. */
export interface HostAppearance {
  /** The desktop's colour scheme. A window with a scheme of its own shows it in `prefers-color-scheme` instead. */
  readonly colorScheme: AppearanceColorScheme | null;
  readonly contrast: AppearanceContrast | null;
  readonly reducedMotion: boolean | null;
  /** The desktop's text size as a factor of the default: 1 for the default, 1.5 for half as large again. */
  readonly textScale: number | null;
  /** The desktop's accent colour, `#rrggbb`. */
  readonly accentColor: string | null;
  /** The forced palette while `contrast` is `"forced-colors"`, otherwise `null`. */
  readonly systemColors: AppearanceSystemColors | null;
}

/**
 * What the page's engine reports through its CSS media queries, which is what the page's CSS sees. `null` is a media
 * feature the engine does not evaluate, and every member under server rendering.
 */
export interface EngineAppearance {
  /** `prefers-color-scheme`, which follows a window's own colour scheme. */
  readonly colorScheme: AppearanceColorScheme | null;
  /** `forced-colors: active`, otherwise `prefers-contrast: more`, otherwise `"no-preference"`. */
  readonly contrast: AppearanceContrast | null;
  /** `prefers-reduced-motion: reduce`. */
  readonly reducedMotion: boolean | null;
}

/**
 * The page's appearance. `engine` is what the page's CSS sees and `host` what the host read from the desktop; the other
 * members combine them:
 * - `colorScheme` is the engine's, which follows a window's own scheme, or the host's without it.
 * - `contrast` and `reducedMotion` are the latest change: the engine's value, until the host pushes a change of the
 *   member, whose value then applies until the engine's media query changes. An engine that reads the desktop's setting
 *   only when the page loads, or does not follow it at all, so reports the desktop's later changes. Without the
 *   engine's value they are the host's.
 * - `textScale`, `accentColor` and `systemColors` come from the host alone.
 *
 * `null` is a preference neither source knows, as under server rendering. To tell "the desktop asks for forced colours"
 * from "this page's colours are forced", read `host.contrast` and `engine.contrast`.
 */
export interface Appearance {
  readonly colorScheme: AppearanceColorScheme | null;
  readonly contrast: AppearanceContrast | null;
  readonly reducedMotion: boolean | null;
  readonly textScale: number | null;
  readonly accentColor: string | null;
  readonly systemColors: AppearanceSystemColors | null;
  /** The engine's media queries alone. */
  readonly engine: EngineAppearance;
  /** What the host pushed, or `null` before its first push and with a host without `appearance/1`. */
  readonly host: HostAppearance | null;
}

interface MediaQuery {
  readonly matches: boolean;
  addEventListener?(type: "change", listener: () => void): void;
  removeEventListener?(type: "change", listener: () => void): void;
  addListener?(listener: () => void): void;
  removeListener?(listener: () => void): void;
}

const queryTexts = [
  "(prefers-color-scheme: dark)", "(prefers-color-scheme: light)",
  "(forced-colors: active)", "(forced-colors: none)",
  "(prefers-contrast: more)", "(prefers-contrast: no-preference)", "(prefers-contrast: less)", "(prefers-contrast: custom)",
  "(prefers-reduced-motion: reduce)", "(prefers-reduced-motion: no-preference)",
] as const;
type QueryText = typeof queryTexts[number];

// The engine's value of a member when the host's change of it took over.
interface Takeover<T> { readonly engine: T }

interface AppearanceStore {
  // The raw push the store last read, and what it read from it.
  seen: Readonly<Record<string, unknown>> | undefined;
  host: HostAppearance | null;
  contrast: Takeover<AppearanceContrast | null> | undefined;
  reducedMotion: Takeover<boolean | null> | undefined;
  current: Appearance | undefined;
  readonly listeners: Set<(appearance: Appearance) => void>;
  // The media queries while someone listens, so their changes reach the listeners.
  queries?: { readonly lists: ReadonlyMap<QueryText, MediaQuery>; readonly stop: () => void } | undefined;
}

type AppearanceChannel = AppearancePushChannel & { store?: AppearanceStore };

// The store, which from now on hears each push the runtime receives.
function channel(): AppearanceChannel & { store: AppearanceStore } {
  const state = appearancePushChannel() as AppearanceChannel;
  if (!state.store) {
    state.store = { seen: undefined, host: null, contrast: undefined, reducedMotion: undefined, current: undefined, listeners: new Set() };
    state.received = () => notify(state as AppearanceChannel & { store: AppearanceStore });
  }
  return state as AppearanceChannel & { store: AppearanceStore };
}

function matchMediaOf(): ((query: string) => MediaQuery) | undefined {
  const match = (globalThis as { matchMedia?: unknown }).matchMedia;
  return typeof match === "function" ? (query: string) => (match as (query: string) => MediaQuery).call(globalThis, query) : undefined;
}

// The engine's values, or null for a media feature the engine does not evaluate (no value of it matches).
function engine(read: (query: QueryText) => boolean | undefined) {
  const matches = (query: QueryText) => read(query) === true;
  const colorScheme: AppearanceColorScheme | null = matches("(prefers-color-scheme: dark)") ? "dark"
    : matches("(prefers-color-scheme: light)") ? "light" : null;
  const contrast: AppearanceContrast | null = matches("(forced-colors: active)") ? "forced-colors"
    : matches("(prefers-contrast: more)") ? "more"
    : matches("(forced-colors: none)") || matches("(prefers-contrast: no-preference)") || matches("(prefers-contrast: less)")
      || matches("(prefers-contrast: custom)") ? "no-preference" : null;
  const reducedMotion = matches("(prefers-reduced-motion: reduce)") ? true
    : matches("(prefers-reduced-motion: no-preference)") ? false : null;
  return { colorScheme, contrast, reducedMotion };
}

function readEngine(state: AppearanceStore): EngineAppearance {
  const lists = state.queries?.lists;
  const match = lists ? undefined : matchMediaOf();
  return engine((query: QueryText): boolean | undefined => {
    if (lists) return lists.get(query)?.matches;
    try { return match?.(query).matches; } catch { return undefined; }
  });
}

// Reads a push the store has not read yet. A member the host changed from its previous known value takes over from the
// engine; one the host no longer knows hands back to it.
function readPush(channel: AppearanceChannel, state: AppearanceStore, page: EngineAppearance): void {
  if (channel.push === state.seen) return;
  state.seen = channel.push;
  const previous = state.host;
  const next = channel.push === undefined ? null : readHostAppearance(channel.push);
  state.host = sameHost(previous, next) ? previous : next;
  const changed = <T>(member: "contrast" | "reducedMotion", current: Takeover<T> | undefined, engineValue: T): Takeover<T> | undefined => {
    const before = previous?.[member] ?? null, after = next?.[member] ?? null;
    if (after === null) return undefined;
    return before !== null && before !== after ? { engine: engineValue } : current;
  };
  state.contrast = changed("contrast", state.contrast, page.contrast);
  state.reducedMotion = changed("reducedMotion", state.reducedMotion, page.reducedMotion);
}

function compute(channel: AppearanceChannel & { store: AppearanceStore }): Appearance {
  const state = channel.store;
  const page = readEngine(state);
  readPush(channel, state, page);
  // The engine's change since the host's took over is the latest.
  if (state.contrast && state.contrast.engine !== page.contrast) state.contrast = undefined;
  if (state.reducedMotion && state.reducedMotion.engine !== page.reducedMotion) state.reducedMotion = undefined;
  const host = state.host;
  const current = state.current;
  const engineValue = current && current.engine.colorScheme === page.colorScheme && current.engine.contrast === page.contrast
    && current.engine.reducedMotion === page.reducedMotion ? current.engine : Object.freeze(page);
  const next: Appearance = {
    colorScheme: page.colorScheme ?? host?.colorScheme ?? null,
    contrast: state.contrast ? host!.contrast : page.contrast ?? host?.contrast ?? null,
    reducedMotion: state.reducedMotion ? host!.reducedMotion : page.reducedMotion ?? host?.reducedMotion ?? null,
    textScale: host?.textScale ?? null,
    accentColor: host?.accentColor ?? null,
    systemColors: host?.systemColors ?? null,
    engine: engineValue,
    host,
  };
  // The same object while nothing changed, for framework snapshots.
  if (current && current.colorScheme === next.colorScheme && current.contrast === next.contrast
    && current.reducedMotion === next.reducedMotion && current.engine === next.engine && current.host === next.host) return current;
  return state.current = Object.freeze(next);
}

function notify(state: AppearanceChannel & { store: AppearanceStore }): void {
  const previous = state.store.current;
  const next = compute(state);
  if (next === previous) return;
  for (const listener of [...state.store.listeners]) {
    try { listener(next); } catch (error) { reportBridgeError(error); }
  }
}

function watchQueries(channel: AppearanceChannel & { store: AppearanceStore }): void {
  const state = channel.store;
  const match = matchMediaOf();
  if (!match || state.queries) return;
  const lists = new Map<QueryText, MediaQuery>();
  const changed = () => notify(channel);
  for (const query of queryTexts) {
    try {
      const list = match(query);
      if (list.addEventListener) list.addEventListener("change", changed);
      else list.addListener?.(changed);
      lists.set(query, list);
    } catch { /* An engine that refuses the query does not evaluate it. */ }
  }
  state.queries = {
    lists,
    stop: () => {
      for (const list of lists.values()) {
        if (list.removeEventListener) list.removeEventListener("change", changed);
        else list.removeListener?.(changed);
      }
    },
  };
}

/**
 * The appearance with every member `null`: what {@link appearance} reports under server rendering, and the server
 * snapshot of the framework hooks.
 */
export const unknownAppearance: Appearance = Object.freeze({
  colorScheme: null, contrast: null, reducedMotion: null, textScale: null, accentColor: null, systemColors: null,
  engine: Object.freeze({ colorScheme: null, contrast: null, reducedMotion: null }), host: null,
});

/**
 * The page's appearance now: the colour scheme, contrast and reduced motion of the page's engine combined with the
 * desktop's appearance that a Runic host pushes (`appearance/1`, `RunicDesktopHost` in .NET), which also carries the
 * text scale, the accent colour and the forced-colours palette; `engine` and `host` report each source alone. The same
 * frozen object until something changes. Under server rendering every member is `null`.
 */
export function appearance(): Appearance {
  return compute(channel());
}

/**
 * Calls `listener` with the page's appearance each time it changes, from the engine's media queries or a host push,
 * until the returned function is called.
 */
export function onAppearance(listener: (appearance: Appearance) => void): () => void {
  const state = channel();
  compute(state);
  state.store.listeners.add(listener);
  watchQueries(state);
  let stopped = false;
  return () => {
    if (stopped) return;
    stopped = true;
    const store = state.store;
    store.listeners.delete(listener);
    if (store.listeners.size === 0 && store.queries) {
      store.queries.stop();
      store.queries = undefined;
    }
  };
}

const colorPattern = /^#[0-9a-f]{6}$/;
const systemColorNames = ["canvas", "canvasText", "linkText", "grayText", "highlight", "highlightText", "buttonFace", "buttonText"] as const;

function readColor(value: unknown): string | null {
  return typeof value === "string" && colorPattern.test(value) ? value : null;
}

/**
 * Reads the members of an `appearance` push. A member of the wrong type, out of range or with a value this version does
 * not know is `null`; the rest still applies.
 */
export function readHostAppearance(push: Readonly<Record<string, unknown>>): HostAppearance {
  const colorScheme = push["colorScheme"] === "light" || push["colorScheme"] === "dark" ? push["colorScheme"] : null;
  const contrast = push["contrast"] === "no-preference" || push["contrast"] === "more" || push["contrast"] === "forced-colors"
    ? push["contrast"] : null;
  const reducedMotion = typeof push["reducedMotion"] === "boolean" ? push["reducedMotion"] : null;
  const scale = push["textScale"];
  const textScale = typeof scale === "number" && Number.isFinite(scale) && scale > 0 ? scale : null;
  let systemColors: AppearanceSystemColors | null = null;
  const colors = push["systemColors"];
  if (typeof colors === "object" && colors !== null && !Array.isArray(colors)) {
    const entries = systemColorNames.map(name => [name, readColor((colors as Record<string, unknown>)[name])] as const);
    if (entries.every(([, color]) => color !== null)) systemColors = Object.freeze(Object.fromEntries(entries)) as unknown as AppearanceSystemColors;
  }
  return Object.freeze({ colorScheme, contrast, reducedMotion, textScale, accentColor: readColor(push["accentColor"]), systemColors });
}

function sameHost(first: HostAppearance | null, second: HostAppearance | null): boolean {
  if (first === second) return true;
  if (first === null || second === null) return false;
  return first.colorScheme === second.colorScheme && first.contrast === second.contrast && first.reducedMotion === second.reducedMotion
    && first.textScale === second.textScale && first.accentColor === second.accentColor
    && (first.systemColors === second.systemColors || first.systemColors !== null && second.systemColors !== null
      && systemColorNames.every(name => first.systemColors![name] === second.systemColors![name]));
}
