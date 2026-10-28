// @vitest-environment happy-dom

import { flushSync, mount, unmount } from "svelte";
import { afterEach, describe, expect, expectTypeOf, test } from "vitest";
import type { MenuBarMenu, MenuBarRequest, MenuBarState, RunicBridgeClient } from "@runic-artifex/views";
import { installMockMenuBar } from "@runic-artifex/views/mock";
import { useMenuBar, type MenuBarHandle } from "../src/views/index.js";
import * as root from "../src/index.js";
import { onBridgeConnection as viewsOnBridgeConnection, type BridgeConnectionOptions as ViewsBridgeConnectionOptions, type BridgeConnectionState as ViewsBridgeConnectionState } from "@runic-artifex/views";
import MenuBarOwner from "./fixtures/MenuBarOwner.svelte";

const host = globalThis as typeof globalThis & { __runicBridge?: RunicBridgeClient };
afterEach(() => { delete host.__runicBridge; });

// A menu bar as Runic Desktop reports it on Linux (specs/desktop/menu-bar-page.md), served by the mock host routes.
// The lowest-peer run copies only this package, so the spec's fixture is not used here.
const menus: readonly MenuBarMenu[] = [
  { kind: "menu", key: "1m", label: "File", accessKey: "F", accessKeyIndex: 0, role: "file", enabled: true, entries: [
    { kind: "item", key: "1m/0i", label: "New", accessKey: "N", accessKeyIndex: 0, commandId: "file.new", enabled: true,
      shortcut: "Primary+N", shortcutText: "Ctrl+N", ariaKeyShortcuts: "Control+N" },
    { kind: "item", key: "1m/1i", label: "Save", accessKey: "S", accessKeyIndex: 0, commandId: "file.save", enabled: true },
    { kind: "separator", key: "1m/2s" },
  ] },
  { kind: "menu", key: "0m", label: "Edit", accessKey: "E", accessKeyIndex: 0, role: "none", enabled: true, entries: [
    { kind: "item", key: "0m/0i", label: "Find", accessKey: "F", accessKeyIndex: 0, commandId: "edit.find", enabled: false },
  ] },
];

async function until(condition: () => boolean) {
  for (let round = 0; !condition(); round++) {
    if (round > 200) throw new Error("The condition was not met.");
    await new Promise(resolve => setTimeout(resolve, 2));
    flushSync();
  }
}

const keyDown = (init: KeyboardEventInit) => window.dispatchEvent(new KeyboardEvent("keydown", { cancelable: true, ...init }));
const save = menus[0]!.entries[1] as Extract<MenuBarMenu["entries"][number], { kind: "item" }>;

describe("useMenuBar", () => {
  test("a component renders the host's menus, gets requests, and disposes the controller when unmounted", async () => {
    const mock = installMockMenuBar(menus, { platform: "linux" });
    const target = document.createElement("div");
    const requests: MenuBarRequest[] = [];
    const component = mount(MenuBarOwner, { target, props: { requests } });
    flushSync();
    expect(target.textContent).toBe("connecting ");
    await until(() => target.textContent === "ready File,Edit");
    keyDown({ key: "n", code: "KeyN", ctrlKey: true });
    keyDown({ key: "F10", code: "F10" });
    keyDown({ key: "e", code: "KeyE", altKey: true });
    await until(() => mock.invocations.length === 1);
    expect(requests.map(request => request.kind)).toEqual(["focus", "open"]);
    mock.update({ menus: menus.slice(0, 1) });
    await until(() => target.textContent === "ready File");
    await unmount(component);
    keyDown({ key: "n", code: "KeyN", ctrlKey: true });
    await new Promise(resolve => setTimeout(resolve, 10));
    expect(mock.invocations).toHaveLength(1);
  });

  test("state follows the host reactively, invoke reaches it, and the \"app\" lease ends with the owner", async () => {
    const mock = installMockMenuBar(menus, { platform: "linux" });
    let bar!: MenuBarHandle;
    const seen: string[] = [];
    const destroy = $effect.root(() => {
      bar = useMenuBar({ shortcuts: "app" });
      $effect(() => { seen.push(bar.state.status); });
    });
    expectTypeOf(bar.state).toEqualTypeOf<MenuBarState>();
    expect(bar.state.shortcuts).toBe("app");
    await expect(bar.invoke(save)).rejects.toMatchObject({ name: "BridgeError", kind: "disconnected" });
    flushSync();
    await until(() => bar.state.status === "ready" && mock.leased);
    // Protocol 2 watches the bar, then takes the lease: the state can be ready with `shortcuts: "page"` before the
    // lease's revision makes it "app", but the status never goes back.
    expect(seen[0]).toBe("connecting");
    expect(new Set(seen.slice(1))).toEqual(new Set(["ready"]));
    expect(bar.state.shortcuts).toBe("app");
    expect(await bar.invoke(save)).toBe("executed");
    const event = new KeyboardEvent("keydown", { key: "n", code: "KeyN", ctrlKey: true, cancelable: true });
    expect(bar.handleKeyDown(event)).toBe(true);
    await until(() => mock.invocations.length === 2);
    destroy();
    await until(() => !mock.leased);
    expect(bar.handleKeyDown(new KeyboardEvent("keydown", { key: "n", code: "KeyN", ctrlKey: true }))).toBe(false);
  });

  test("createMenuBarController notifies subscribers again when a reply repeats the revision a push already applied", async () => {
    // MenuBarController.subscribe: "Calls `listener` after each change of `current`". The "app" lease's reply and its
    // push carry the same revision (the same state, specs/desktop/menu-bar-page.md "State"), and `apply` sets both.
    const mock = installMockMenuBar(menus, { platform: "linux" });
    let bar!: MenuBarHandle;
    const seen: MenuBarState[] = [];
    const destroy = $effect.root(() => {
      bar = useMenuBar({ shortcuts: "app" });
      $effect(() => { seen.push(bar.state); });
    });
    flushSync();
    await until(() => bar.state.status === "ready" && mock.leased);
    await new Promise(resolve => setTimeout(resolve, 10));
    flushSync();
    const summary = seen.map(state => `${state.status}/${state.revision}/${state.shortcuts}`);
    expect(summary).toEqual([...new Set(summary)]);
    destroy();
  });

  test("an owner destroyed before its first flush, like server rendering, creates no controller", async () => {
    const mock = installMockMenuBar(menus, { platform: "linux" });
    let bar!: MenuBarHandle;
    const destroy = $effect.root(() => { bar = useMenuBar({ shortcuts: "app" }); });
    destroy();
    await new Promise(resolve => setTimeout(resolve, 20));
    expect(mock.leased).toBe(false);
    expect(bar.state).toMatchObject({ status: "connecting", placement: "none", shortcuts: "app", menus: [] });
  });

  test("is a root export (0.11)", () => {
    expect(Object.keys(root).sort()).toEqual([
      "ViewOutlet", "onBridgeConnection", "useBridgeConnection", "useCollectionViewport", "useCommand", "useLatestOperation", "useMenuBar",
      "useOperation", "useView",
    ]);
    // The connection API that useBridgeConnection wraps is re-exported, as the menu-bar types are (0.11).
    expect(root.onBridgeConnection).toBe(viewsOnBridgeConnection);
    expectTypeOf<root.BridgeConnectionState>().toEqualTypeOf<ViewsBridgeConnectionState>();
    expectTypeOf<root.BridgeConnectionOptions>().toEqualTypeOf<ViewsBridgeConnectionOptions>();
  });
});
