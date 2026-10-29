import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { afterEach, test } from "node:test";
import { createMenuBarController, splitAccessKey, type MenuBarController, type MenuBarItem, type MenuBarMenu, type MenuBarRequest } from "../dist/index.js";
import { createMockBridge, installMockBridge, installMockMenuBar } from "../dist/mock.js";

// The in-page menu bar (specs/desktop/menu-bar-page.md): the controller over the mock host routes.
const fixture = JSON.parse(readFileSync(new URL("../../../../specs/desktop/fixtures/menu-bar-page.json", import.meta.url), "utf8")) as {
  readonly menus: readonly MenuBarMenu[];
};

type Host = typeof globalThis & { __runicBridge?: unknown };
const controllers: MenuBarController[] = [];
afterEach(() => {
  for (const controller of controllers.splice(0)) controller.dispose();
  delete (globalThis as Host).__runicBridge;
});

function controller(options?: Parameters<typeof createMenuBarController>[0]) {
  const created = createMenuBarController(options);
  controllers.push(created);
  return created;
}

async function until(condition: () => boolean, message: string): Promise<void> {
  for (let round = 0; !condition(); round++) {
    if (round > 500) assert.fail(message);
    await new Promise(resolve => setTimeout(resolve, 1));
  }
}

function key(init: Record<string, unknown>, type = "keydown"): Event & { key: string } {
  return Object.assign(new Event(type, { cancelable: true }), { ctrlKey: false, altKey: false, shiftKey: false, metaKey: false, ...init }) as unknown as
    Event & { key: string };
}

function item(menus: readonly MenuBarMenu[], commandId: string): MenuBarItem {
  const find = (entries: readonly MenuBarMenu["entries"][number][]): MenuBarItem | undefined => {
    for (const entry of entries) {
      if (entry.kind === "item" && entry.commandId === commandId) return entry;
      if (entry.kind === "menu") { const found = find(entry.entries); if (found) return found; }
    }
    return undefined;
  };
  return find(menus) ?? assert.fail(`No item ${commandId}`);
}

test("the state carries the shared fixture's menus, placement and shortcut owner", async () => {
  installMockMenuBar(fixture.menus, { platform: "linux" });
  const bar = controller();
  assert.equal(bar.current.status, "connecting");
  await until(() => bar.current.status === "ready", "The controller did not connect.");
  assert.deepEqual(bar.current.menus, fixture.menus);
  assert.equal(bar.current.placement, "page");
  assert.equal(bar.current.shortcuts, "page");
  assert.equal(Object.isFrozen(bar.current), true);
  const save = item(bar.current.menus, "file.save");
  assert.equal(save.shortcutText, "Shift+Ctrl+S");
  assert.equal(save.ariaKeyShortcuts, "Control+Shift+S");
  assert.deepEqual(splitAccessKey(save), ["S", "a", "ve As…"]);
  assert.deepEqual(splitAccessKey(item(bar.current.menus, "help.about")), ["About (", "Q", ")"]);
  assert.deepEqual(splitAccessKey(item(bar.current.menus, "file.export")), ["PDF", "", ""]);
  assert.deepEqual(splitAccessKey({ ...save, label: "😀 Save", accessKeyIndex: 0 }), ["", "😀", " Save"]);
});

test("unknown entry kinds and roles from a later host are left out or mapped", async () => {
  const later = [{ ...fixture.menus[0]!, role: "window", entries: [{ kind: "toolbar", key: "x" }, ...fixture.menus[0]!.entries] }] as unknown as MenuBarMenu[];
  installMockMenuBar(later, { placement: "dock" as "page" });
  const bar = controller();
  await until(() => bar.current.status === "ready", "The controller did not connect.");
  assert.equal(bar.current.menus[0]!.role, "none");
  assert.deepEqual(bar.current.menus[0]!.entries, fixture.menus[0]!.entries);
  assert.equal(bar.current.placement, "none");
});

