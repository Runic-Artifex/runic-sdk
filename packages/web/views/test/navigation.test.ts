// navigation-path/1 on the page (specs/application/README.md, "Navigation paths"): the window's Back, the hello's path,
// path pushes, and the URL and history kept in step with them, with a refused change reverted.
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  installBackNavigation, installNavigationPath, navigateBack, onNavigationPath, openNavigationPath,
  type NavigationPathChange, type NavigationWindow, type RunicBridgeClient,
} from "../dist/index.js";

const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const tick = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));
const committed = { kind: "Committed", rejection: null, arrival: "None" };
const record = (index: number, path: string | null) => ({ state: { runicNavigation: { index, path } }, hash: path === null ? "#/" : `#/${path}` });
const indexes = (page: { entries: { state: unknown }[] }) =>
  page.entries.map(entry => (entry.state as { runicNavigation?: { index: number } } | null)?.runicNavigation?.index);
const refusedOutcome = { kind: "Rejected", rejection: "Guard", arrival: "None" };

/** A host whose path region holds a stack of routes; `refuse` makes its guard say no. */
function installHost(options: { features?: readonly string[]; stack?: (string | null)[]; delay?: number; pathAtHello?: boolean } = {}) {
  const features = options.features ?? ["text-splice/1", "navigation-path/1"];
  const pathAtHello = options.pathAtHello ?? true;
  const listeners = new Set<(text: string) => void>();
  const stack = options.stack ?? ["home"];
  const requests: Record<string, unknown>[] = [];
  let refuse = false;
  const pushPath = (operation: string) => {
    const text = JSON.stringify({ kind: "navigation-path", path: stack.at(-1) ?? null, operation });
    for (const listener of [...listeners]) listener(text);
  };
  const bridge: RunicBridgeClient = {
    isConnected: () => true,
    onConnectionChange(report) { report(true); return () => {}; },
    onMessage(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    async request(text) {
      const message = JSON.parse(text) as Record<string, unknown>;
      if (message["kind"] === "hello")
        return JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features,
          ...(features.includes("navigation-path/1") && pathAtHello ? { navigationPath: stack.at(-1) ?? null } : {}) });
      if (message["kind"] !== "navigation")
        return JSON.stringify({ ok: false, error: { kind: "rejected", reason: "invalid-request", message: "unexpected" } });
      requests.push(message);
      if (options.delay) await tick(options.delay);
      let outcome: object | null = committed;
      if (refuse) outcome = refusedOutcome;
      else if (message["action"] === "back") {
        if (stack.length < 2) outcome = { kind: "Rejected", rejection: "NoHistory", arrival: "None" };
        else { stack.pop(); pushPath("back"); }
      }
      else {
        const path = message["path"] as string;
        const at = stack.indexOf(path);
        if (path.startsWith("nowhere") || path.startsWith("settings")) outcome = null;
        else if (at === stack.length - 1) { /* already there */ }
        else if (at >= 0) { stack.length = at + 1; pushPath("back"); }
        else { stack.push(path); pushPath("push"); }
      }
      return JSON.stringify({ ok: true, outcome, path: stack.at(-1) ?? null });
    },
  };
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  host["__runicBridge"] = bridge;
  return {
    stack, requests, pushPath,
    set refuse(value: boolean) { refuse = value; },
    /** The application navigates by itself. */
    navigate(path: string) { stack.push(path); pushPath("push"); },
    /** The application goes back by itself, such as with a Back button in the page. */
    back() { stack.pop(); pushPath("back"); },
    /** The window's path region becomes available after the hello. */
    report() { pushPath("replace"); },
  };
}

