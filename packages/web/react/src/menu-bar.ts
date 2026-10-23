import { useEffect, useInsertionEffect, useRef, useState, useSyncExternalStore } from "react";
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

export interface UseMenuBarOptions extends Omit<MenuBarControllerOptions, "signal"> {
  /**
   * Receives the `focus` (Alt tap, F10) and `open` (Alt plus a menu's access key) requests: focus the first trigger
   * or open the menu in the menubar component. The latest function is called.
   */
  readonly onRequest?: (request: MenuBarRequest) => void;
}

export interface MenuBarHandle {
  /** The menu bar as the page renders it; render a bar only when `placement` is `"page"`. */
  readonly state: MenuBarState;
  /** Runs an item through the native bar's command path. Rejects with a BridgeError before the hook connected. */
  readonly invoke: MenuBarController["invoke"];
  /** Matches a `keydown` event against the items' shortcuts, for the `shortcuts: "app"` option. */
  readonly handleKeyDown: MenuBarController["handleKeyDown"];
}

type MenuBarStore = {
  readonly initial: MenuBarState;
  readonly subscribe: (onChange: () => void) => () => void;
  readonly read: () => MenuBarState;
  readonly attach: (controller: MenuBarController) => () => void;
  readonly invoke: MenuBarController["invoke"];
  readonly handleKeyDown: MenuBarController["handleKeyDown"];
};

// The state before a controller follows the host, as a new controller starts.
const connectingState = (shortcuts: MenuBarControllerOptions["shortcuts"]): MenuBarState => Object.freeze({
  status: "connecting", placement: "none", shortcuts: shortcuts === "app" ? "app" : "host", accessKeyHints: false,
  revision: 0, menus: Object.freeze([]),
});

// The controller listens to the window and polls the host from creation, so the effect owns it; render reads a
// store that mirrors the mounted controller.
function createMenuBarStore(shortcuts: MenuBarControllerOptions["shortcuts"]): MenuBarStore {
  const initial = connectingState(shortcuts);
  const listeners = new Set<() => void>();
  let snapshot = initial;
  let controller: MenuBarController | undefined;
  const publish = (next: MenuBarState) => {
    if (next === snapshot) return;
    snapshot = next;
    for (const listener of [...listeners]) listener();
  };
  return {
    initial,
    subscribe(onChange) {
      listeners.add(onChange);
      return () => { listeners.delete(onChange); };
    },
    read: () => snapshot,
    attach(mounted) {
      controller = mounted;
      const stop = mounted.subscribe(() => publish(mounted.current));
      publish(mounted.current);
      return () => {
        stop();
        if (controller === mounted) controller = undefined;
      };
    },
    invoke: item => controller
      ? controller.invoke(item)
      : Promise.reject(new BridgeError("disconnected", "The menu bar is not connected yet.", { route: "__runicMenuBarInvoke" })),
    handleKeyDown: event => controller?.handleKeyDown(event) ?? false,
  };
}

/**
 * Follows the window's declared menu bar (Runic Desktop's `DesktopMenuBarPresentation.Page`) while the component is
 * mounted, as headless state for the application's own menubar component, through `createMenuBarController`. Pass
 * `invoke` to the component's item `onSelect`/`onClick` and map `onRequest` to it. A changed `shortcuts` or
 * `keyboard` creates a new controller; unmounting disposes it. The server snapshot is `connecting`.
 *
 * ```tsx
 * const menuBar = useMenuBar({ onRequest: request => request.kind === "open" ? setOpen(request.menu.key) : focusBar() });
 * if (menuBar.state.placement !== "page") return null;
 * menuBar.state.menus.map(menu => <Menubar.Menu key={menu.key}>…</Menubar.Menu>)
 * ```
 */
export function useMenuBar(options: UseMenuBarOptions = {}): MenuBarHandle {
  const { shortcuts, keyboard } = options;
  const onRequest = useRef(options.onRequest);
  useInsertionEffect(() => { onRequest.current = options.onRequest; });
  const [store] = useState(() => createMenuBarStore(shortcuts));
  useEffect(() => {
    const controller = createMenuBarController({
      ...(shortcuts === undefined ? {} : { shortcuts }),
      ...(keyboard === undefined ? {} : { keyboard }),
    });
    controller.onRequest(request => onRequest.current?.(request));
    const detach = store.attach(controller);
    return () => {
      detach();
      controller.dispose();
    };
  }, [store, shortcuts, keyboard]);
  const state = useSyncExternalStore(store.subscribe, store.read, () => store.initial);
  return { state, invoke: store.invoke, handleKeyDown: store.handleKeyDown };
}