test("invoke runs enabled items and reports disabled and stale ones", async () => {
  const host = installMockMenuBar(fixture.menus, { platform: "linux" });
  const bar = controller();
  await until(() => bar.current.status === "ready", "The controller did not connect.");
  const save = item(bar.current.menus, "file.save");
  assert.equal(await bar.invoke(save), "executed");
  assert.deepEqual(host.invocations.map(entry => entry.commandId), ["file.save"]);
  assert.equal(await bar.invoke(item(bar.current.menus, "edit.delete")), "disabled");
  assert.equal(await bar.invoke(item(bar.current.menus, "tools.run")), "disabled", "An item of a disabled menu ran.");

  // A new bar reaches the page; an item of the earlier bar is stale, its successor runs.
  const changes: number[] = [];
  bar.subscribe(() => changes.push(bar.current.revision));
  host.update({ menus: fixture.menus.slice(1) });
  await until(() => bar.current.menus.length === fixture.menus.length - 1, "The new bar did not reach the page.");
  assert.ok(changes.length > 0 && changes.every(revision => revision > 1));
  assert.equal(await bar.invoke(save), "stale");
  assert.equal(await bar.invoke(item(bar.current.menus, "edit.find")), "executed");

  // enabled follows the host; the mock treats every new menus array as a new bar.
  const find = item(bar.current.menus, "edit.find");
  host.update({ menus: host.menus.map(menu => menu.key === "0m" ? { ...menu, entries: menu.entries.map(entry =>
    entry.kind === "item" && entry.commandId === "edit.delete" ? { ...entry, enabled: true } : entry) } : menu) });
  await until(() => item(bar.current.menus, "edit.delete").enabled, "enabled did not follow the host.");
  assert.equal(await bar.invoke(find), "stale");
});

test("a host without the menu bar route leaves the controller unavailable", async () => {
  // A host that does not serve runic.menu-bar answers released, as for any route it does not serve.
  installMockBridge(createMockBridge());
  const bar = controller();
  await until(() => bar.current.status !== "connecting", "The controller kept connecting.");
  assert.equal(bar.current.status, "unavailable");
  assert.equal(bar.current.error?.kind, "unavailable");
  await assert.rejects(bar.invoke(item(fixture.menus, "file.new")), (error: { kind?: string }) => error.kind === "unavailable");
});

test("a host that rejects the menu bar request leaves the controller failed", async () => {
  const bridge = installMockBridge(createMockBridge());
  bridge.intercept(({ route }) => route === "runic.menu-bar"
    ? { ok: false, error: { kind: "rejected", reason: "invalid-request", message: "The menu bar request is invalid." } } : undefined);
  const bar = controller();
  await until(() => bar.current.status !== "connecting", "The controller kept connecting.");
  assert.equal(bar.current.status, "failed");
  assert.equal(bar.current.error?.kind, "failed");
});

test("the controller speaks menu bar page protocol 2 on the Views channel", async () => {
  const host = installMockMenuBar(fixture.menus, { platform: "linux" });
  const bar = controller();
  await until(() => bar.current.status === "ready", "The controller did not connect.");
  assert.equal(await bar.invoke(item(bar.current.menus, "file.new")), "executed");
  const calls = host.bridge.calls.map(call => [call.kind, call.route]);
  assert.deepEqual(calls, [["hello", undefined], ["menu-bar-watch", "runic.menu-bar"], ["menu-bar-invoke", "runic.menu-bar"]]);
  const invoke = host.bridge.calls.at(-1)!.message;
  assert.deepEqual(invoke, { kind: "menu-bar-invoke", route: "runic.menu-bar", revision: host.revision, key: item(fixture.menus, "file.new").key });
  // The host pushes each change of its revision while the controller watches; dispose stops the watch.
  const pushes: unknown[] = [];
  const stop = host.bridge.onMessage(message => pushes.push(JSON.parse(message)));
  host.update({ placement: "window" });
  await until(() => bar.current.placement === "window", "The push did not reach the controller.");
  assert.deepEqual(pushes.map(push => (push as { kind: string; route: string }).kind), ["menu-bar"]);
  assert.equal((pushes[0] as { state: { protocol: number; revision: number } }).state.protocol, 2);
  assert.equal(bar.current.revision, host.revision);
  bar.dispose();
  await until(() => host.bridge.calls.some(call => call.kind === "menu-bar-unwatch"), "dispose did not unwatch.");
  host.update({ placement: "page" });
  assert.equal(pushes.length, 1);
  stop();
});