/** A browser history: entries with state and fragment, and popstate on back, go and fragment navigation. */
function fakeWindow(hash = "", history?: { entries: { state: unknown; hash: string }[]; at: number }) {
  const entries: { state: unknown; hash: string }[] = history?.entries ?? [{ state: null, hash }];
  let at = history?.at ?? 0;
  const pop = new Set<(event: { state: unknown }) => void>();
  const listeners = new Map<string, Set<(event: never) => void>>();
  const fire = () => { const state = entries[at]!.state; setTimeout(() => { for (const listener of [...pop]) listener({ state }); }, 0); };
  const toHash = (url?: string) => url ?? entries[at]!.hash;
  const page: NavigationWindow & { entries: typeof entries; readonly at: number; userBack(): void; userForward(): void; type(hash: string): void;
    dispatch(type: string, event: object): void } = {
    entries,
    get at() { return at; },
    location: { get hash() { return entries[at]!.hash; } },
    history: {
      get state() { return entries[at]!.state; },
      pushState(state, _unused, url) { entries.splice(at + 1); entries.push({ state, hash: toHash(url) }); at++; },
      replaceState(state, _unused, url) { entries[at] = { state, hash: toHash(url) }; },
      back() { if (at > 0) { at--; fire(); } },
      go(delta) { const next = at + delta; if (next >= 0 && next < entries.length) { at = next; fire(); } },
    },
    addEventListener(type, listener) {
      if (type === "popstate") pop.add(listener as never);
      else { let set = listeners.get(type); if (!set) listeners.set(type, set = new Set()); set.add(listener); }
    },
    removeEventListener(type, listener) {
      if (type === "popstate") pop.delete(listener as never);
      else listeners.get(type)?.delete(listener);
    },
    userBack() { this.history.back(); },
    userForward() { this.history.go(1); },
    type(next) { entries.splice(at + 1); entries.push({ state: null, hash: next }); at++; fire(); },
    dispatch(type, event) { for (const listener of [...listeners.get(type) ?? []]) (listener as (event: object) => void)(event); },
  };
  return page;
}

test("navigateBack and openNavigationPath return the outcome and the window's path", async () => {
  const app = installHost({ stack: ["home", "customers"] });
  assert.deepEqual(await navigateBack(), { outcome: committed, path: "home" });
  assert.deepEqual(await openNavigationPath("nowhere/1"), { outcome: null, path: "home" });
  app.refuse = true;
  assert.deepEqual(await openNavigationPath("customers/42"), { outcome: refusedOutcome, path: "home" });
  assert.deepEqual(app.requests, [{ kind: "navigation", action: "back" }, { kind: "navigation", action: "open", path: "nowhere/1" },
    { kind: "navigation", action: "open", path: "customers/42" }]);
});

test("without the feature the calls fail and nothing is listened to", async () => {
  installHost({ features: ["text-splice/1"] });
  await assert.rejects(navigateBack(), (error: Error & { kind?: string }) => error.kind === "failed" && /navigation-path\/1/.test(error.message));
});

test("onNavigationPath reports the hello's path, then each change", async () => {
  const app = installHost({ stack: ["home"] });
  const changes: NavigationPathChange[] = [];
  const stop = onNavigationPath(change => changes.push(change));
  await tick(5);
  app.navigate("customers");
  await tick();
  stop();
  app.navigate("customers/42");
  await tick();
  assert.deepEqual(changes, [{ path: "home", operation: "replace" }, { path: "customers", operation: "push" }]);
});

test("the URL follows the path region, and Back, Forward and the address bar open their paths", async () => {
  const app = installHost({ stack: ["home"] });
  const page = fakeWindow();
  const stop = installNavigationPath({ window: page });
  await tick(5);
  assert.equal(page.location.hash, "#/home");

  app.navigate("customers");
  app.navigate("customers/42");
  await tick(5);
  assert.deepEqual(page.entries.map(entry => entry.hash), ["#/home", "#/customers", "#/customers/42"]);

  // The browser's Back opens the previous entry's path: the region goes back.
  page.userBack();
  await tick(10);
  assert.deepEqual(app.stack, ["home", "customers"]);
  assert.equal(page.location.hash, "#/customers");
  // Forward pushes it again.
  page.userForward();
  await tick(10);
  assert.deepEqual(app.stack, ["home", "customers", "customers/42"]);

  // The application's own Back moves the history back instead of adding an entry.
  await navigateBack();
  await tick(10);
  assert.equal(page.at, 1);
  assert.equal(page.location.hash, "#/customers");

  // An edited address bar opens its path.
  page.type("#/customers/7");
  await tick(10);
  assert.deepEqual(app.stack, ["home", "customers", "customers/7"]);
  assert.equal(page.location.hash, "#/customers/7");
  stop();
});

