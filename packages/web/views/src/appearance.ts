// The page's appearance (`appearance/1`, specs/application/README.md, "Appearance"): the colour scheme, contrast,
// reduced motion, text scale, accent colour and forced-colours palette. The engine's CSS media queries give what the
// page's CSS sees; the host pushes what it read from the desktop, including the values no engine exposes to script.
// The store combines both.
//
// The state is page-wide and shared by every copy of this package on the page, like the connection state.
import { reportBridgeError } from "./transport.js";

/** `appearance/1`: the desktop's appearance that the host pushes to its pages. */
export const appearanceFeature = "appearance/1";

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
 * The page's appearance. `colorScheme` is the engine's `prefers-color-scheme`, which follows a window's own scheme, or
 * the host's without it. `contrast` and `reducedMotion` are the stronger of the engine's media query and the host's
 * value, since an engine can parse a media feature without following the desktop. `textScale`, `accentColor` and
 * `systemColors` come from the host alone. `null` is a preference neither source knows, as under server rendering.
 */
export interface Appearance {
  readonly colorScheme: AppearanceColorScheme | null;
  readonly contrast: AppearanceContrast | null;
  readonly reducedMotion: boolean | null;
  readonly textScale: number | null;
  readonly accentColor: string | null;
  readonly systemColors: AppearanceSystemColors | null;
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

interface AppearanceChannel {
  host: HostAppearance | null;
  current: Appearance | undefined;
  readonly listeners: Set<(appearance: Appearance) => void>;
  // The media queries while someone listens, so their changes reach the listeners.
  queries?: { readonly lists: ReadonlyMap<QueryText, MediaQuery>; readonly stop: () => void } | undefined;
}

const channelKey = Symbol.for("runic.views.appearance");

function channel(): AppearanceChannel {
  const host = globalThis as unknown as Record<symbol, AppearanceChannel | undefined>;
  return host[channelKey] ??= { host: null, current: undefined, listeners: new Set() };
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

const contrastRank = { "no-preference": 0, more: 1, "forced-colors": 2 } as const;

function stronger(first: AppearanceContrast | null, second: AppearanceContrast | null): AppearanceContrast | null {
  if (first === null) return second;
  if (second === null) return first;
  return contrastRank[second] > contrastRank[first] ? second : first;
}

function compute(state: AppearanceChannel): Appearance {
  const lists = state.queries?.lists;
  const match = lists ? undefined : matchMediaOf();
  const read = (query: QueryText): boolean | undefined => {
    if (lists) return lists.get(query)?.matches;
    try { return match?.(query).matches; } catch { return undefined; }
  };
  const page = engine(read);
  const host = state.host;
  const reducedMotion = page.reducedMotion === null ? host?.reducedMotion ?? null
    : page.reducedMotion || host?.reducedMotion === true;
  const next: Appearance = {
    colorScheme: page.colorScheme ?? host?.colorScheme ?? null,
    contrast: stronger(page.contrast, host?.contrast ?? null),
    reducedMotion,
    textScale: host?.textScale ?? null,
    accentColor: host?.accentColor ?? null,
    systemColors: host?.systemColors ?? null,
    host,
  };
  const current = state.current;
  // The same object while nothing changed, for framework snapshots.
  if (current && current.colorScheme === next.colorScheme && current.contrast === next.contrast
    && current.reducedMotion === next.reducedMotion && current.host === next.host) return current;
  return state.current = Object.freeze(next);
}

function notify(state: AppearanceChannel): void {
  const previous = state.current;
  const next = compute(state);
  if (next === previous) return;
  for (const listener of [...state.listeners]) {
    try { listener(next); } catch (error) { reportBridgeError(error); }
  }
}

function watchQueries(state: AppearanceChannel): void {
  const match = matchMediaOf();
  if (!match || state.queries) return;
  const lists = new Map<QueryText, MediaQuery>();
  const changed = () => notify(state);
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
  colorScheme: null, contrast: null, reducedMotion: null, textScale: null, accentColor: null, systemColors: null, host: null,
});

/**
 * The page's appearance now: the colour scheme, contrast and reduced motion of the page's engine combined with the
 * desktop's appearance that a Runic host pushes (`appearance/1`, `RunicDesktopHost` in .NET), which also carries the
 * text scale, the accent colour and the forced-colours palette. The same frozen object until something changes.
 * Under server rendering every member is `null`.
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
  state.listeners.add(listener);
  watchQueries(state);
  let stopped = false;
  return () => {
    if (stopped) return;
    stopped = true;
    state.listeners.delete(listener);
    if (state.listeners.size === 0 && state.queries) {
      state.queries.stop();
      state.queries = undefined;
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

/**
 * The page runtime reports the host's appearance that the producer pushed (not exported from the package). Listeners hear only changes. A
 * new connection keeps the last push until the host pushes again.
 */
export function reportHostAppearance(host: HostAppearance | null): void {
  const state = channel();
  if (sameHost(state.host, host)) return;
  state.host = host;
  notify(state);
}