test("the page matches shortcuts where the host has no key path, after the page and outside compositions", async () => {
  const host = installMockMenuBar(fixture.menus, { platform: "linux" });
  const bar = controller();
  await until(() => bar.current.status === "ready", "The controller did not connect.");
  const save = key({ key: "S", code: "KeyS", ctrlKey: true, shiftKey: true });
  globalThis.dispatchEvent(save);
  assert.equal(save.defaultPrevented, true);
  await until(() => host.invocations.length === 1, "Ctrl+Shift+S did not run Save As.");

  const kept = key({ key: "s", code: "KeyS", ctrlKey: true, shiftKey: true });
  kept.preventDefault();
  globalThis.dispatchEvent(kept);
  globalThis.dispatchEvent(key({ key: "S", code: "KeyS", ctrlKey: true, shiftKey: true, isComposing: true }));
  // A disabled item's shortcut passes through.
  const deleted = key({ key: "Delete", code: "Delete" });
  globalThis.dispatchEvent(deleted);
  assert.equal(deleted.defaultPrevented, false);
  await new Promise(resolve => setTimeout(resolve, 5));
  assert.equal(host.invocations.length, 1, "A key the page kept, a composing key or a disabled item ran a command.");

  // The host matches them on its own key path.
  host.update({ shortcuts: "host" });
  await until(() => bar.current.shortcuts === "host", "The owner did not follow the host.");
  globalThis.dispatchEvent(key({ key: "N", code: "KeyN", ctrlKey: true }));
  await new Promise(resolve => setTimeout(resolve, 5));
  assert.equal(host.invocations.length, 1, "The page matched a shortcut the host owns.");
});

test("Alt, F10 and Alt plus an access key request focus and open on Windows and Linux", async () => {
  installMockMenuBar(fixture.menus, { platform: "windows", shortcuts: "host" });
  const bar = controller();
  const requests: MenuBarRequest[] = [];
  bar.onRequest(request => requests.push(request));
  await until(() => bar.current.status === "ready", "The controller did not connect.");

  globalThis.dispatchEvent(key({ key: "Alt", code: "AltLeft", altKey: true }));
  assert.equal(bar.current.accessKeyHints, true, "Holding Alt did not show the access keys.");
  globalThis.dispatchEvent(key({ key: "Alt", code: "AltLeft" }, "keyup"));
  assert.deepEqual(requests, [{ kind: "focus" }]);
  assert.equal(bar.current.accessKeyHints, true, "The hints ended before the request was handled.");
  globalThis.dispatchEvent(key({ key: "Escape", code: "Escape" }));
  assert.equal(bar.current.accessKeyHints, false);

  // Alt used as a modifier of another key is not a tap.
  globalThis.dispatchEvent(key({ key: "Alt", code: "AltLeft", altKey: true }));
  globalThis.dispatchEvent(key({ key: "ArrowLeft", code: "ArrowLeft", altKey: true }));
  globalThis.dispatchEvent(key({ key: "Alt", code: "AltLeft" }, "keyup"));
  assert.equal(requests.length, 1);
  assert.equal(bar.current.accessKeyHints, false);

  const f10 = key({ key: "F10", code: "F10" });
  globalThis.dispatchEvent(f10);
  assert.equal(f10.defaultPrevented, true);
  globalThis.dispatchEvent(new Event("blur"));
  assert.equal(bar.current.accessKeyHints, false, "Blur did not end the hints.");

  const altF = key({ key: "f", code: "KeyF", altKey: true });
  globalThis.dispatchEvent(altF);
  assert.equal(altF.defaultPrevented, true);
  const altZ = key({ key: "z", code: "KeyZ", altKey: true });
  globalThis.dispatchEvent(altZ);
  assert.equal(altZ.defaultPrevented, false, "Alt with a key no menu has was taken.");
  const open = requests.slice(1);
  assert.deepEqual(open.map(request => request.kind), ["focus", "open"]);
  assert.equal(open[1]?.kind === "open" ? open[1].menu.label : undefined, "File");

  // A key the page handled requests nothing.
  const kept = key({ key: "F10", code: "F10" });
  kept.preventDefault();
  globalThis.dispatchEvent(kept);
  assert.equal(requests.length, 3);
});

