/// <reference lib="esnext.disposable" preserve="true" />
// The in-page menu bar (Runic Desktop's DesktopMenuBarPresentation.Page): the declared menu bar as headless state for
// the application's own menubar component, and `invoke` through the native bar's command path. The wire is menu bar
// page protocol 2 (specs/desktop/menu-bar-page.md): requests to the reserved route `runic.menu-bar` on the Views
// channel and targeted `menu-bar` pushes. A host without it leaves the controller `unavailable`.
import { delay } from "./clock.js";
import { BridgeError } from "./errors.js";
import { disposable } from "./lifetime.js";
import { isObject, type Message } from "./protocol.js";
import { sharedRuntimeFor, type SharedRuntime } from "./runtime.js";
import { isApplePlatform, matchesShortcut, type ShortcutEvent } from "./shortcuts.js";
import { reportBridgeError, waitForBridge } from "./transport.js";

/** Options of {@link createMenuBarController}. */
export interface MenuBarControllerOptions {
  /**
   * Who matches the items' shortcuts. This set is closed.
   * - `"runic"`, the default: the host matches them on its own key path, or this controller in the page where the
   *   host has none, after the page saw the key and did not call `preventDefault()`.
   * - `"app"`: the page matches them itself, for example with {@link MenuBarController.handleKeyDown}. While the bar
   *   is in the page (`placement` `"page"`), the host stops matching them for this page until the controller is disposed.
   */
  readonly shortcuts?: "runic" | "app";
  /**
   * Emits `focus` and `open` requests for Alt, F10 and Alt plus a menu's access key on Windows and Linux while the bar
   * is in the page, and sets `accessKeyHints`. Defaults to `true`.
   */
  readonly keyboard?: boolean;
  /** Disposes the controller when it aborts. */
  readonly signal?: AbortSignal;
}

/** The menu bar as the page renders it. Each change is a new object. */
export interface MenuBarState {
  /**
   * `connecting` until the host answered; `ready` with the host's bar; `unavailable` when the host has no in-page menu
   * bar (an older host, CS-WebUI, a released window or no Bridge); `failed` with `error` otherwise.
   * This set is open: a later version may add values.
   */
  readonly status: "connecting" | "ready" | "unavailable" | "failed";
  /**
   * Where the bar is shown now; render a bar only for `"page"`. `"window"` and `"global"` are native bars (macOS keeps
   * its application menu bar, `"global"`), and `"none"` shows no bar. This set is open: a later version may add values.
   */
  readonly placement: "page" | "window" | "global" | "none";
  /**
   * Who matches the items' shortcuts now: `"host"` on its native key path, `"page"` this package in the page, `"app"`
   * the application, under the `shortcuts: "app"` option. This set is open: a later version may add values.
   */
  readonly shortcuts: "host" | "page" | "app";
  /** Alt is held, or a keyboard request is being handled: underline the access keys ({@link splitAccessKey}). */
  readonly accessKeyHints: boolean;
  /** Increases with each change of the host's state. */
  readonly revision: number;
  /** The menus in the platform's order: on Windows and Linux File first and Help last. */
  readonly menus: readonly MenuBarMenu[];
  /** Why the status is `unavailable` or `failed`. */
  readonly error?: BridgeError;
}

/** An entry of a menu. This set is open: entries of kinds a later host adds are left out. */
export type MenuBarEntry = MenuBarMenu | MenuBarItem | MenuBarSeparator;

/** A menu of the bar, or a submenu. */
export interface MenuBarMenu {
  readonly kind: "menu";
  /** Stable while the bar's declaration keeps the entry; use it as the component key. */
  readonly key: string;
  /** The visible title; it ends with the access key in parentheses when the title does not contain it, as `Help (H)`. */
  readonly label: string;
  /** The access key, upper case, when the menu has one. */
  readonly accessKey?: string;
  /** The access key's UTF-16 index in `label`, or -1. */
  readonly accessKeyIndex: number;
  /** The declared role. This set is open: a later version may add values; render unknown ones as `"none"`. */
  readonly role: "none" | "application" | "file" | "help";
  /** The declaration's `IsEnabled`, with its parents'. */
  readonly enabled: boolean;
  readonly entries: readonly MenuBarEntry[];
}

