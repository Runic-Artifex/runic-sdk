// navigation-path/1 on the page (specs/application/README.md, "Navigation paths"): the window's Back, and the route path
// of the window's path region kept in step with the page's URL and history.
import { BridgeError } from "./errors.js";
import { navigationPathFeature } from "./navigation-feature.js";
import { replyError, type Push } from "./protocol.js";
import { regionOutcome, type RegionOutcome } from "./regions.js";
import { sharedRuntimeFor, type SharedRuntime } from "./runtime.js";
import { reportBridgeError, waitForBridgeIn } from "./transport.js";
import { currentSlots, runInSlots, type Slots } from "./scope.js";

export { navigationPathFeature };

/** How the page changes its history for a new route path: a new entry, back to the previous one, or in place. */
export type NavigationPathOperation = "push" | "back" | "replace";

/** The reply to {@link navigateBack} and {@link openNavigationPath}. */
export interface NavigationReply {
  /** The request's .NET `NavigationOutcome`, or `null` when no route matched the opened path and nothing navigated. */
  readonly outcome: RegionOutcome | null;
  /**
   * The window's route path after the request: what the URL shows, which the page reverts to when the request did not
   * commit. `null` for an empty path region, `undefined` when the window has no path region.
   */
  readonly path?: string | null;
}

/** A change of the window's route path. */
export interface NavigationPathChange {
  readonly path: string | null;
  readonly operation: NavigationPathOperation;
}

async function greetedRuntime(slots: Slots, signal?: AbortSignal): Promise<SharedRuntime> {
  const runtime = sharedRuntimeFor(slots, await waitForBridgeIn(slots, signal ? { signal } : {}));
  await runtime.whenGreeted?.();
  if (runtime.features?.has(navigationPathFeature) !== true)
    throw new BridgeError("failed", "The .NET host does not implement navigation-path/1. Use the Runic packages of one release.");
  return runtime;
}

// A request of the current instance's window.
function navigation(message: Record<string, unknown>): Promise<NavigationReply> {
  return navigationIn(currentSlots(), message);
}

async function navigationIn(slots: Slots, message: Record<string, unknown>): Promise<NavigationReply> {
  const runtime = await greetedRuntime(slots);
  const reply = await runtime.request({ kind: "navigation", ...message });
  if (!reply.ok) throw replyError(reply.error, undefined, "The navigation request failed.");
  const path = reply["path"];
  return {
    outcome: reply["outcome"] === null ? null : regionOutcome(reply["outcome"]),
    ...(path === undefined ? {} : { path: path as string | null }),
  };
}

/**
 * Goes back in the window as its Back button does: the .NET `RunicNavigator.BackAsync`, in the innermost presented region
 * that can go back, through its departure guards. A guard's refusal resolves with a `Rejected` outcome; a host without
 * `navigation-path/1` rejects with a `failed` BridgeError.
 */
export function navigateBack(): Promise<NavigationReply> {
  return navigation({ action: "back" });
}

/**
 * Opens a route path, such as `customers/42`, in the window's path region, as a browser's Back, Forward or address bar
 * does: back to the page with that route when the region's history has one, otherwise a push, through the departure
 * guards. Resolves with `outcome: null` when no route matches.
 */
export function openNavigationPath(path: string): Promise<NavigationReply> {
  return navigation({ action: "open", path });
}

/**
 * Calls `listener` with the window's route path: once the connection's `hello` reports it (with `operation: "replace"`),
 * and after each change of the path region. Nothing is called for a window without a path region. Returns a function
 * that stops listening.
 */
