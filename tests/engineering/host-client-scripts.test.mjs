import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { runInNewContext } from "node:vm";

const root = resolve(import.meta.dir, "../..");
const scripts = [
  "packages/dotnet/Runic.Application.Views.CsWebUi/www/runic-cswebui.js",
  "packages/dotnet/Runic.Application.Desktop/www/runic-desktop-views.js",
];

// A connected bridge that subscribes to all events cancels navigations,
// including history updates, until navigation is allowed again (#42).
for (const script of scripts) {
  test(`${script} allows history updates the bridge would cancel`, () => {
    const events = [];
    let allowed = false;
    const history = {
      pushState(state) { events.push(["pushState", allowed, state]); },
      replaceState(state) { events.push(["replaceState", allowed, state]); },
    };
    const webui = {
      isConnected: () => true,
      allowNavigation: status => { allowed = status; },
      call: async () => undefined,
    };
    const context = { history, setInterval, clearInterval, console };
    context.window = context;
    context.globalThis = context;
    runInNewContext(readFileSync(resolve(root, script), "utf8"), context);
    context.webui = webui;

    context.history.replaceState({ route: 1 }, "");
    allowed = false;
    context.history.pushState({ route: 2 }, "");

    expect(events).toEqual([["replaceState", true, { route: 1 }], ["pushState", true, { route: 2 }]]);
  });
}

function loadCsWebUiBridge(webui) {
  const context = { history: { pushState() {}, replaceState() {} }, setInterval, clearInterval, setTimeout, clearTimeout, console };
  context.window = context;
  context.globalThis = context;
  runInNewContext(readFileSync(resolve(root, scripts[0]), "utf8"), context);
  context.webui = webui;
  return context;
}

const settle = () => new Promise(resolve => setTimeout(resolve, 0));

// Native WebUI claims an event slot for each incoming call without holding its
// lock, so two calls arriving together can share one and a reply is lost (#53).
// The CS-WebUI client sends the next call once .NET has admitted the previous one.
test("runic-cswebui.js sends a call only after .NET admitted the previous one", async () => {
  const sent = [];
  const replies = new Map();
  const context = loadCsWebUiBridge({
    isConnected: () => true,
    call: name => new Promise(resolve => { sent.push(name); replies.set(name, resolve); }),
  });
  const bridge = context.__runicBridge;

  const wait = bridge.call("__runicOperationWait");
  const snapshot = bridge.call("shellSnapshot");
  const mount = bridge.call("shellMount");
  await settle();
  expect(sent).toEqual(["__runicOperationWait"]);

  // A long-running call stops holding back others once .NET received it.
  context.__runicBridgeAdmitted();
  await settle();
  expect(sent).toEqual(["__runicOperationWait", "shellSnapshot"]);

  // A reply also admits a call, for example one no route received.
  replies.get("shellSnapshot")("snapshot");
  expect(await snapshot).toBe("snapshot");
  await settle();
  expect(sent).toEqual(["__runicOperationWait", "shellSnapshot", "shellMount"]);

  const unmount = bridge.call("shellUnmount");
  await settle();
  expect(sent).toEqual(["__runicOperationWait", "shellSnapshot", "shellMount"]);
  context.__runicBridgeAdmitted();
  await settle();
  expect(sent).toEqual(["__runicOperationWait", "shellSnapshot", "shellMount", "shellUnmount"]);

  replies.get("shellMount")("mounted");
  replies.get("shellUnmount")("unmounted");
  replies.get("__runicOperationWait")("done");
  expect(await Promise.all([wait, mount, unmount])).toEqual(["done", "mounted", "unmounted"]);
});

test("runic-cswebui.js releases the next call when a call fails to start", async () => {
  const sent = [];
  const context = loadCsWebUiBridge({
    isConnected: () => true,
    call: name => {
      sent.push(name);
      return name === "missing" ? Promise.reject(new ReferenceError("No binding")) : Promise.resolve(name);
    },
  });
  const failed = context.__runicBridge.call("missing");
  const next = context.__runicBridge.call("shellSnapshot");
  await expect(failed).rejects.toThrow("No binding");
  expect(await next).toBe("shellSnapshot");
  expect(sent).toEqual(["missing", "shellSnapshot"]);
});

test("runic-cswebui.js does not hold calls behind one .NET never admits", async () => {
  const sent = [];
  const context = loadCsWebUiBridge({
    isConnected: () => true,
    call: name => { sent.push(name); return name === "unanswered" ? new Promise(() => {}) : Promise.resolve(name); },
  });
  void context.__runicBridge.call("unanswered");
  expect(await context.__runicBridge.call("shellSnapshot")).toBe("shellSnapshot");
  expect(sent).toEqual(["unanswered", "shellSnapshot"]);
});

test("runic-cswebui.js credits a late admission to the call the timeout released", async () => {
  const sent = [];
  const context = loadCsWebUiBridge({
    isConnected: () => true,
    call: name => { sent.push(name); return new Promise(() => {}); },
  });
  const bridge = context.__runicBridge;
  void bridge.call("first");
  void bridge.call("second");
  void bridge.call("third");
  await new Promise(resolve => setTimeout(resolve, 1_700));
  expect(sent).toEqual(["first", "second"]);

  // The first call's admission arrives after the timeout released the second.
  context.__runicBridgeAdmitted();
  await settle();
  expect(sent).toEqual(["first", "second"]);

  context.__runicBridgeAdmitted();
  await settle();
  expect(sent).toEqual(["first", "second", "third"]);
});

// Examples serve their own copy; each must match the package's script.
test("example copies of runic-cswebui.js match the package script", () => {
  const expected = readFileSync(resolve(root, scripts[0]), "utf8");
  for (const example of ["notes-reactive-views", "notes-view-first"])
    for (const frontend of ["Frontend", "Angular"])
      expect(readFileSync(resolve(root, `examples/${example}/${frontend}/public/runic-cswebui.js`), "utf8"), `${example}/${frontend}`)
        .toBe(expected);
});

// The all-events binding delivers disconnects, but WebUI then also sends a click
// event for each element with an id. Those collide with Bridge calls (#53), so
// the script marks the elements as WebUI does once it has attached a listener.
test("runic-cswebui.js stops WebUI from sending click events for elements with ids", () => {
  const element = (id, children = []) => ({
    nodeType: 1, id, dataset: {},
    querySelectorAll: () => children.flatMap(child => [child, ...child.querySelectorAll("[id]")]).filter(child => child.id),
  });
  const main = element("main");
  const plain = element("", [main]);
  const documentElement = element("", [plain]);
  let observe;
  const context = {
    history: { pushState() {}, replaceState() {} }, setInterval, clearInterval, setTimeout, clearTimeout, console,
    document: { documentElement },
    MutationObserver: class { constructor(callback) { observe = callback; } observe() {} },
  };
  context.window = context;
  context.globalThis = context;
  runInNewContext(readFileSync(resolve(root, scripts[0]), "utf8"), context);
  expect(main.dataset.webui_click_is_set).toBe("true");
  expect(plain.dataset.webui_click_is_set).toBeUndefined();

  const pane = element("pane");
  const added = element("", [pane]);
  const renamed = element("status");
  observe([{ type: "childList", addedNodes: [added, { nodeType: 3 }] }, { type: "attributes", target: renamed }]);
  expect(pane.dataset.webui_click_is_set).toBe("true");
  expect(renamed.dataset.webui_click_is_set).toBe("true");
  expect(added.dataset.webui_click_is_set).toBeUndefined();
});