/** A menu item. */
export interface MenuBarItem {
  readonly kind: "item";
  /** Stable while the bar's declaration keeps the entry; pass the item to {@link MenuBarController.invoke}. */
  readonly key: string;
  readonly label: string;
  /** The access key, upper case, when the item has one. */
  readonly accessKey?: string;
  /** The access key's UTF-16 index in `label`, or -1. */
  readonly accessKeyIndex: number;
  readonly commandId: string;
  readonly commandParameter?: string;
  /** The declaration's `IsEnabled`, its menus' and its command's `CanExecute`. */
  readonly enabled: boolean;
  /** A check or radio item's state; absent for a plain item. */
  readonly checked?: boolean;
  /** The radio group of a radio item. */
  readonly radioGroup?: string;
  /** The shortcut as `parseShortcut` reads it, such as `Primary+Shift+S`; reserved shortcuts are left out. */
  readonly shortcut?: string;
  /** The shortcut as the platform shows it: `Ctrl+Shift+S` on Windows, `Shift+Ctrl+S` on Linux, `⇧⌘S` on macOS. */
  readonly shortcutText?: string;
  /** The `aria-keyshortcuts` value, such as `Control+Shift+S`. */
  readonly ariaKeyShortcuts?: string;
}

/** A separator line. */
export interface MenuBarSeparator {
  readonly kind: "separator";
  readonly key: string;
}

/**
 * A keyboard request for the page's menubar component: `focus` its first menu (an Alt tap or F10) or `open` a menu
 * (Alt plus the menu's access key). This set is open: ignore kinds a later version adds.
 */
export type MenuBarRequest = { readonly kind: "focus" } | { readonly kind: "open"; readonly menu: MenuBarMenu };

/**
 * What {@link MenuBarController.invoke} did: `executed` the command ran; `disabled` it cannot run now; `stale` the
 * bar changed since the item was rendered. This set is open: treat other values as `disabled`.
 */
export type MenuBarInvokeResult = "executed" | "disabled" | "stale";

/** The headless in-page menu bar. Framework adapters wrap it in their reactive state. */
export interface MenuBarController {
  /** The current state. */
  readonly current: MenuBarState;
  /** Calls `listener` after each change of `current`, until the returned function is called. */
  subscribe(listener: () => void): () => void;
  /** Calls `listener` with each keyboard request, until the returned function is called. */
  onRequest(listener: (request: MenuBarRequest) => void): () => void;
  /**
   * Runs an item as the native menu bar does: the window's command, then the application's, if it can run now.
   * Rejects with a BridgeError when the host cannot be reached.
   */
  invoke(item: MenuBarItem): Promise<MenuBarInvokeResult>;
  /**
   * Matches a `keydown` event against the enabled items' shortcuts. On a match it calls `preventDefault()`, invokes
   * the item and returns `true`. Events that are `defaultPrevented` or part of a composition never match. For the
   * `shortcuts: "app"` option.
   */
  handleKeyDown(event: ShortcutEvent & { readonly defaultPrevented?: boolean; preventDefault(): void }): boolean;
  /** Stops following the host, ends the `"app"` lease and removes the keyboard listeners. */
  dispose(): void;
}

/**
 * Splits an entry's label around its access key, to underline the key while `accessKeyHints` is true.
 * Without an access key the whole label is `before`.
 */
export function splitAccessKey(entry: MenuBarMenu | MenuBarItem): readonly [before: string, key: string, after: string] {
  const { label, accessKeyIndex: index } = entry;
  if (!Number.isInteger(index) || index < 0 || index >= label.length) return [label, "", ""];
  const end = index + ((label.codePointAt(index) ?? 0) > 0xffff ? 2 : 1);
  return [label.slice(0, index), label.slice(index, end), label.slice(end)];
}

const menuBarRoute = "runic.menu-bar";
const placements: readonly string[] = ["page", "window", "global", "none"];
const owners: readonly string[] = ["host", "page", "app"];
const roles: readonly string[] = ["none", "application", "file", "help"];

type KeyboardTarget = {
  addEventListener(type: string, listener: (event: never) => void, options?: boolean): void;
  removeEventListener(type: string, listener: (event: never) => void, options?: boolean): void;
};
type KeyEvent = ShortcutEvent & { readonly defaultPrevented?: boolean; readonly repeat?: boolean; preventDefault(): void };

const freeze = <T extends object>(value: T): T => Object.freeze(value);

function unavailable(message: string): BridgeError {
  return new BridgeError("unavailable", message);
}

const text = (value: unknown): value is string => typeof value === "string";

