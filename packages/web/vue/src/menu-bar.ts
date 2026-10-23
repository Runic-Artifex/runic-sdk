import { hasInjectionContext, inject, shallowRef, ssrContextKey, type ShallowRef } from "vue";
import {
  BridgeError,
  createMenuBarController,
  type MenuBarController,
  type MenuBarControllerOptions,
  type MenuBarRequest,
  type MenuBarState,
} from "@runic-artifex/views";
import { releaseWithScope } from "./scope.js";

export type {
  MenuBarEntry,
  MenuBarInvokeResult,
  MenuBarItem,
  MenuBarMenu,
  MenuBarRequest,
  MenuBarSeparator,
  MenuBarState,
} from "@runic-artifex/views";

export interface UseMenuBarOptions extends Omit<MenuBarControllerOptions, "signal"> {
  /**
   * Receives the `focus` (Alt tap, F10) and `open` (Alt plus a menu's access key) requests: focus the first trigger
   * or set the menubar component's open menu.
   */
  readonly onRequest?: (request: MenuBarRequest) => void;
}

export interface MenuBarHandle {
  /** The menu bar as the page renders it; render a bar only when `placement` is `"page"`. */
  readonly state: Readonly<ShallowRef<MenuBarState>>;
  /** Runs an item through the native bar's command path. */
  readonly invoke: MenuBarController["invoke"];
  /** Matches a `keydown` event against the items' shortcuts, for the `shortcuts: "app"` option. */
  readonly handleKeyDown: MenuBarController["handleKeyDown"];
}

/**
 * Follows the window's declared menu bar (Runic Desktop's `DesktopMenuBarPresentation.Page`) as a shallow ref, as
 * headless state for the application's own menubar component, through `createMenuBarController`, until the current
 * effect scope is disposed. Call it in `setup()` or `effectScope().run()`: outside an effect scope it is never
 * released (development builds warn). Server rendering creates no controller and stays `connecting`.
 *
 * ```vue
 * const open = ref("");
 * const { state: menuBar, invoke } = useMenuBar({ onRequest: request => { if (request.kind === "open") open.value = request.menu.key; } });
 * <MenubarRoot v-if="menuBar.placement === 'page'" v-model="open">…<MenubarItem @select="invoke(item)">
 * ```
 */
export function useMenuBar(options: UseMenuBarOptions = {}): MenuBarHandle {
  const { onRequest, ...controllerOptions } = options;
  // Server rendering never stops a component's scope, so it would never release a controller.
  if (hasInjectionContext() && inject(ssrContextKey, null) !== null) {
    const state = shallowRef<MenuBarState>(Object.freeze({
      status: "connecting", placement: "none", shortcuts: controllerOptions.shortcuts === "app" ? "app" : "host",
      accessKeyHints: false, revision: 0, menus: Object.freeze([]),
    }));
    return {
      state,
      invoke: () => Promise.reject(new BridgeError("unavailable", "The menu bar is not followed during server rendering.", { route: "__runicMenuBarInvoke" })),
      handleKeyDown: () => false,
    };
  }
  const controller = createMenuBarController(controllerOptions);
  const state = shallowRef(controller.current);
  controller.subscribe(() => { state.value = controller.current; });
  if (onRequest) controller.onRequest(onRequest);
  releaseWithScope("useMenuBar", () => controller.dispose());
  return { state, invoke: controller.invoke, handleKeyDown: controller.handleKeyDown };
}
