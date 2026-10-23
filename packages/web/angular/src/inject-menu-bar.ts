import { assertInInjectionContext, DestroyRef, inject, Injector, PLATFORM_ID, signal, type Signal } from "@angular/core";
import {
  BridgeError,
  createMenuBarController,
  type MenuBarController,
  type MenuBarControllerOptions,
  type MenuBarRequest,
  type MenuBarState,
} from "@runic-artifex/views";

export type {
  MenuBarEntry,
  MenuBarInvokeResult,
  MenuBarItem,
  MenuBarMenu,
  MenuBarRequest,
  MenuBarSeparator,
  MenuBarState,
} from "@runic-artifex/views";

export interface MenuBarHandle {
  /** The menu bar as the page renders it; render a bar only when `placement` is `"page"`. */
  readonly state: Signal<MenuBarState>;
  /** Runs an item through the native bar's command path. Rejects with a BridgeError under server rendering. */
  readonly invoke: MenuBarController["invoke"];
  /** Matches a `keydown` event against the items' shortcuts, for the `shortcuts: "app"` option. */
  readonly handleKeyDown: MenuBarController["handleKeyDown"];
}

export interface InjectMenuBarOptions extends Omit<MenuBarControllerOptions, "signal"> {
  /**
   * Receives the `focus` (Alt tap, F10) and `open` (Alt plus a menu's access key) requests: focus the first
   * `cdkMenuItem` of the bar or open a menu's `CdkMenuTrigger`.
   */
  readonly onRequest?: (request: MenuBarRequest) => void;
  /** Required outside an injection context. */
  readonly injector?: Injector;
}

/**
 * Follows the window's declared menu bar (Runic Desktop's `DesktopMenuBarPresentation.Page`) as a signal, as headless
 * state for the application's own menubar (for example the CDK Menu's `cdkMenuBar`), through
 * `createMenuBarController`, for the lifetime of the injection context. Destruction disposes the controller. Under
 * server rendering (`PLATFORM_ID` `"server"`) it creates none and stays `connecting`.
 *
 * ```ts
 * readonly menuBar = injectMenuBar({ onRequest: request => this.onMenuRequest(request) });
 * // @if (menuBar.state().placement === "page") { <div cdkMenuBar>…<button cdkMenuItem (cdkMenuItemTriggered)="menuBar.invoke(item)"> }
 * ```
 */
export function injectMenuBar({ injector: provided, onRequest, ...options }: InjectMenuBarOptions = {}): MenuBarHandle {
  if (!provided) assertInInjectionContext(injectMenuBar);
  const injector = provided ?? inject(Injector);
  if (injector.get(PLATFORM_ID, null) === "server") {
    const state = signal<MenuBarState>(Object.freeze({
      status: "connecting", placement: "none", shortcuts: options.shortcuts === "app" ? "app" : "host",
      accessKeyHints: false, revision: 0, menus: Object.freeze([]),
    }));
    return {
      state: state.asReadonly(),
      invoke: () => Promise.reject(new BridgeError("unavailable", "The menu bar is not followed during server rendering.", { route: "__runicMenuBarInvoke" })),
      handleKeyDown: () => false,
    };
  }
  const controller = createMenuBarController(options);
  const state = signal(controller.current);
  controller.subscribe(() => state.set(controller.current));
  if (onRequest) controller.onRequest(onRequest);
  injector.get(DestroyRef).onDestroy(() => controller.dispose());
  return { state: state.asReadonly(), invoke: controller.invoke, handleKeyDown: controller.handleKeyDown };
}