function decodeEntries(value: unknown, revision: number, revisions: WeakMap<object, number>): MenuBarEntry[] {
  if (!Array.isArray(value)) return [];
  const entries: MenuBarEntry[] = [];
  for (const raw of value as unknown[]) {
    if (raw === null || typeof raw !== "object") continue;
    const entry = raw as Record<string, unknown>;
    if (!text(entry["key"])) continue;
    if (entry["kind"] === "separator") {
      entries.push(freeze({ kind: "separator", key: entry["key"] }));
      continue;
    }
    if (entry["kind"] !== "menu" && entry["kind"] !== "item") continue;
    if (!text(entry["label"])) continue;
    const index = typeof entry["accessKeyIndex"] === "number" && Number.isInteger(entry["accessKeyIndex"]) ? entry["accessKeyIndex"] : -1;
    const accessKey = text(entry["accessKey"]) ? { accessKey: entry["accessKey"] } : {};
    if (entry["kind"] === "menu") {
      const role = text(entry["role"]) && roles.includes(entry["role"]) ? entry["role"] as MenuBarMenu["role"] : "none";
      entries.push(freeze({
        kind: "menu", key: entry["key"], label: entry["label"], ...accessKey, accessKeyIndex: index, role,
        enabled: entry["enabled"] !== false, entries: freeze(decodeEntries(entry["entries"], revision, revisions)),
      }));
      continue;
    }
    if (!text(entry["commandId"])) continue;
    const optional: Record<string, unknown> = {};
    for (const name of ["commandParameter", "radioGroup", "shortcut", "shortcutText", "ariaKeyShortcuts"])
      if (text(entry[name])) optional[name] = entry[name];
    if (typeof entry["checked"] === "boolean") optional["checked"] = entry["checked"];
    const item = freeze({
      kind: "item", key: entry["key"], label: entry["label"], ...accessKey, accessKeyIndex: index, commandId: entry["commandId"],
      enabled: entry["enabled"] === true, ...optional,
    }) as MenuBarItem;
    revisions.set(item, revision);
    entries.push(item);
  }
  return entries;
}

function* items(entries: readonly MenuBarEntry[]): Generator<MenuBarItem> {
  for (const entry of entries) {
    if (entry.kind === "item") yield entry;
    else if (entry.kind === "menu" && entry.enabled) yield* items(entry.entries);
  }
}

/**
 * Follows the host's in-page menu bar: the declared `DesktopHost.MenuBar` with the commands' availability, for the
 * application's own menubar component. Render a bar while `current.placement` is `"page"`, run a chosen item with
 * `invoke`, and map `onRequest` to the component (focus its first trigger, or open a menu). The controller keeps
 * following the host across reconnects until it is disposed; a `using` declaration disposes it where the engine has
 * `Symbol.dispose`.
 */