test("a refused Back or address bar edit reverts the URL", async () => {
  const app = installHost({ stack: ["home", "editor"] });
  const page = fakeWindow();
  const stop = installNavigationPath({ window: page });
  await tick(5);
  app.navigate("editor/2");
  await tick(5);
  app.refuse = true;
  page.userBack();
  await tick(20);
  assert.deepEqual(app.stack, ["home", "editor", "editor/2"]);
  assert.equal(page.location.hash, "#/editor/2");
  assert.equal(page.at, 1);

  page.type("#/settings");
  await tick(20);
  assert.equal(page.location.hash, "#/editor/2");
  // A fragment that is not a route path, such as an anchor, is left alone.
  page.type("#section");
  await tick(20);
  assert.equal(app.requests.filter(request => request["action"] === "open").length, 2);
  stop();
});

test("a route path in the URL at load is a deep link", async () => {
  const app = installHost({ stack: ["home"] });
  const page = fakeWindow("#/customers/42");
  const stop = installNavigationPath({ window: page });
  await tick(10);
  assert.deepEqual(app.requests, [{ kind: "navigation", action: "open", path: "customers/42" }]);
  assert.deepEqual(app.stack, ["home", "customers/42"]);
  assert.equal(page.location.hash, "#/customers/42");
  stop();
});

test("the mouse's Back button and Alt+Left go back, and text fields keep Alt+Left", async () => {
  const app = installHost({ stack: ["home", "a", "b", "c"] });
  const page = fakeWindow();
  const stop = installBackNavigation({ window: page });
  const event = (members: object) => {
    let prevented = false;
    return { defaultPrevented: false, preventDefault() { prevented = true; }, get prevented() { return prevented; }, ...members };
  };
  const press = event({ button: 3 });
  page.dispatch("mousedown", press);
  const release = event({ button: 3 });
  page.dispatch("mouseup", release);
  const keys = { altKey: true, ctrlKey: false, metaKey: false, shiftKey: false };
  const altLeft = event({ key: "ArrowLeft", ...keys, target: { tagName: "DIV" } });
  page.dispatch("keydown", altLeft);
  const inField = event({ key: "ArrowLeft", ...keys, target: { tagName: "INPUT" } });
  page.dispatch("keydown", inField);
  page.dispatch("keydown", event({ key: "BrowserBack", altKey: false, ctrlKey: false, metaKey: false, shiftKey: false, target: null }));
  await tick(10);
  assert.ok(press.prevented && release.prevented && altLeft.prevented && !inField.prevented);
  assert.deepEqual(app.stack, ["home"]);
  stop();
  page.dispatch("mouseup", event({ button: 3 }));
  await tick(5);
  assert.equal(app.requests.length, 3);
});

test("the mock bridge answers navigation requests and pushes the path", async () => {
  const { createMockBridge, installMockBridge } = await import("../dist/mock.js");
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  const stack = ["home"];
  const bridge = createMockBridge({
    navigation: {
      path: "home",
      handle: request => {
        if (request.action === "back") return { outcome: { kind: "Rejected", rejection: "NoHistory", arrival: "None" } };
        stack.push(request.path);
        return { outcome: committed, path: request.path };
      },
    },
  });
  installMockBridge(bridge);
  const changes: NavigationPathChange[] = [];
  const stop = onNavigationPath(change => changes.push(change));
  await bridge.flush();
  assert.deepEqual(await bridge.flushUntil(openNavigationPath("customers")), { outcome: committed, path: "customers" });
  assert.deepEqual(await bridge.flushUntil(navigateBack()),
    { outcome: { kind: "Rejected", rejection: "NoHistory", arrival: "None" }, path: "customers" });
  bridge.pushNavigationPath("settings", "push");
  await bridge.flush();
  assert.deepEqual(changes, [{ path: "home", operation: "replace" }, { path: "settings", operation: "push" }]);
  stop();

});

test("going back to an entry without a route path sends the window's Back", async () => {
  // A borrowed home page has no route: its path is null.
  const app = installHost({ stack: [null] });
  const page = fakeWindow();
  const stop = installNavigationPath({ window: page });
  await tick(5);
  assert.equal(page.location.hash, "#/");
  app.navigate("customers");
  await tick(5);
  page.userBack();
  await tick(10);
  assert.deepEqual(app.requests, [{ kind: "navigation", action: "back" }]);
  assert.deepEqual(app.stack, [null]);
  assert.equal(page.location.hash, "#/");
  stop();
});

