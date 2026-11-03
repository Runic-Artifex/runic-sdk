// navigation-path/1 on the page (specs/application/README.md, "Navigation paths"): the window's Back, and the route path
// of the window's path region kept in step with the page's URL and history.
import { BridgeError } from "./errors.js";
import { navigationPathFeature } from "./navigation-feature.js";
import { replyError, type Push } from "./protocol.js";
import { regionOutcome, type RegionOutcome } from "./regions.js";
import { sharedRuntimeFor, type SharedRuntime } from "./runtime.js";
import { reportBridgeError, waitForBridge } from "./transport.js";

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

async function greetedRuntime(signal?: AbortSignal): Promise<SharedRuntime> {
  const runtime = sharedRuntimeFor(await waitForBridge(signal ? { signal } : {}));
  await runtime.whenGreeted?.();
  if (runtime.features?.has(navigationPathFeature) !== true)
    throw new BridgeError("failed", "The .NET host does not implement navigation-path/1. Use the Runic packages of one release.");
  return runtime;
}

async function navigation(message: Record<string, unknown>): Promise<NavigationReply> {
  const runtime = await greetedRuntime();
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
  const lifetime = new AbortController();
  const stops: (() => void)[] = [];
  const call = (change: NavigationPathChange) => {
    try { listener(change); } catch (error) { reportBridgeError(error); }
  };
  void (async () => {
    let runtime: SharedRuntime;
    try { runtime = await greetedRuntime(lifetime.signal); }
    catch (error) {
      if (!lifetime.signal.aborted) reportBridgeError(error);
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
}

const editable = (target: unknown): boolean => {
  const element = target as { isContentEditable?: boolean; tagName?: string } | null;
  if (!element || typeof element !== "object") return false;
  if (element.isContentEditable) return true;
  return element.tagName === "INPUT" || element.tagName === "TEXTAREA" || element.tagName === "SELECT";
};

/**
 * Maps the mouse's Back button (button 3) and Alt+Left, Cmd+[ and the keyboard's Browser Back key to
 * {@link navigateBack}, so they go back in the window's innermost region on Runic Desktop and CS-WebUI alike. The page's
 * own handlers win: an event whose default was prevented is left alone, and Alt+Left and Cmd+[ are left to text fields.
 * Returns a function that removes the handlers.
 */
export function installBackNavigation(options: BackNavigationOptions = {}): () => void {
  const page = options.window ?? (globalThis as unknown as { window?: NavigationWindow }).window;
  if (!page) return () => {};
  const back = () => { void navigateBack().catch(reportBridgeError); };
  const onKey = (event: BackKeyEvent) => {
    if (event.defaultPrevented) return;
    const altLeft = event.key === "ArrowLeft" && event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey;
    const commandBracket = event.key === "[" && event.metaKey && !event.altKey && !event.ctrlKey && !event.shiftKey;
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

/**
 * Keeps the URL's fragment and the browser's history in step with the window's route path (`navigation-path/1`):
 *
 * - A change of the path region pushes a history entry, goes back to the previous one, or replaces the current one.
 * - The browser's Back and Forward and an edited address bar open the URL's route path with {@link openNavigationPath},
 *   through the departure guards. When the request doesn't commit, the URL goes back to the window's path: the history
 *   moves back to the entry the user left, or the current entry is replaced.
 * - A URL with a route path when the page loads opens it, as a deep link.
 *
 * Install it once per page. The browser's own Back then goes through the history, so on CS-WebUI the Back button, the
 * mouse's Back button and Alt+Left all work. Don't install it next to another router that owns the fragment, such as
 * SvelteKit's hash router. Returns a function that stops it.
 */
export function installNavigationPath(options: NavigationPathOptions = {}): () => void {
  const page = options.window ?? (globalThis as unknown as { window?: NavigationWindow }).window;
  if (!page) return () => {};
  const prefix = options.prefix ?? "#/";
  // Our index of each history entry, kept in history.state, and the path each one shows.
  const paths = new Map<number, string | null>();
  const indexOf = (state: unknown): number | undefined => {
    const value = (state as Record<string, unknown> | null)?.[stateKey];
    return typeof value === "number" && Number.isSafeInteger(value) ? value : undefined;
  };
  let index = indexOf(page.history.state) ?? 0;
  let highest = index;
  // Pops this install caused (history.back and go), which it does not answer.
  let expectedPops = 0;
  let disposed = false;

  const url = (path: string | null) => path === null ? prefix : prefix + path;
  const current = (): string | null | undefined => {
    const hash = page.location.hash;
    if (!hash.startsWith(prefix)) return hash === "" || hash === "#" ? null : undefined;
    const path = hash.slice(prefix.length);
    return path === "" ? null : path;
  };
  const state = (at: number) => ({ ...(page.history.state as object | null ?? {}), [stateKey]: at });
  const replace = (path: string | null) => {
    page.history.replaceState(state(index), "", url(path));
    paths.set(index, path);
  };
  const push = (path: string | null) => {
    index = ++highest;
    page.history.pushState(state(index), "", url(path));
    paths.set(index, path);
  };

  // A change of the window's path region.
  const apply = (change: NavigationPathChange) => {
    if (disposed) return;
    if (current() === change.path) {
      paths.set(index, change.path);
      return;
    }
    if (change.operation === "push") push(change.path);
    else if (change.operation === "back" && paths.has(index - 1) && paths.get(index - 1) === change.path) {
      expectedPops++;
      page.history.back();
    }
    else replace(change.path);
  };

  // A Back, Forward or address bar edit: open the URL's path; revert the URL when that doesn't commit.
  const onPop = (event: { readonly state: unknown }) => {
    if (disposed) return;
    const left = index;
    const at = indexOf(event.state);
    if (expectedPops > 0) {
      expectedPops--;
      if (at !== undefined) index = at;
      return;
    }
    const path = current();
    if (path === undefined) return; // a fragment that is not a route path, such as an in-page anchor
    if (at === undefined) {
      // An edited address bar: a new entry without our state.
      index = ++highest;
      page.history.replaceState(state(index), "", url(path));
    }
    else index = at;
    paths.set(index, path);
    if (path === null) return;
    void openNavigationPath(path).then(reply => {
      if (disposed || reply.path === undefined || reply.path === path) return;
      // The region stayed: show its path again, by moving back to the entry the user left when it is ours.
      if (at !== undefined && paths.get(left) === reply.path && index === at) {
        expectedPops++;
        page.history.go(left - at);
        index = left;
      }
      else replace(reply.path);
    }, reportBridgeError);
  };

  // The page loaded or reconnected: a route path in the URL is a deep link; otherwise the URL shows the window's path.
  let first = true;
  const greeted = (change: NavigationPathChange) => {
    if (!first || change.operation !== "replace") return apply(change);
    first = false;
    const linked = current();
    if (linked === undefined || linked === null || linked === change.path) return replace(change.path);
    paths.set(index, linked);
    void openNavigationPath(linked).then(reply => {
      if (!disposed && reply.path !== undefined && reply.path !== linked) replace(reply.path);
    }, reportBridgeError);
  };

  page.addEventListener("popstate", onPop);
  const stop = onNavigationPath(greeted);
  return () => {
    disposed = true;
    stop();
    page.removeEventListener("popstate", onPop);
  };
}