export function onNavigationPath(listener: (change: NavigationPathChange) => void): () => void {
  const slots = currentSlots();
  const lifetime = new AbortController();
  const stops: (() => void)[] = [];
  const call = (change: NavigationPathChange) => {
    try { listener(change); } catch (error) { reportBridgeError(slots, error); }
  };
  void (async () => {
    let runtime: SharedRuntime;
    try { runtime = await greetedRuntime(slots, lifetime.signal); }
    catch (error) {
      if (!lifetime.signal.aborted) reportBridgeError(slots, error);
      return;
    }
    if (lifetime.signal.aborted) return;
    const greeted = () => {
      const path = runtime.navigationPath;
      if (path !== undefined) call({ path, operation: "replace" });
    };
    stops.push(runtime.onWindowPush?.("navigation-path", (push: Push) => {
      call({ path: push["path"] as string | null, operation: push["operation"] as NavigationPathOperation });
    }) ?? (() => {}));
    stops.push(runtime.onConnection(connected => {
      if (connected) void runtime.whenGreeted?.().then(greeted, () => undefined);
    }));
    greeted();
  })();
  return () => {
    lifetime.abort();
    for (const stop of stops.splice(0)) stop();
  };
}

/** The parts of the page's `window` that the navigation installs use; tests pass a stand-in. */
export interface NavigationWindow {
  readonly location: { readonly hash: string };
  readonly history: {
    readonly state: unknown;
    pushState(data: unknown, unused: string, url?: string): void;
    replaceState(data: unknown, unused: string, url?: string): void;
    back(): void;
    go(delta: number): void;
  };
  addEventListener(type: string, listener: (event: never) => void, options?: { capture?: boolean }): void;
  removeEventListener(type: string, listener: (event: never) => void, options?: { capture?: boolean }): void;
}

interface BackKeyEvent {
  readonly key: string;
  readonly altKey: boolean;
  readonly ctrlKey: boolean;
  readonly metaKey: boolean;
  readonly shiftKey: boolean;
  readonly defaultPrevented: boolean;
  readonly target: unknown;
  preventDefault(): void;
}

interface BackMouseEvent {
  readonly button: number;
  readonly defaultPrevented: boolean;
  preventDefault(): void;
}

/** Options of {@link installBackNavigation}. */
export interface BackNavigationOptions {
  /** The page's window; `globalThis.window` by default. */
  readonly window?: NavigationWindow;
  /** Whether Cmd+[ goes back, as on macOS; detected from the browser by default. */
  readonly macOS?: boolean;
}

const editable = (target: unknown): boolean => {
  const element = target as { isContentEditable?: boolean; tagName?: string } | null;
  if (!element || typeof element !== "object") return false;
  if (element.isContentEditable) return true;
  return element.tagName === "INPUT" || element.tagName === "TEXTAREA" || element.tagName === "SELECT";
};

const detectMacOS = (): boolean => {
  const browser = (globalThis as { navigator?: { platform?: string; userAgentData?: { platform?: string } } }).navigator;
  return /mac/i.test(browser?.userAgentData?.platform ?? browser?.platform ?? "");
};

// The history Back of an active installNavigationPath: true when it went back in the page's history, which then opens
// the previous path through popstate.
let historyBack: (() => boolean) | undefined;

/**
 * Maps the mouse's Back button (button 3), Alt+Left, Cmd+[ on macOS and the keyboard's Browser Back key to the window's
 * Back, so they go back in its innermost presented region on Runic Desktop and CS-WebUI alike. While
 * {@link installNavigationPath} is active they go back in the page's history instead, whose `popstate` then opens the
 * previous path, so a Back never runs twice; at the first history entry they fall back to {@link navigateBack}.
 * The page's own handlers win: an event whose default was prevented is left alone, and Alt+Left and Cmd+[ are left to
 * text fields. Keys and mouse buttons inside an iframe go to the iframe's document and don't reach these handlers.
 * Returns a function that removes them.
 */
