import { untrack } from "svelte";
import {
  BridgeError,
  createMenuBarController,
  type MenuBarController,
  type MenuBarControllerOptions,
  type MenuBarRequest,
  type MenuBarState,
} from "@runic-artifex/views";

export interface UseMenuBarOptions extends Omit<MenuBarControllerOptions, "signal"> {
  /**
   * Receives the `focus` (Alt tap, F10) and `open` (Alt plus a menu's access key) requests: focus the first trigger
   * or set the menubar component's open menu.
   */
  readonly onRequest?: (request: MenuBarRequest) => void;
}

export interface MenuBarHandle {
  /** The menu bar as the page renders it; render a bar only when `placement` is `"page"`. A reactive property. */
  readonly state: MenuBarState;
  /** Runs an item through the native bar's command path. Rejects with a BridgeError before the owner's effects ran. */
  readonly invoke: MenuBarController["invoke"];
  /** Matches a `keydown` event against the items' shortcuts, for the `shortcuts: "app"` option. */
  readonly handleKeyDown: MenuBarController["handleKeyDown"];
}

/**
 * Follows the window's declared menu bar (Runic Desktop's `DesktopMenuBarPresentation.Page`) while its owner lives,
 * as headless state for the application's own menubar component, through `createMenuBarController`. The controller
 * starts with the owner's effects, so server rendering never creates one and `state` stays `connecting`. Call during
 * component initialization (or in `$effect.root`); owner destruction disposes the controller.
 *
 * ```svelte
 * let open = $state("");
 * const menuBar = useMenuBar({ onRequest: request => { if (request.kind === "open") open = request.menu.key; } });
 * {#if menuBar.state.placement === "page"}<Menubar.Root bind:value={open}>…{/if}
 * ```
 */
export function useMenuBar(options: UseMenuBarOptions = {}): MenuBarHandle {
  const { onRequest, ...controllerOptions } = options;
  let state = $state.raw<MenuBarState>(Object.freeze({
    status: "connecting", placement: "none", shortcuts: controllerOptions.shortcuts === "app" ? "app" : "host",
    accessKeyHints: false, revision: 0, menus: Object.freeze([]),
  }));
  let controller: MenuBarController | undefined;
  $effect.pre(() => untrack(() => {
    const created = createMenuBarController(controllerOptions);
    controller = created;
    state = created.current;
    created.subscribe(() => { state = created.current; });
    if (onRequest) created.onRequest(onRequest);
    return () => {
      if (controller === created) controller = undefined;
      created.dispose();
    };
  }));
  return {
    get state() { return state; },
    invoke: item => controller
      ? controller.invoke(item)
      : Promise.reject(new BridgeError("disconnected", "The menu bar is not connected yet.", { route: "runic.menu-bar" })),
    handleKeyDown: event => controller?.handleKeyDown(event) ?? false,
  };
}