test("history indexes have no gaps, and a host Back after Back and push moves back", async () => {
  const app = installHost({ stack: ["home"] });
  const page = fakeWindow();
  const stop = installNavigationPath({ window: page });
  await tick(5);
  app.navigate("a");
  await tick(5);
  page.userBack();
  await tick(10);
  assert.deepEqual(app.stack, ["home"]);
  app.navigate("b");
  await tick(5);
  assert.deepEqual(page.entries.map(entry => entry.hash), ["#/home", "#/b"]);
  assert.deepEqual(indexes(page), [0, 1]);
  // The page's own Back button: the history moves back instead of replacing the entry, and asks nothing.
  const asked = app.requests.length;
  app.back();
  await tick(10);
  assert.equal(page.at, 0);
  assert.equal(page.location.hash, "#/home");
  assert.equal(app.requests.length, asked);
  stop();
});

test("a reload keeps the entries' indexes and paths, also with forward entries", async () => {
  const app = installHost({ stack: ["home", "customers"] });
  // The user went back from customers/42 and reloaded.
  const page = fakeWindow("", { entries: [record(0, "home"), record(1, "customers"), record(2, "customers/42")], at: 1 });
  const stop = installNavigationPath({ window: page });
  await tick(5);
  assert.deepEqual(app.requests, []);
  page.userForward();
  await tick(10);
  assert.deepEqual(app.requests, [{ kind: "navigation", action: "open", path: "customers/42" }]);
  app.navigate("invoices/7");
  await tick(5);
  assert.deepEqual(indexes(page), [0, 1, 2, 3]);
  // Back to the reloaded entry's path, known from its record.
  app.back();
  await tick(10);
  assert.equal(page.at, 2);
  stop();
});

test("with both installs, the mouse's Back goes back once, through the history", async () => {
  const app = installHost({ stack: ["home"] });
  const page = fakeWindow();
  const stopPath = installNavigationPath({ window: page });
  const stopBack = installBackNavigation({ window: page });
  await tick(5);
  app.navigate("a");
  await tick(5);
  page.dispatch("mouseup", { button: 3, defaultPrevented: false, preventDefault() {} });
  await tick(10);
  assert.deepEqual(app.requests, [{ kind: "navigation", action: "open", path: "home" }]);
  assert.deepEqual(app.stack, ["home"]);
  // At the first entry, the window's Back is sent instead.
  page.dispatch("mouseup", { button: 3, defaultPrevented: false, preventDefault() {} });
  await tick(10);
  assert.deepEqual(app.requests.at(-1), { kind: "navigation", action: "back" });
  stopBack();
  stopPath();
});

test("of quick moves that a guard refuses, only the last reverts, to the confirmed entry", async () => {
  const app = installHost({ stack: ["home", "a", "b"], delay: 20 });
  const page = fakeWindow("", { entries: [record(0, "home"), record(1, "a"), record(2, "b")], at: 2 });
  const stop = installNavigationPath({ window: page });
  await tick(5);
  app.refuse = true;
  page.userBack();
  await tick(1);
  page.userBack();
  await tick(80);
  assert.equal(app.requests.length, 2);
  assert.equal(page.at, 2);
  assert.equal(page.location.hash, "#/b");
  // The revert's own popstate is not answered.
  await tick(20);
  assert.equal(app.requests.length, 2);
  stop();
});

test("Cmd+[ goes back only on macOS", async () => {
  const keys = { key: "[", altKey: false, ctrlKey: false, metaKey: true, shiftKey: false, defaultPrevented: false, target: null };
  for (const macOS of [false, true]) {
    installHost({ stack: ["home", "a"] });
    const page = fakeWindow();
    const stop = installBackNavigation({ window: page, macOS });
    let prevented = false;
    page.dispatch("keydown", { ...keys, preventDefault() { prevented = true; } });
    assert.equal(prevented, macOS);
    stop();
  }
});

test("a path region that appears after the hello is reported, and the load-time link opens then", async () => {
  const app = installHost({ stack: ["home"], pathAtHello: false });
  const page = fakeWindow("#/customers/42");
  const stop = installNavigationPath({ window: page });
  await tick(10);
  assert.deepEqual(app.requests, []);
  app.report();
  await tick(10);
  assert.deepEqual(app.requests, [{ kind: "navigation", action: "open", path: "customers/42" }]);
  assert.deepEqual(app.stack, ["home", "customers/42"]);
  stop();
});
