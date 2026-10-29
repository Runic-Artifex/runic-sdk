// `installMockMenuBar`: the host side of the in-page menu bar routes (specs/desktop/menu-bar-page.md) on a mock Bridge,
// so a page's menubar component and its createMenuBarController can be tested without .NET.
import type { MenuBarEntry, MenuBarInvokeResult, MenuBarItem, MenuBarMenu } from "./menu-bar.js";
import { createMockBridge, installMockBridge, type MockBridge } from "./mock-bridge.js";
import { isApplePlatform } from "./shortcuts.js";
import type { RunicBridgeClient } from "./transport.js";

const menuBarRoute = "runic.menu-bar";

/** Options of {@link installMockMenuBar}. */
export interface MockMenuBarOptions {
  /** The mock Bridge to serve the menu bar on; defaults to the installed mock Bridge, or a newly installed one. */
  readonly bridge?: MockBridge;
  /** Where the host shows the bar; defaults to `"page"`. This set is open, as `MenuBarState.placement`. */
  readonly placement?: "page" | "window" | "global" | "none";
  /**
   * Whether the host matches the items' shortcuts on its own key path (`"host"`) or leaves them to the page
   * (`"page"`, the default, as for a page in a browser). This set is closed.
   */
  readonly shortcuts?: "host" | "page";
  /** The host's platform; defaults to `"macos"` on Apple platforms and `"linux"` elsewhere. This set is closed. */
  readonly platform?: "windows" | "linux" | "macos";
  /**
   * Runs an enabled item the page invoked; its result is the reply, `"executed"` when it returns nothing. Every
   * invocation is recorded in {@link MockMenuBar.invocations} first.
   */
  readonly onInvoke?: (item: MenuBarItem) => MenuBarInvokeResult | void;
}

/** The host side of a mock in-page menu bar. */
export interface MockMenuBar {
  /** The mock Bridge that serves the menu bar. */
  readonly bridge: MockBridge;
  /** The host's revision; each change increases it. */
  readonly revision: number;
  readonly menus: readonly MenuBarMenu[];
  readonly placement: "page" | "window" | "global" | "none";
  readonly shortcuts: "host" | "page";
  /** A controller holds the `shortcuts: "app"` lease. */
  readonly leased: boolean;
  /** The enabled items the page invoked, in order. */
  readonly invocations: readonly MenuBarItem[];
  /**
   * Changes the host's state, as `SetMenuBar`, a command's `CanExecuteChanged` or a placement change do, and pushes
   * it to a watching controller. New `menus` make items of earlier revisions `stale`.
   */
  update(change: { readonly menus?: readonly MenuBarMenu[]; readonly placement?: MockMenuBar["placement"]; readonly shortcuts?: "host" | "page" }): void;
}

const isMockBridge = (value: unknown): value is MockBridge =>
  typeof (value as { intercept?: unknown } | undefined)?.intercept === "function" && typeof (value as { view?: unknown }).view === "function";

function find(entries: readonly MenuBarEntry[], key: string, enabled = true): { item: MenuBarItem; enabled: boolean } | undefined {
  for (const entry of entries) {
    if (entry.kind === "item" && entry.key === key) return { item: entry, enabled: enabled && entry.enabled };
    if (entry.kind === "menu") {
      const found = find(entry.entries, key, enabled && entry.enabled);
      if (found) return found;
    }
  }
  return undefined;
}

/**
 * Serves the in-page menu bar (menu bar page protocol 2) with `menus` on a mock Bridge, for tests of a page's menu bar. Install it before
 * the first `createMenuBarController()`. The mock answers as Runic Desktop does: `stale` for an item of an earlier
 * bar, `disabled` for a disabled item, and `executed` after recording the invocation.
 */
export function installMockMenuBar(menus: readonly MenuBarMenu[], options: MockMenuBarOptions = {}): MockMenuBar {
  const installed = (globalThis as { __runicBridge?: RunicBridgeClient }).__runicBridge;
  const bridge = options.bridge ?? (isMockBridge(installed) ? installed : installMockBridge(createMockBridge()));
  const platform = options.platform ?? (isApplePlatform() ? "macos" : "linux");
  const invocations: MenuBarItem[] = [];
  let current = menus;
  let placement = options.placement ?? "page";
  let shortcuts = options.shortcuts ?? "page";
  let revision = 1;
  let menusRevision = 1;
  let leases = 0;
  let watching = false;

  const stateOf = () => ({
    protocol: 2, revision, placement, platform, menus: current,
    shortcuts: placement === "page" && leases > 0 ? "app" : shortcuts,
  });
  const publish = () => {
    revision++;
    if (watching) bridge.push({ kind: "menu-bar", route: menuBarRoute, state: stateOf() });
  };
  const invalid = { ok: false, error: { kind: "rejected", reason: "invalid-request", message: "The menu bar request is invalid." } };

  bridge.onConnectionChange(connected => { if (!connected) { leases = 0; watching = false; } });
  bridge.intercept(({ route, kind, message }) => {
    if (route !== menuBarRoute) return undefined;
    switch (kind) {
      case "menu-bar-watch": watching = true; return { ok: true, state: stateOf() };
      case "menu-bar-unwatch": watching = false; return { ok: true };
      case "menu-bar-invoke": {
        const at = message["revision"];
        const key = message["key"];
        if (typeof at !== "number" || typeof key !== "string") return invalid;
        const found = at < menusRevision || at > revision ? undefined : find(current, key);
        if (!found) return { ok: true, result: "stale" };
        if (!found.enabled) return { ok: true, result: "disabled" };
        invocations.push(found.item);
        return { ok: true, result: options.onInvoke?.(found.item) ?? "executed" };
      }
      case "menu-bar-shortcuts": {
        const owner = message["shortcuts"];
        if (owner !== "app" && owner !== "runic") return invalid;
        const before = leases;
        leases = owner === "app" ? leases + 1 : Math.max(0, leases - 1);
        if ((before === 0) !== (leases === 0)) publish();
        return { ok: true, state: stateOf() };
      }
      default: return invalid;
    }
  });

  return {
    bridge,
    get revision() { return revision; },
    get menus() { return current; },
    get placement() { return placement; },
    get shortcuts() { return shortcuts; },
    get leased() { return leases > 0; },
    get invocations() { return invocations; },
    update(change) {
      if (change.menus !== undefined) current = change.menus;
      if (change.placement !== undefined) placement = change.placement;
      if (change.shortcuts !== undefined) shortcuts = change.shortcuts;
      publish();
      if (change.menus !== undefined) menusRevision = revision;
    },
  };
}