test("no keyboard requests on macOS, outside the page or with keyboard: false", async () => {
  for (const [options, mock] of [
    [{}, { platform: "macos" as const }],
    [{}, { platform: "linux" as const, placement: "window" as const }],
    [{ keyboard: false }, { platform: "linux" as const }],
  ] as const) {
    installMockMenuBar(fixture.menus, mock);
    const bar = controller(options);
    const requests: MenuBarRequest[] = [];
    bar.onRequest(request => requests.push(request));
    await until(() => bar.current.status === "ready", "The controller did not connect.");
    globalThis.dispatchEvent(key({ key: "F10", code: "F10" }));
    globalThis.dispatchEvent(key({ key: "f", code: "KeyF", altKey: true }));
    assert.deepEqual(requests, [], JSON.stringify(mock));
    assert.equal(bar.current.accessKeyHints, false);
    bar.dispose();
    delete (globalThis as Host).__runicBridge;
  }
});

test("shortcuts: \"app\" takes the lease, handleKeyDown invokes, and dispose or abort releases it", async () => {
  const host = installMockMenuBar(fixture.menus, { platform: "linux", shortcuts: "host" });
  const lifetime = new AbortController();
  const bar = controller({ shortcuts: "app", signal: lifetime.signal });
  await until(() => bar.current.status === "ready", "The controller did not connect.");
  assert.equal(host.leased, true);
  assert.equal(bar.current.shortcuts, "app");

  // The page's own key handling: the controller does not match on its own.
  globalThis.dispatchEvent(key({ key: "n", code: "KeyN", ctrlKey: true }));
  await new Promise(resolve => setTimeout(resolve, 5));
  assert.equal(host.invocations.length, 0);
  let prevented = 0;
  const event = { key: "n", code: "KeyN", ctrlKey: true, preventDefault: () => { prevented++; } };
  assert.equal(bar.handleKeyDown(event), true);
  assert.equal(prevented, 1);
  await until(() => host.invocations.length === 1, "handleKeyDown did not invoke New.");
  // The invocation's reply is on its way: let it arrive before the connection is lost below.
  await host.bridge.flush();
  assert.equal(bar.handleKeyDown({ ...event, defaultPrevented: true }), false);
  assert.equal(bar.handleKeyDown({ ...event, key: "q", code: "KeyQ" }), false);

  // The lease belongs to a connection: a new connection watches and takes it again.
  const count = (kind: string) => host.bridge.calls.filter(call => call.kind === kind).length;
  assert.deepEqual([count("menu-bar-watch"), count("menu-bar-shortcuts")], [1, 1]);
  host.bridge.disconnect();
  assert.equal(host.leased, false);
  await until(() => bar.current.status === "connecting", "The controller did not see the lost connection.");
  host.bridge.reconnect();
  await until(() => host.leased, "The lease was not taken on the new connection.");
  assert.deepEqual([count("hello"), count("menu-bar-watch"), count("menu-bar-shortcuts")], [2, 2, 2]);
  assert.equal(bar.current.status, "ready");

  lifetime.abort();
  await until(() => !host.leased, "Aborting did not release the lease.");
  assert.deepEqual(host.bridge.calls.filter(call => call.kind === "menu-bar-shortcuts").at(-1)!.message["shortcuts"], "runic");
  assert.equal(bar.handleKeyDown(event), false, "A disposed controller matched a shortcut.");
  await assert.rejects(bar.invoke(item(fixture.menus, "file.new")), (error: { kind?: string }) => error.kind === "released");
});