export function createMenuBarController(options: MenuBarControllerOptions = {}): MenuBarController & Disposable {
  const app = options.shortcuts === "app";
  const keyboard = options.keyboard ?? true;
  const lifetime = new AbortController();
  const listeners = new Set<() => void>();
  const requestListeners = new Set<(request: MenuBarRequest) => void>();
  const revisions = new WeakMap<object, number>();
  let state: MenuBarState = freeze({ status: "connecting", placement: "none", shortcuts: app ? "app" : "host", accessKeyHints: false,
    revision: 0, menus: freeze([]) });
  let runtime: SharedRuntime | undefined;
  let platform: string | undefined;
  let leased = false;
  let disposed = false;
  let watching = false;
  const stops: (() => void)[] = [];
  let altDown = false;
  let altAlone = false;
  let stickyHints = false;

  const set = (patch: Partial<MenuBarState>, clearError = false) => {
    if (disposed) return;
    const next: Record<string, unknown> = { ...state, ...patch };
    if (clearError) delete next["error"];
    state = freeze(next as unknown as MenuBarState);
    for (const listener of [...listeners]) {
      try { listener(); } catch (error) { reportBridgeError(error); }
    }
  };
  const stop = (status: "unavailable" | "failed", error: BridgeError) => {
    set({ status, error });
    lifetime.abort();
    for (const end of stops.splice(0)) end();
  };
  // The host names its platform; before it answered, the page's own.
  const apple = () => platform === undefined ? isApplePlatform() : platform === "macos";

  const apply = (value: unknown): boolean => {
    if (!isObject(value) || typeof value["revision"] !== "number") return false;
    if (typeof value["platform"] === "string") platform = value["platform"];
    const revision = value["revision"];
    // A state the controller already has, from a reply or a push, changes nothing.
    if (state.status === "ready" && revision <= state.revision) return true;
    const placement = text(value["placement"]) && placements.includes(value["placement"])
      ? value["placement"] as MenuBarState["placement"] : "none";
    const shortcuts = text(value["shortcuts"]) && owners.includes(value["shortcuts"])
      ? value["shortcuts"] as MenuBarState["shortcuts"] : "host";
    const menus = decodeEntries(value["menus"], revision, revisions).filter((entry): entry is MenuBarMenu => entry.kind === "menu");
    set({ status: "ready", placement, shortcuts, revision, menus: freeze(menus) }, true);
    return true;
  };

  const request = (message: Message) => {
    if (!runtime) return Promise.reject(new BridgeError("disconnected", "The Bridge is not connected.", { route: menuBarRoute }));
    return runtime.request({ ...message, route: menuBarRoute }, menuBarRoute);
  };

  // Watches the bar on the current connection; the "app" lease belongs to a connection, so it is taken on each.
  const watch = async () => {
    if (disposed || watching || !runtime?.connected) return;
    for (let wait = 25; !disposed; wait = Math.min(wait * 2, 2000)) {
      watching = true;
      try {
        // The "app" lease is taken first, so the state never shows the host matching the shortcuts.
        if (app && !leased) {
          const lease = await request({ kind: "menu-bar-shortcuts", shortcuts: "app" });
          if (disposed) return;
          if (lease.ok) { leased = true; apply(lease["state"]); }
        }
        const watched = await request({ kind: "menu-bar-watch" });
        if (disposed) return;
        if (!watched.ok) {
          if (watched.error?.reason === "invalid-request")
            stop("failed", new BridgeError("failed", "The host rejected the menu bar request.", { route: menuBarRoute }));
          else stop("unavailable", unavailable(watched.error?.kind === "released" && runtime.connected
            ? "This host serves no in-page menu bar, or the window of this menu bar was released: it needs Runic Desktop with Runic.Application.Views.Desktop 0.13 or later."
            : "The menu bar is not available."));
          return;
        }
        if (!apply(watched["state"])) { stop("unavailable", unavailable("The host answered the menu bar with an invalid state.")); return; }
        return;
      } catch {
        // A lost connection watches again on the next one; another failure retries with backoff.
        if (disposed || !runtime.connected) return;
        const pause = delay(wait);
        lifetime.signal.addEventListener("abort", () => pause.cancel(), { once: true });
        await pause.promise;
      } finally {
        watching = false;
      }
    }
  };

  const run = async () => {
    let bridge;
    try { bridge = await waitForBridge({ signal: lifetime.signal }); }
    catch (error) {
      if (disposed) return;
      const failure = error instanceof BridgeError ? error : new BridgeError("failed", "The Bridge could not be reached.", { cause: error });
      stop(failure.kind === "unavailable" ? "unavailable" : "failed", failure);
      return;
    }
    if (disposed) return;
    runtime = sharedRuntimeFor(bridge);
    stops.push(runtime.onPush("menu-bar", menuBarRoute, push => { apply(push["state"]); }));
    stops.push(runtime.onConnection(connected => {
      if (!connected) { leased = false; set({ status: "connecting" }); return; }
      void watch();
    }));
    await watch();
  };

  const invoke = async (item: MenuBarItem): Promise<MenuBarInvokeResult> => {
    if (disposed) throw new BridgeError("released", "The menu bar controller was disposed.", { route: menuBarRoute });
    if (state.status !== "ready" || !runtime) throw new BridgeError(state.status === "connecting" ? "disconnected" : "unavailable",
      "The menu bar is not connected.", { route: menuBarRoute });
    if (stickyHints) { stickyHints = false; set({ accessKeyHints: false }); }
    let reply;
    try { reply = await request({ kind: "menu-bar-invoke", revision: revisions.get(item) ?? state.revision, key: item.key }); }
    catch (cause) {
      if (cause instanceof BridgeError && cause.kind === "disconnected") throw cause;
      throw new BridgeError(runtime.connected ? "failed" : "disconnected", "The menu item could not be invoked.", { cause, route: menuBarRoute });
    }
    if (!reply.ok && reply.error?.kind === "released") throw new BridgeError("released", "The window of this menu bar was released.", { route: menuBarRoute });
    const result = reply["result"];
    return result === "executed" || result === "disabled" || result === "stale" ? result : "disabled";
  };

  const match = (event: KeyEvent): MenuBarItem | undefined => {
    if (disposed || state.status !== "ready" || event.defaultPrevented === true || event.isComposing === true) return undefined;
    for (const item of items(state.menus)) {
      if (item.enabled && item.shortcut !== undefined && matchesShortcut(event, item.shortcut, { apple: apple() }))
        return item;
    }
    return undefined;
  };

  const handleKeyDown = (event: KeyEvent): boolean => {
    const item = match(event);
    if (!item) return false;
    event.preventDefault();
    invoke(item).catch(reportBridgeError);
    return true;
  };

  const emit = (request: MenuBarRequest) => {
    for (const listener of [...requestListeners]) {
      try { listener(request); } catch (error) { reportBridgeError(error); }
    }
  };
  const hints = (on: boolean) => { if (state.accessKeyHints !== on) set({ accessKeyHints: on }); };
  // macOS has no in-page equivalent of the Windows and Linux menu keys.
  const requests = () => keyboard && state.status === "ready" && state.placement === "page" && !apple();
  const accessKeyOf = (event: KeyEvent) => {
    const code = /^(?:Key([A-Z])|Digit([0-9]))$/.exec(event.code ?? "");
    return code ? code[1] ?? code[2] : event.key.length === 1 ? event.key.toUpperCase() : undefined;
  };

  const onKeyDown = (event: KeyEvent) => {
    if (state.shortcuts === "page" && !app && handleKeyDown(event)) return;
    if (!requests() || event.defaultPrevented === true || event.isComposing === true) return;
    if (event.key === "Alt") {
      if (!event.ctrlKey && !event.metaKey && !event.shiftKey && event.repeat !== true) { altDown = true; altAlone = true; hints(true); }
      return;
    }
    if (altDown) altAlone = false;
    if (event.key === "F10" && !event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey) {
      event.preventDefault();
      stickyHints = true;
      hints(true);
      emit(freeze({ kind: "focus" }));
      return;
    }
    if (event.altKey && !event.ctrlKey && !event.metaKey) {
      const key = accessKeyOf(event);
      const menu = key === undefined ? undefined : state.menus.find(candidate => candidate.enabled && candidate.accessKey?.toUpperCase() === key);
      if (menu) {
        event.preventDefault();
        stickyHints = true;
        hints(true);
        emit(freeze({ kind: "open", menu }));
      }
      return;
    }
    if (event.key === "Escape" && state.accessKeyHints && !altDown) { stickyHints = false; hints(false); }
  };
  const onKeyUp = (event: KeyEvent) => {
    if (event.key !== "Alt") return;
    const tap = altDown && altAlone;
    altDown = false;
    altAlone = false;
    if (tap && requests() && event.defaultPrevented !== true) {
      event.preventDefault();
      stickyHints = true;
      emit(freeze({ kind: "focus" }));
    }
    if (!stickyHints) hints(false);
  };
  const reset = () => {
    altDown = false;
    altAlone = false;
    if (!stickyHints && !state.accessKeyHints) return;
    stickyHints = false;
    hints(false);
  };
  const onPointerDown = () => { if (!altDown) reset(); };

  const target = (globalThis as { addEventListener?: unknown }).addEventListener === undefined ? undefined : globalThis as unknown as KeyboardTarget;
  const bindings: [string, (event: never) => void][] = [["keydown", onKeyDown], ["keyup", onKeyUp], ["blur", reset], ["pointerdown", onPointerDown]];
  for (const [type, listener] of target ? bindings : []) target!.addEventListener(type, listener, false);

  const dispose = () => {
    if (disposed) return;
    disposed = true;
    lifetime.abort();
    for (const end of stops.splice(0)) end();
    for (const [type, listener] of target ? bindings : []) target!.removeEventListener(type, listener, false);
    listeners.clear();
    requestListeners.clear();
    if (runtime?.connected) {
      const owner = runtime;
      if (leased) void owner.request({ kind: "menu-bar-shortcuts", route: menuBarRoute, shortcuts: "runic" }, menuBarRoute).catch(() => undefined);
      void owner.request({ kind: "menu-bar-unwatch", route: menuBarRoute }, menuBarRoute).catch(() => undefined);
    }
  };

  if (options.signal?.aborted) dispose();
  else {
    options.signal?.addEventListener("abort", dispose, { once: true });
    void run();
  }

  return disposable({
    get current() { return state; },
    subscribe(listener: () => void) {
      if (disposed) return () => {};
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    onRequest(listener: (request: MenuBarRequest) => void) {
      if (disposed) return () => {};
      requestListeners.add(listener);
      return () => { requestListeners.delete(listener); };
    },
    invoke,
    handleKeyDown,
    dispose,
  });
}