export function installBackNavigation(options: BackNavigationOptions = {}): () => void {
  const page = options.window ?? (globalThis as unknown as { window?: NavigationWindow }).window;
  if (!page) return () => {};
  const macOS = options.macOS ?? detectMacOS();
  const slots = currentSlots();
  const back = () => {
    if (historyBack?.()) return;
    void navigationIn(slots, { action: "back" }).catch(error => reportBridgeError(slots, error));
  };
  const onKey = (event: BackKeyEvent) => {
    if (event.defaultPrevented) return;
    const altLeft = event.key === "ArrowLeft" && event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey;
    const commandBracket = macOS && event.key === "[" && event.metaKey && !event.altKey && !event.ctrlKey && !event.shiftKey;
    if (event.key !== "BrowserBack" && !((altLeft || commandBracket) && !editable(event.target))) return;
    event.preventDefault();
    back();
  };
  // The browser goes back on the release of button 3; preventing the press and the release keeps it from doing so.
  const onMouseDown = (event: BackMouseEvent) => { if (event.button === 3 && !event.defaultPrevented) event.preventDefault(); };
  const onMouseUp = (event: BackMouseEvent) => {
    if (event.button !== 3 || event.defaultPrevented) return;
    event.preventDefault();
    back();
  };
  page.addEventListener("keydown", onKey);
  page.addEventListener("mousedown", onMouseDown);
  page.addEventListener("mouseup", onMouseUp);
  return () => {
    page.removeEventListener("keydown", onKey);
    page.removeEventListener("mousedown", onMouseDown);
    page.removeEventListener("mouseup", onMouseUp);
  };
}

/** Options of {@link installNavigationPath}. */
export interface NavigationPathOptions {
  /** The page's window; `globalThis.window` by default. */
  readonly window?: NavigationWindow;
  /**
   * What precedes the route path in the URL's fragment: `"#/"` by default, so `customers/42` is `#/customers/42`. A
   * fragment that doesn't start with it is not a route path and is left alone.
   */
  readonly prefix?: string;
}

const stateKey = "runicNavigation";

// What this install keeps in history.state: the entry's index and the path it shows, so a reload keeps both.
interface HistoryRecord {
  readonly index: number;
  readonly path: string | null;
}

const recordOf = (state: unknown): HistoryRecord | undefined => {
  const value = (state as Record<string, unknown> | null)?.[stateKey] as Record<string, unknown> | undefined;
  if (!value || typeof value !== "object") return undefined;
  const { index, path } = value;
  return typeof index === "number" && Number.isSafeInteger(index) && index >= 0 && (path === null || typeof path === "string")
    ? { index, path } : undefined;
};

/**
 * Keeps the URL's fragment and the browser's history in step with the window's route path (`navigation-path/1`):
 *
 * - A change of the path region pushes a history entry, goes back to the previous one, or replaces the current one.
 *   Each entry keeps its index and path in `history.state`, so a reload keeps them.
 * - The browser's Back and Forward and an edited address bar open the URL's route path with {@link openNavigationPath},
 *   through the departure guards. Going back to an entry without a route path, such as a home page without a route,
 *   sends the window's Back instead. When the request doesn't commit, the URL goes back to the window's path: the history
 *   moves back to the entry that shows it, or the current entry is replaced. Only the latest of several quick moves
 *   reverts.
 * - A URL with a route path when the page loads opens it, as a deep link, once the window reports its path. A reconnect
 *   of the same page shows the window's path instead.
 *
 * Install it once per page. The browser's own Back then goes through the history, so on CS-WebUI the Back button, the
 * mouse's Back button and Alt+Left all work, and {@link installBackNavigation} goes back in the history too. Don't install
 * it next to another router that owns the fragment, such as SvelteKit's hash router. Returns a function that stops it.
 */
