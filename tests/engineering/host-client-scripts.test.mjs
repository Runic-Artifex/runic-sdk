import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { runInNewContext } from "node:vm";

const root = resolve(import.meta.dir, "../..");
const scripts = [
  "packages/dotnet/Runic.Application.Views.CsWebUi/www/runic-cswebui.js",
  "packages/dotnet/Runic.Application.Views.Desktop/www/runic-desktop-views.js",
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

// WebUI assigns each concurrent call its own event slot since webui#721, which
// CsWebUi 2.5.0-beta.4.7 bundles, so the client sends every call at once.
test("runic-cswebui.js sends concurrent calls without waiting for replies", async () => {
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
  expect(sent).toEqual(["__runicOperationWait", "shellSnapshot", "shellMount"]);

  replies.get("shellMount")("mounted");
  replies.get("shellSnapshot")("snapshot");
  replies.get("__runicOperationWait")("done");
  expect(await Promise.all([wait, snapshot, mount])).toEqual(["done", "snapshot", "mounted"]);
});

test("runic-cswebui.js rejects a call that fails to start", async () => {
  const sent = [];
  const context = loadCsWebUiBridge({
    isConnected: () => true,
    call: name => {
      sent.push(name);
      if (name === "throws") throw new TypeError("WebUI failed");
      return name === "missing" ? Promise.reject(new ReferenceError("No binding")) : Promise.resolve(name);
    },
  });
  const thrown = context.__runicBridge.call("throws");
  const failed = context.__runicBridge.call("missing");
  const next = context.__runicBridge.call("shellSnapshot");
  await expect(thrown).rejects.toThrow("WebUI failed");
  await expect(failed).rejects.toThrow("No binding");
  expect(await next).toBe("shellSnapshot");
  expect(sent).toEqual(["throws", "missing", "shellSnapshot"]);
});

test("runic-cswebui.js does not hold calls behind an unanswered one", async () => {
  const sent = [];
  const context = loadCsWebUiBridge({
    isConnected: () => true,
    call: name => { sent.push(name); return name === "unanswered" ? new Promise(() => {}) : Promise.resolve(name); },
  });
  void context.__runicBridge.call("unanswered");
  expect(await context.__runicBridge.call("shellSnapshot")).toBe("shellSnapshot");
  expect(sent).toEqual(["unanswered", "shellSnapshot"]);
});

const lostMessage = "The CS-WebUI connection was lost before the call completed.";

// WebUI never settles calls in flight when its WebSocket closes, so each would
// wait forever: a command's pending state, an operation start, a long poll.
test("runic-cswebui.js rejects every call in flight when the connection is lost", async () => {
  const sent = [];
  let online = true;
  let releaseReconnecting;
  const context = loadCsWebUiBridge({
    isConnected: () => online,
    call: name => {
      sent.push(name);
      // WebUI holds a call made while it reconnects until the new connection is ready.
      if (name === "duringReconnect") return new Promise(resolve => { releaseReconnecting = resolve; });
      return new Promise(() => {});
    },
  });
  const bridge = context.__runicBridge;
  const outcome = promise => promise.then(value => ({ value }), error => ({ error: error.message }));
  const inFlight = ["counterIncrement", "counterStartSave", "__runicInteractionWait"].map(name => outcome(bridge.call(name, "{}")));
  expect(sent).toEqual(["counterIncrement", "counterStartSave", "__runicInteractionWait"]);

  online = false;
  expect(await Promise.all(inFlight)).toEqual([{ error: lostMessage }, { error: lostMessage }, { error: lostMessage }]);

  // A call sent after the loss belongs to the next connection.
  const reconnecting = outcome(bridge.call("duringReconnect"));
  await settle();
  online = true;
  await new Promise(resolve => setTimeout(resolve, 300));
  releaseReconnecting("answered");
  expect(await reconnecting).toEqual({ value: "answered" });
});

test("runic-cswebui.js stops watching the connection once every call settled", async () => {
  const intervals = new Set();
  const context = loadCsWebUiBridge({ isConnected: () => true, call: name => Promise.resolve(name) });
  context.setInterval = (callback, milliseconds) => { const id = setInterval(callback, milliseconds); intervals.add(id); return id; };
  context.clearInterval = id => { intervals.delete(id); clearInterval(id); };
  const replies = await Promise.all(Array.from({ length: 50 }, (_, index) => context.__runicBridge.call(`call${index}`)));
  expect(replies.length).toBe(50);
  expect(intervals.size).toBe(1);
  await new Promise(resolve => setTimeout(resolve, 300));
  expect(intervals.size).toBe(0);
});

// A hidden window throttles timers, so the poll can miss a loss for a while.
test("runic-cswebui.js sends a call made after an unobserved loss on the next connection", async () => {
  let online = true;
  let releaseReconnecting;
  const context = loadCsWebUiBridge({
    isConnected: () => online,
    call: name => name === "duringReconnect" ? new Promise(resolve => { releaseReconnecting = resolve; }) : new Promise(() => {}),
  });
  const bridge = context.__runicBridge;
  bridge.onReconnect(() => {});
  const before = bridge.call("beforeLoss").catch(error => error.message);
  await settle();
  online = false;
  // Sent before the poll runs: the send itself observes the loss.
  const after = bridge.call("duringReconnect").then(value => value, error => error.message);
  expect(await before).toBe(lostMessage);
  online = true;
  await new Promise(resolve => setTimeout(resolve, 300));
  releaseReconnecting("answered");
  expect(await after).toBe("answered");
});

test("runic-cswebui.js passes replies and failures through unchanged", async () => {
  const context = loadCsWebUiBridge({
    isConnected: () => true,
    call: name => name === "fail" ? Promise.reject(new Error("No binding")) : Promise.resolve(`${name}-reply`),
  });
  expect(await context.__runicBridge.call("snapshot")).toBe("snapshot-reply");
  await expect(context.__runicBridge.call("fail")).rejects.toThrow("No binding");
});

test("runic-cswebui.js watches the connection while a call is unsettled", async () => {
  let online = true;
  const context = loadCsWebUiBridge({
    isConnected: () => online,
    call: name => name === "lost" ? new Promise(() => {}) : Promise.resolve(name),
  });
  const bridge = context.__runicBridge;
  const lost = bridge.call("lost").catch(error => error.message);
  await settle();
  online = false;
  await new Promise(resolve => setTimeout(resolve, 300));
  // Without a reconnect listener, the poll still observes the loss.
  expect(await lost).toBe(lostMessage);
  online = true;
  expect(await bridge.call("snapshot")).toBe("snapshot");
});

test("runic-cswebui.js does not resume a listener registered after an unobserved loss", async () => {
  let online = true;
  const context = loadCsWebUiBridge({
    isConnected: () => online,
    call: name => name === "lost" ? new Promise(() => {}) : Promise.resolve(name),
  });
  const bridge = context.__runicBridge;
  // The waiting call keeps the poll running; no reconnect listener exists.
  const lost = bridge.call("lost").catch(error => error.message);
  await settle();
  online = false;
  await new Promise(resolve => setTimeout(resolve, 300));
  expect(await lost).toBe(lostMessage);
  // The poll stops once idle, so it does not see the reconnect either.
  await new Promise(resolve => setTimeout(resolve, 300));
  online = true;

  let resumes = 0;
  bridge.onReconnect(() => resumes++);
  await new Promise(resolve => setTimeout(resolve, 600));
  expect(resumes).toBe(0);

  // A loss observed while the listener exists still resumes it once.
  online = false;
  await new Promise(resolve => setTimeout(resolve, 300));
  online = true;
  await new Promise(resolve => setTimeout(resolve, 300));
  expect(resumes).toBe(1);
});

// A page learns that the host ended when the WebSocket drops, not on its next
// call (#406). Each host script reports WebUI's own connection state at once and
// after each change, reads it without the Bridge's navigation side effect, and
// stops polling once nothing listens.
for (const script of scripts) {
  test(`${script} reports connection changes as they happen`, async () => {
    let online = true;
    let allowed = 0;
    const intervals = new Set();
    const context = {
      history: { pushState() {}, replaceState() {} }, setTimeout, clearTimeout, console,
      setInterval: (callback, milliseconds) => { const id = setInterval(callback, milliseconds); intervals.add(id); return id; },
      clearInterval: id => { intervals.delete(id); clearInterval(id); },
    };
    context.window = context;
    context.globalThis = context;
    runInNewContext(readFileSync(resolve(root, script), "utf8"), context);
    context.webui = { isConnected: () => online, allowNavigation: () => { allowed++; }, call: async () => "" };
    const bridge = context.__runicBridge;

    const states = [];
    const stop = bridge.onConnectionChange(connected => states.push(connected));
    expect(states).toEqual([true]);
    online = false;
    await new Promise(resolve => setTimeout(resolve, 300));
    expect(states).toEqual([true, false]);
    online = true;
    await new Promise(resolve => setTimeout(resolve, 300));
    expect(states).toEqual([true, false, true]);
    expect(allowed).toBe(0);
    stop();
    expect(intervals.size).toBe(0);
    online = false;
    await new Promise(resolve => setTimeout(resolve, 300));
    expect(states).toEqual([true, false, true]);
  });
}

// Examples serve their own copy; each must match the package's script.
test("example copies of runic-cswebui.js match the package script", () => {
  const expected = readFileSync(resolve(root, scripts[0]), "utf8");
  for (const example of ["notes-reactive-views", "notes-view-first"])
    for (const frontend of ["Frontend", "Angular"])
      expect(readFileSync(resolve(root, `examples/${example}/${frontend}/public/runic-cswebui.js`), "utf8"), `${example}/${frontend}`)
        .toBe(expected);
});

// The all-events binding delivers disconnects, but WebUI then also sends a click
// event for each element with an id. Runic does not use them, so the script
// saves that traffic by marking the elements as WebUI does once it has attached
// a listener.
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

// With the all-events binding, WebUI cancels the page's navigations until a
// Bridge call allows them and reports each to .NET, which then asks the page to
// continue it (#196). The script repeats a cancelled same-origin navigation
// with navigation allowed only for it, and leaves the others cancelled.
test("runic-cswebui.js continues a cancelled same-origin navigation", () => {
  const actions = [];
  let allowed = false;
  let navigate;
  const location = {
    href: "http://localhost:4000/index.html", origin: "http://localhost:4000",
    reload: () => actions.push(["reload", allowed]),
    assign: url => actions.push(["assign", url, allowed]),
    replace: url => actions.push(["replace", url, allowed]),
  };
  const context = {
    history: { pushState() {}, replaceState() {} }, setInterval, clearInterval, setTimeout, clearTimeout, console, URL,
    location, navigation: { addEventListener: (type, listener) => { if (type === "navigate") navigate = listener; } },
    webui: { allowNavigation: status => { allowed = status; } },
  };
  context.window = context;
  context.globalThis = context;
  runInNewContext(readFileSync(resolve(root, scripts[0]), "utf8"), context);
  // WebUI's own listener runs after the script's and cancels the navigation.
  const report = (url, navigationType, { cancelled = true, formData = null } = {}) => {
    const event = { destination: { url, key: "k" }, navigationType, formData, defaultPrevented: false };
    navigate(event);
    event.defaultPrevented = cancelled;
    context.__runicContinueNavigation();
  };

  report("http://localhost:4000/index.html", "reload");
  report("http://localhost:4000/notes.html", "push");
  report("http://localhost:4000/index.html#notes", "replace");
  report("http://example.com/", "push");
  report("http://localhost:4000/save", "push", { formData: {} });
  report("http://localhost:4000/allowed.html", "push", { cancelled: false });
  context.__runicContinueNavigation();

  expect(actions).toEqual([
    ["reload", true],
    ["assign", "http://localhost:4000/notes.html", true],
    ["replace", "http://localhost:4000/index.html#notes", true],
  ]);
  expect(allowed).toBe(false);
});