export function installNavigationPath(options: NavigationPathOptions = {}): () => void {
  const page = options.window ?? (globalThis as unknown as { window?: NavigationWindow }).window;
  if (!page) return () => {};
  const prefix = options.prefix ?? "#/";
  const slots = currentSlots();
  const report = (error: unknown) => reportBridgeError(slots, error);
  // The path each entry shows, by index, as far as this page knows.
  const paths = new Map<number, string | null>();
  const loaded = recordOf(page.history.state);
  let index = loaded?.index ?? 0;
  if (loaded) paths.set(index, loaded.path);
  // The index of the entry that shows the window's path, as the window last confirmed it.
  let confirmed = index;
  // The index a history move of this install (back or go) lands on; its popstate is not answered.
  let expected: number | undefined;
  // Each user move gets a number; only the latest one's reply may revert the URL.
  let moves = 0;
  let disposed = false;

  const url = (path: string | null) => path === null ? prefix : prefix + path;
  const current = (): string | null | undefined => {
    const hash = page.location.hash;
    if (!hash.startsWith(prefix)) return hash === "" || hash === "#" ? null : undefined;
    const path = hash.slice(prefix.length);
    return path === "" ? null : path;
  };
  const state = (at: number, path: string | null) => ({ ...(page.history.state as object | null ?? {}), [stateKey]: { index: at, path } });
  const replace = (path: string | null) => {
    page.history.replaceState(state(index, path), "", url(path));
    paths.set(index, path);
    confirmed = index;
  };
  const push = (path: string | null) => {
    index += 1;
    // pushState drops the entries above the current one; so are their records.
    for (const at of [...paths.keys()]) if (at >= index) paths.delete(at);
    page.history.pushState(state(index, path), "", url(path));
    paths.set(index, path);
    confirmed = index;
  };
  const go = (to: number) => {
    if (to === index) return;
    expected = to;
    page.history.go(to - index);
    index = to;
  };

  // A change of the window's path region.
  const apply = (change: NavigationPathChange) => {
    if (disposed) return;
    if (current() === change.path) return replace(change.path);
    if (change.operation === "push") push(change.path);
    else if (change.operation === "back" && index > 0 && paths.has(index - 1) && paths.get(index - 1) === change.path) {
      go(index - 1);
      confirmed = index;
    }
    else replace(change.path);
  };

  // A Back, Forward or address bar edit: open the URL's path, or go back for an entry without one; revert the URL when
  // that doesn't commit.
  const onPop = (event: { readonly state: unknown }) => {
    if (disposed) return;
    const record = recordOf(event.state);
    if (expected !== undefined) {
      const target = expected;
      expected = undefined;
      if (record?.index === target) return;
    }
    const left = index;
    const path = record ? record.path : current();
    if (path === undefined) return; // a fragment that is not a route path, such as an in-page anchor
    if (record) index = record.index;
    else {
      // An edited address bar: a new entry above the one left, without our record.
      index = left + 1;
      for (const at of [...paths.keys()]) if (at >= index) paths.delete(at);
      page.history.replaceState(state(index, path), "", url(path));
    }
    paths.set(index, path);
    const move = ++moves;
    const settle = (reply: NavigationReply) => {
      if (disposed || move !== moves || reply.path === undefined) return;
      if (reply.path === path) {
        confirmed = index;
        return;
      }
      // The region kept another page: show its path again, at the entry that shows it when there is one.
      if (paths.get(confirmed) === reply.path) go(confirmed);
      else replace(reply.path);
    };
    // An entry without a route path, such as a home page without a route: the window goes back to it.
    const request = navigationIn(slots, path === null ? { action: "back" } : { action: "open", path });
    void request.then(settle, report);
  };

  // The window reports its path: when the connection starts, or when its path region becomes available later. The first
  // report of this page is where a load-time route path opens as a deep link; later ones show the window's path.
  let first = true;
  const linked = current();
  const reported = (change: NavigationPathChange) => {
    if (!first || change.operation !== "replace") return apply(change);
    first = false;
    if (loaded || linked === undefined || linked === null || linked === change.path) return replace(change.path);
    paths.set(index, linked);
    void navigationIn(slots, { action: "open", path: linked }).then(reply => {
      if (disposed || reply.path === undefined) return;
      if (reply.path !== linked) replace(reply.path);
      else confirmed = index;
    }, report);
  };

  page.addEventListener("popstate", onPop);
  const stop = onNavigationPath(reported);
  const back = () => {
    if (disposed || index === 0) return false;
    page.history.back();
    return true;
  };
  historyBack = back;
  return () => {
    disposed = true;
    if (historyBack === back) historyBack = undefined;
    stop();
    page.removeEventListener("popstate", onPop);
  };
}
