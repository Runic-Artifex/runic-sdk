import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { runInNewContext } from "node:vm";

// The page-side host scripts of Views protocol 4 (specs/application/README.md,
// "Host bridge (page)" and "receive"). Each runs in a vm context with fakes of
// the host's own script.
const root = resolve(import.meta.dir, "../..");
const csWebUi = "packages/dotnet/Runic.Application.Views.CsWebUi/www/runic-cswebui.js";
const desktop = "packages/dotnet/Runic.Application.Views.Desktop/www/runic-desktop-views.js";
const scripts = [csWebUi, desktop];

function load(script, globals = {}) {
  const context = {
    history: { pushState() {}, replaceState() {} }, setInterval, clearInterval, setTimeout, clearTimeout, console,
    TextDecoder, URL, ...globals,
  };
  context.window = context;
  context.globalThis = context;
  runInNewContext(readFileSync(resolve(root, script), "utf8"), context);
  return context;
}

const settle = () => new Promise(resolve => setTimeout(resolve, 0));
const poll = () => new Promise(resolve => setTimeout(resolve, 300));
const outcome = promise => promise.then(value => ({ value }), error => ({ error: error.message }));

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
    const context = load(script, { history });
    context.webui = { isConnected: () => true, allowNavigation: status => { allowed = status; }, call: async () => undefined };

    context.history.replaceState({ route: 1 }, "");
    allowed = false;
    context.history.pushState({ route: 2 }, "");

    expect(events).toEqual([["replaceState", true, { route: 1 }], ["pushState", true, { route: 2 }]]);
  });

  // A cached page of protocol 3 reports its protocol diagnostic; nothing reaches the host.
  test(`${script} answers protocol-3 calls without the host`, async () => {
    const sent = [];
    const context = load(script);
    context.webui = { isConnected: () => true, allowNavigation() {}, call: async name => { sent.push(name); return ""; } };
    const bridge = context.__runicBridge;
    expect(await bridge.call("counterSnapshot")).toBe('{"ok":true,"state":{"revision":0},"error":null,"protocol":4}');
    expect(await bridge.call("counterIncrement", "{}")).toBe(
      '{"ok":false,"state":null,"error":{"kind":"released","message":"This View was released."}}');
    expect(await bridge.call("__runicOperationWait")).toBe(
      '{"ok":false,"state":null,"error":{"kind":"released","message":"This View was released."}}');
    expect(sent).toEqual([]);
    expect("onReconnect" in bridge).toBe(false);
  });

  // A1: a page wrapper written for protocol 3 sees nothing reach .NET; the first call says why in the console, once.
  test(`${script} warns once when a page uses the protocol-3 call stub`, async () => {
    const warnings = [];
    const context = load(script, { console: { ...console, warn: (...args) => warnings.push(args.join(" ")) } });
    context.webui = { isConnected: () => true, allowNavigation() {}, call: async () => "" };
    const bridge = context.__runicBridge;
    await bridge.call("counterSnapshot");
    await bridge.call("counterIncrement", "{}");
    expect(warnings.length).toBe(1);
    expect(warnings[0]).toContain('__runicBridge.call("counterSnapshot")');
    expect(warnings[0]).toContain("__runicBridge.request(message)");
  });

  test(`${script} rejects requests without the host's script`, async () => {
    const context = load(script);
    await expect(context.__runicBridge.request('{"kind":"hello"}')).rejects.toThrow(/is unavailable\.$/);
    expect(context.__runicBridge.isConnected()).toBe(false);
  });
}

// ---------------------------------------------------------------------------
// Runic Desktop: webui.js installs __runicDesktopViews with the Views messages
// and the connection's transitions; the script adds no poll.

function fakeDesktop({ connected = true } = {}) {
  const messageListeners = new Set();
  const connectionListeners = new Set();
  const views = {
    connected,
    onMessage(listener) { messageListeners.add(listener); return () => messageListeners.delete(listener); },
    onConnectionChange(listener) { connectionListeners.add(listener); return () => connectionListeners.delete(listener); },
    isConnected: () => views.connected,
    push: message => { for (const listener of [...messageListeners]) listener(message); },
    // webui.js reports transitions only; `repeat` simulates a duplicate report.
    transition(now, { repeat = false } = {}) {
      if (now === views.connected && !repeat) return;
      views.connected = now;
      for (const listener of [...connectionListeners]) listener(now);
    },
    messageListeners, connectionListeners,
  };
  return views;
}

function loadDesktop(views, webui) {
  const intervals = [];
  const context = load(desktop, { setInterval: (...args) => { intervals.push(args); return 0; } });
  if (views) context.__runicDesktopViews = views;
  if (webui) context.webui = webui;
  context.intervals = intervals;
  return context;
}

test("runic-desktop-views.js sends a request on __runicViews and resolves with the reply text", async () => {
  const calls = [];
  let allowed = false;
  const context = loadDesktop(fakeDesktop(), {
    isConnected: () => true,
    allowNavigation: status => { allowed = status; },
    call: async (name, ...args) => { calls.push([name, ...args, allowed]); return '{"ok":true}'; },
  });
  const message = '{"kind":"hello","protocol":4,"session":"s","client":{"name":"c","version":"0"}}';
  expect(await context.__runicBridge.request(message)).toBe('{"ok":true}');
  expect(calls).toEqual([["__runicViews", message, true]]);
});

// 0.14 T2: webui.js's `request` sends the call right behind the token check; the
// script prefers it to webui.call, which waits for the token reply.
test("runic-desktop-views.js sends a request through webui.js's request when it has one", async () => {
  const requests = [];
  const views = fakeDesktop();
  views.request = async (name, ...args) => { requests.push([name, ...args]); return '{"ok":true}'; };
  let allowed = false;
  const context = loadDesktop(views, {
    isConnected: () => false,
    allowNavigation: status => { allowed = status; },
    call: async () => { throw new Error("webui.call"); },
  });
  expect(await context.__runicBridge.request('{"kind":"hello"}')).toBe('{"ok":true}');
  expect(requests).toEqual([["__runicViews", '{"kind":"hello"}']]);
  expect(allowed).toBe(true);
});

test("runic-desktop-views.js passes a failed request through", async () => {
  const context = loadDesktop(fakeDesktop(), { isConnected: () => true, call: async () => { throw new Error("closed"); } });
  await expect(context.__runicBridge.request("{}")).rejects.toThrow("closed");
});

test("runic-desktop-views.js delivers pushes to its listeners until they unsubscribe", () => {
  const views = fakeDesktop();
  const context = loadDesktop(views, { isConnected: () => true, call: async () => "" });
  const first = [];
  const second = [];
  const stopFirst = context.__runicBridge.onMessage(message => first.push(message));
  context.__runicBridge.onMessage(message => second.push(message));
  views.push('{"kind":"state","route":"a","revision":1}');
  views.push('{"kind":"frame","route":"a","baseRevision":1,"revision":2,"changes":[]}');
  stopFirst();
  views.push('{"kind":"state","route":"a","revision":3}');
  expect(first).toEqual(['{"kind":"state","route":"a","revision":1}', '{"kind":"frame","route":"a","baseRevision":1,"revision":2,"changes":[]}']);
  expect(second.length).toBe(3);
  expect(views.messageListeners.size).toBe(1);
});

test("runic-desktop-views.js without __runicDesktopViews has no pushes and reports webui's state", () => {
  const context = loadDesktop(undefined, { isConnected: () => true, call: async () => "" });
  const stop = context.__runicBridge.onMessage(() => {});
  expect(typeof stop).toBe("function");
  stop();
  const states = [];
  context.__runicBridge.onConnectionChange(connected => states.push(connected)).call();
  expect(states).toEqual([true]);
  expect(context.__runicBridge.isConnected()).toBe(true);
});

// A page learns that the host ended when the WebSocket drops, not on its next
// call (#406). webui.js reports transitions; the script adds the current state.
test("runic-desktop-views.js reports the connection at once and on each transition", () => {
  const views = fakeDesktop({ connected: false });
  const context = loadDesktop(views, { isConnected: () => true, allowNavigation: () => { throw new Error("navigation"); }, call: async () => "" });
  const bridge = context.__runicBridge;
  expect(bridge.isConnected()).toBe(false);

  const states = [];
  const stop = bridge.onConnectionChange(connected => states.push(connected));
  expect(states).toEqual([false]);
  views.transition(true);
  expect(bridge.isConnected()).toBe(true);
  // A duplicate report reaches no listener twice.
  views.transition(true, { repeat: true });
  views.transition(false);
  views.transition(true);
  expect(states).toEqual([false, true, false, true]);

  const late = [];
  const stopLate = bridge.onConnectionChange(connected => late.push(connected));
  expect(late).toEqual([true]);
  stop();
  views.transition(false);
  expect(states).toEqual([false, true, false, true]);
  expect(late).toEqual([true, false]);
  stopLate();
  expect(views.connectionListeners.size).toBe(0);
  expect(context.intervals).toEqual([]);
});

test("runic-desktop-views.js logs a failing listener and goes on", () => {
  const views = fakeDesktop();
  const errors = [];
  const context = loadDesktop(views, { isConnected: () => true, call: async () => "" });
  context.console = { error: (...args) => errors.push(args[0]) };
  const states = [];
  context.__runicBridge.onConnectionChange(() => { throw new Error("listener"); });
  context.__runicBridge.onConnectionChange(connected => states.push(connected));
  views.transition(false);
  expect(states).toEqual([true, false]);
  expect(errors).toEqual(["Runic connection listener failed", "Runic connection listener failed"]);
});

// ---------------------------------------------------------------------------
// CS-WebUI: requests on the __runicViews binding, broadcasts as raw data, and
// targeted pushes through the mailbox.

const receiveRequest = '{"kind":"receive"}';
const lostMessage = "The CS-WebUI connection was lost before the call completed.";
const noHello = '{"ok":false,"error":{"kind":"rejected","reason":"no-hello","message":"No hello."}}';

// A WebUI whose calls the test answers. Every call is recorded with its
// arguments and settles only when the test resolves or rejects it.
function fakeCsWebUi() {
  const webui = {
    online: true,
    calls: [],
    allowed: 0,
    isConnected: () => webui.online,
    allowNavigation: status => { if (status) webui.allowed++; },
    call: (name, ...args) => new Promise((resolve, reject) => {
      webui.calls.push({ name, args, message: args[0], resolve, reject });
    }),
    // The calls not answered yet with the given message.
    open: message => webui.calls.filter(call => call.message === message && !call.done),
    answer(call, reply) { call.done = true; call.resolve(reply); },
    receives: () => webui.calls.filter(call => call.message === receiveRequest),
  };
  return webui;
}

function loadCsWebUi(webui, globals) {
  const context = load(csWebUi, globals);
  context.webui = webui;
  return context;
}

// WebUI assigns each concurrent call its own event slot since webui#721, which
// CsWebUi 2.5.0-beta.4.7 and later bundle, so the client sends every request at once.
test("runic-cswebui.js sends concurrent requests without waiting for replies", async () => {
  const webui = fakeCsWebUi();
  const bridge = loadCsWebUi(webui).__runicBridge;
  const requests = ["a", "b", "c"].map(route => bridge.request(`{"kind":"snapshot","route":"${route}"}`));
  expect(webui.calls.map(call => [call.name, ...call.args])).toEqual([
    ["__runicViews", '{"kind":"snapshot","route":"a"}'],
    ["__runicViews", '{"kind":"snapshot","route":"b"}'],
    ["__runicViews", '{"kind":"snapshot","route":"c"}'],
  ]);
  expect(webui.allowed).toBe(3);
  webui.answer(webui.calls[2], "c");
  webui.answer(webui.calls[0], "a");
  webui.answer(webui.calls[1], "b");
  expect(await Promise.all(requests)).toEqual(["a", "b", "c"]);
});

test("runic-cswebui.js rejects a request that fails to start and passes failures through", async () => {
  const sent = [];
  const context = loadCsWebUi({
    isConnected: () => true,
    call: (name, message) => {
      sent.push(message);
      if (message === "throws") throw new TypeError("WebUI failed");
      return message === "missing" ? Promise.reject(new ReferenceError("No binding")) : Promise.resolve(`${message}-reply`);
    },
  });
  const thrown = context.__runicBridge.request("throws");
  const failed = context.__runicBridge.request("missing");
  const next = context.__runicBridge.request("snapshot");
  await expect(thrown).rejects.toThrow("WebUI failed");
  await expect(failed).rejects.toThrow("No binding");
  expect(await next).toBe("snapshot-reply");
  expect(sent).toEqual(["throws", "missing", "snapshot"]);
});

test("runic-cswebui.js does not hold requests behind an unanswered one", async () => {
  const context = loadCsWebUi({
    isConnected: () => true,
    call: (name, message) => message === "unanswered" ? new Promise(() => {}) : Promise.resolve(message),
  });
  void context.__runicBridge.request("unanswered");
  expect(await context.__runicBridge.request("snapshot")).toBe("snapshot");
});

// WebUI never settles calls in flight when its WebSocket closes, so each would
// wait forever: a command's pending state, an operation start, a receive.
test("runic-cswebui.js rejects every request in flight when the connection is lost", async () => {
  const webui = fakeCsWebUi();
  const bridge = loadCsWebUi(webui).__runicBridge;
  const inFlight = ["set", "start", "watch"].map(kind => outcome(bridge.request(`{"kind":"${kind}"}`)));

  webui.online = false;
  expect(await Promise.all(inFlight)).toEqual([{ error: lostMessage }, { error: lostMessage }, { error: lostMessage }]);

  // A request sent after the loss belongs to the next connection: WebUI holds
  // it until the new connection is ready.
  const reconnecting = outcome(bridge.request('{"kind":"hello"}'));
  await settle();
  webui.online = true;
  await poll();
  webui.answer(webui.open('{"kind":"hello"}')[0], "answered");
  expect(await reconnecting).toEqual({ value: "answered" });
});

test("runic-cswebui.js stops watching the connection once every request settled", async () => {
  const intervals = new Set();
  const context = loadCsWebUi({ isConnected: () => true, call: (name, message) => Promise.resolve(message) }, {
    setInterval: (callback, milliseconds) => { const id = setInterval(callback, milliseconds); intervals.add(id); return id; },
    clearInterval: id => { intervals.delete(id); clearInterval(id); },
  });
  const replies = await Promise.all(Array.from({ length: 50 }, (_, index) => context.__runicBridge.request(`${index}`)));
  expect(replies.length).toBe(50);
  expect(intervals.size).toBe(1);
  await poll();
  expect(intervals.size).toBe(0);
});

// A hidden window throttles timers, so the poll can miss a loss for a while.
test("runic-cswebui.js sends a request made after an unobserved loss on the next connection", async () => {
  const webui = fakeCsWebUi();
  const bridge = loadCsWebUi(webui).__runicBridge;
  const before = outcome(bridge.request("before"));
  await settle();
  webui.online = false;
  // Sent before the poll runs: the send itself observes the loss.
  const after = outcome(bridge.request("after"));
  expect(await before).toEqual({ error: lostMessage });
  webui.online = true;
  await poll();
  webui.answer(webui.open("after")[0], "answered");
  expect(await after).toEqual({ value: "answered" });
});

test("runic-cswebui.js watches the connection while a request is unsettled", async () => {
  const webui = fakeCsWebUi();
  const bridge = loadCsWebUi(webui).__runicBridge;
  const lost = outcome(bridge.request("lost"));
  await settle();
  webui.online = false;
  await poll();
  // Without a listener, the poll still observes the loss.
  expect(await lost).toEqual({ error: lostMessage });
});

// A page learns that the host ended when the WebSocket drops, not on its next
// call (#406). The script reports WebUI's own connection state at once and after
// each change, reads it without the Bridge's navigation side effect, and stops
// polling once nothing listens.
test("runic-cswebui.js reports connection changes as they happen", async () => {
  let online = true;
  let allowed = 0;
  const intervals = new Set();
  const context = loadCsWebUi({ isConnected: () => online, allowNavigation: () => { allowed++; }, call: async () => "" }, {
    setInterval: (callback, milliseconds) => { const id = setInterval(callback, milliseconds); intervals.add(id); return id; },
    clearInterval: id => { intervals.delete(id); clearInterval(id); },
  });
  const bridge = context.__runicBridge;

  const states = [];
  const stop = bridge.onConnectionChange(connected => states.push(connected));
  expect(states).toEqual([true]);
  online = false;
  await poll();
  expect(states).toEqual([true, false]);
  online = true;
  await poll();
  expect(states).toEqual([true, false, true]);
  expect(allowed).toBe(0);
  stop();
  expect(intervals.size).toBe(0);
  online = false;
  await poll();
  expect(states).toEqual([true, false, true]);
});

// A page's connection indicator subscribes before WebUI connects; the Views runtime subscribes once it has seen
// isConnected() true, before the next poll. It must get the current state, and the first listener the change.
test("runic-cswebui.js gives a later listener the current state, not the last poll's", () => {
  let online = false;
  const context = loadCsWebUi({ isConnected: () => online, allowNavigation: () => {}, call: () => new Promise(() => {}) },
    { setInterval: () => 1, clearInterval: () => {} });
  const bridge = context.__runicBridge;
  const first = [];
  const later = [];
  bridge.onConnectionChange(connected => first.push(connected));
  online = true;
  bridge.onConnectionChange(connected => later.push(connected));
  expect(first).toEqual([false, true]);
  expect(later).toEqual([true]);
});

// D040: .NET broadcasts Views pushes as WebUI raw data (the UTF-8 JSON), which
// the script decodes instead of evaluating, so a policy without 'unsafe-eval' works.
test("runic-cswebui.js delivers raw pushes to every message listener", () => {
  const errors = [];
  const context = loadCsWebUi({ isConnected: () => false, call: async () => "" });
  context.console = { error: (...args) => errors.push(args[0]) };
  const raw = json => context.__runicViewsPush(new TextEncoder().encode(json));
  raw('{"kind":"state","route":"dropped","revision":1}');

  const first = [];
  const second = [];
  context.__runicBridge.onMessage(() => { throw new Error("listener"); });
  const stop = context.__runicBridge.onMessage(message => first.push(message));
  context.__runicBridge.onMessage(message => second.push(message));
  const message = '{"kind":"state","route":"counter","revision":2,"state":{"text":"Grüße \\u2028","__proto__":{"polluted":true}}}';
  raw(message);
  stop();
  raw('{"kind":"failure","route":"counter","revision":3,"error":{}}');

  expect(first).toEqual([message]);
  expect(second).toEqual([message, '{"kind":"failure","route":"counter","revision":3,"error":{}}']);
  expect(errors).toEqual(["Runic message listener failed", "Runic message listener failed"]);
  expect({}.polluted).toBeUndefined();
  expect(typeof context.__runicContinueNavigation).toBe("function");
});

// The mailbox: CS-WebUI cannot push to one connection, so the script drains the
// connection's targeted pushes with one pending receive.
test("runic-cswebui.js drains the mailbox in order with one pending receive", async () => {
  const webui = fakeCsWebUi();
  const bridge = loadCsWebUi(webui).__runicBridge;
  const received = [];
  const stop = bridge.onMessage(message => received.push(message));
  const stopOther = bridge.onMessage(() => {});
  expect(webui.receives().length).toBe(1);
  expect(webui.receives()[0].name).toBe("__runicViews");
  // A request does not start a second receive.
  const hello = bridge.request('{"kind":"hello"}');
  webui.answer(webui.open('{"kind":"hello"}')[0], '{"ok":true}');
  await hello;
  await settle();
  expect(webui.open(receiveRequest).length).toBe(1);

  webui.answer(webui.open(receiveRequest)[0], JSON.stringify({ ok: true, messages: [
    { kind: "operation", route: "a", operation: "o", status: { kind: "running" } },
    { kind: "interaction", route: "a", text: "Grüße" },
  ] }));
  await settle();
  expect(received).toEqual([
    '{"kind":"operation","route":"a","operation":"o","status":{"kind":"running"}}',
    '{"kind":"interaction","route":"a","text":"Grüße"}',
  ]);
  // The next receive follows at once; an empty reply sends another.
  expect(webui.receives().length).toBe(2);
  webui.answer(webui.open(receiveRequest)[0], '{"ok":true,"messages":[]}');
  await settle();
  expect(webui.receives().length).toBe(3);
  expect(webui.open(receiveRequest).length).toBe(1);

  // Without listeners, the pending receive's messages are dropped and no other follows.
  stop();
  stopOther();
  webui.answer(webui.open(receiveRequest)[0], '{"ok":true,"messages":[{"kind":"operation"}]}');
  await settle();
  expect(received.length).toBe(2);
  expect(webui.receives().length).toBe(3);
});

test("runic-cswebui.js pauses the mailbox until a request settles after no-hello", async () => {
  const webui = fakeCsWebUi();
  const bridge = loadCsWebUi(webui).__runicBridge;
  const received = [];
  bridge.onMessage(message => received.push(message));
  webui.answer(webui.open(receiveRequest)[0], noHello);
  await settle();
  await poll();
  expect(webui.receives().length).toBe(1);

  // The page's hello settles: the loop resumes.
  const hello = bridge.request('{"kind":"hello"}');
  webui.answer(webui.open('{"kind":"hello"}')[0], '{"ok":true,"protocol":4}');
  await hello;
  await settle();
  expect(webui.receives().length).toBe(2);
  webui.answer(webui.open(receiveRequest)[0], '{"ok":true,"messages":[{"kind":"operation","route":"a"}]}');
  await settle();
  expect(received).toEqual(['{"kind":"operation","route":"a"}']);
});

// Views protocol 4 lets a page send its first requests with the hello (specs/application/README.md, "Handshake"): the
// script sends them at once, and the producer holds the open until the hello completed, even when WebUI's threads
// deliver the open first.
test("runic-cswebui.js sends an open with its hello and resumes the mailbox when the first of them settles", async () => {
  const webui = fakeCsWebUi();
  const bridge = loadCsWebUi(webui).__runicBridge;
  bridge.onMessage(() => {});
  webui.answer(webui.open(receiveRequest)[0], noHello);
  await settle();
  const hello = bridge.request('{"kind":"hello"}');
  const open = bridge.request('{"kind":"open","route":"a"}');
  expect(webui.calls.map(call => call.message).slice(-2)).toEqual(['{"kind":"hello"}', '{"kind":"open","route":"a"}']);
  expect(webui.receives().length).toBe(1);
  webui.answer(webui.open('{"kind":"hello"}')[0], '{"ok":true,"protocol":4}');
  await hello;
  await settle();
  expect(webui.receives().length).toBe(2);
  webui.answer(webui.open('{"kind":"open","route":"a"}')[0], '{"ok":true}');
  expect(await open).toBe('{"ok":true}');
  await settle();
  expect(webui.open(receiveRequest).length).toBe(1);
});

test("runic-cswebui.js resumes the mailbox at once when the hello settled before its no-hello arrived", async () => {
  const webui = fakeCsWebUi();
  const bridge = loadCsWebUi(webui).__runicBridge;
  bridge.onMessage(() => {});
  // The receive and the hello race; the hello's reply arrives first.
  const hello = bridge.request('{"kind":"hello"}');
  webui.answer(webui.open('{"kind":"hello"}')[0], '{"ok":true,"protocol":4}');
  await hello;
  await settle();
  webui.answer(webui.open(receiveRequest)[0], noHello);
  await settle();
  expect(webui.receives().length).toBe(2);
  expect(webui.open(receiveRequest).length).toBe(1);
});

test("runic-cswebui.js pauses the mailbox after a failed or unreadable reply", async () => {
  for (const reply of ['{"ok":false,"error":{"kind":"released","message":"This View was released."}}', "", "not json", '{"ok":true}']) {
    const webui = fakeCsWebUi();
    const bridge = loadCsWebUi(webui).__runicBridge;
    bridge.onMessage(() => {});
    webui.answer(webui.open(receiveRequest)[0], reply);
    await settle();
    expect(webui.receives().length, reply).toBe(1);
    // A failed request settles too.
    const request = outcome(bridge.request("{}"));
    webui.calls.find(call => call.message === "{}").reject(new Error("failed"));
    await request;
    await settle();
    expect(webui.receives().length, reply).toBe(2);
  }
});

// WebUI can replace its WebSocket between two polls. The new connection has no
// hello, so its first receive fails with no-hello after an earlier one succeeded.
test("runic-cswebui.js reports a connection the poll missed when a receive finds no hello", async () => {
  const webui = fakeCsWebUi();
  const bridge = loadCsWebUi(webui).__runicBridge;
  const states = [];
  const hellos = [];
  bridge.onConnectionChange(connected => {
    states.push(connected);
    if (connected) hellos.push(bridge.request('{"kind":"hello"}'));
  });
  bridge.onMessage(() => {});
  webui.answer(webui.open('{"kind":"hello"}')[0], '{"ok":true}');
  await settle();
  webui.answer(webui.open(receiveRequest)[0], '{"ok":true,"messages":[]}');
  await settle();
  const inFlight = outcome(bridge.request('{"kind":"set"}'));

  webui.answer(webui.open(receiveRequest)[0], noHello);
  await settle();
  expect(states).toEqual([true, false, true]);
  // WebUI will not answer a call sent on the replaced WebSocket.
  expect(await inFlight).toEqual({ error: lostMessage });
  // The loop waits for the new connection's hello.
  expect(hellos.length).toBe(2);
  expect(webui.receives().length).toBe(2);
  webui.answer(webui.open('{"kind":"hello"}')[0], '{"ok":true}');
  await settle();
  expect(webui.receives().length).toBe(3);
  expect(webui.open(receiveRequest).length).toBe(1);
});

// A receive in flight when the WebSocket closes is never answered; the loss
// rejects it, and the next connection starts its own loop.
test("runic-cswebui.js restarts the mailbox on the next connection", async () => {
  const webui = fakeCsWebUi();
  const bridge = loadCsWebUi(webui).__runicBridge;
  const states = [];
  bridge.onConnectionChange(connected => states.push(connected));
  bridge.onMessage(() => {});
  webui.answer(webui.open(receiveRequest)[0], '{"ok":true,"messages":[]}');
  await settle();
  expect(webui.open(receiveRequest).length).toBe(1);

  webui.online = false;
  await poll();
  expect(states).toEqual([true, false]);
  expect(webui.receives().length).toBe(2);
  webui.online = true;
  await poll();
  expect(states).toEqual([true, false, true]);
  // The abandoned receive stays unanswered; the new connection has its own.
  expect(webui.receives().length).toBe(3);
  webui.answer(webui.calls.at(-1), noHello);
  await settle();
  const hello = bridge.request('{"kind":"hello"}');
  webui.answer(webui.open('{"kind":"hello"}')[0], '{"ok":true}');
  await hello;
  await settle();
  expect(webui.receives().length).toBe(4);
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
  load(csWebUi, {
    document: { documentElement },
    MutationObserver: class { constructor(callback) { observe = callback; } observe() {} },
  });
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
  const context = load(csWebUi, {
    location, navigation: { addEventListener: (type, listener) => { if (type === "navigate") navigate = listener; } },
    webui: { allowNavigation: status => { allowed = status; } },
  });
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

// ---------------------------------------------------------------------------
// CS-WebUI connection events. webui.js defines the WebuiBridge class and
// creates window.webui when the document has loaded; WebUI reports CONNECTED
// once the token is accepted and DISCONNECTED when the WebSocket closes, each
// only when it differs from the last event it reported, to its one callback.

function fakeWebuiBridge() {
  class WebuiBridge {
    #eventsCallback = null;
    #lastEvent = -1;
    event = { CONNECTED: 0, DISCONNECTED: 1 };
    online = false;
    calls = [];
    isConnected() { return this.online; }
    allowNavigation() {}
    call(name, ...args) {
      return new Promise((resolve, reject) => { this.calls.push({ name, args, message: args[0], resolve, reject }); });
    }
    setEventCallback(callback) { this.#eventsCallback = callback; }
    // WebUI's #userEventCallback.
    report(event) {
      if (this.#eventsCallback && event != this.#lastEvent) {
        this.#lastEvent = event;
        this.#eventsCallback(event);
      }
    }
    connect() { this.online = true; this.report(this.event.CONNECTED); }
    disconnect() { this.online = false; this.report(this.event.DISCONNECTED); }
    open(message) { return this.calls.filter(call => call.message === message && !call.done); }
    answer(call, reply) { call.done = true; call.resolve(reply); }
  }
  return WebuiBridge;
}

// Loads the script as a page does: after webui.js, before the document has loaded.
function loadWebuiPage() {
  const WebuiBridge = fakeWebuiBridge();
  const loaded = [];
  const intervals = [];
  const context = load(csWebUi, {
    WebuiBridge,
    document: { addEventListener: (type, listener) => { if (type === "DOMContentLoaded") loaded.push(listener); } },
    setInterval: (...args) => { intervals.push(args); return intervals.length; },
  });
  context.intervals = intervals;
  // webui.js's own listener runs first and creates the instance.
  context.domContentLoaded = () => {
    context.webui = new WebuiBridge();
    for (const listener of loaded) listener();
    return context.webui;
  };
  return context;
}

test("runic-cswebui.js sends hello as soon as WebUI reports the connection, without a poll", () => {
  const context = loadWebuiPage();
  const bridge = context.__runicBridge;
  // A module script subscribes before the document has loaded, so before window.webui exists.
  const states = [];
  bridge.onConnectionChange(connected => {
    states.push(connected);
    if (connected) void bridge.request('{"kind":"hello"}');
  });
  const webui = context.domContentLoaded();
  expect(states).toEqual([false]);
  webui.connect();
  // The hello goes out within WebUI's own event, not up to 250 ms later.
  expect(states).toEqual([false, true]);
  expect(webui.calls.map(call => call.message)).toEqual(['{"kind":"hello"}']);
  webui.disconnect();
  webui.connect();
  expect(states).toEqual([false, true, false, true]);
  expect(webui.calls.length).toBe(2);
  expect(context.intervals).toEqual([]);
});

test("runic-cswebui.js keeps the application's own event callback", () => {
  const context = loadWebuiPage();
  const webui = context.domContentLoaded();
  const application = [];
  // Before the Bridge watches, WebUI calls the application's callback itself.
  webui.setEventCallback(function (event) { application.push(["first", event, this === webui]); });
  webui.connect();
  const states = [];
  context.__runicBridge.onConnectionChange(connected => states.push(connected));
  webui.disconnect();
  // The application replaces its callback while the Bridge's is installed.
  webui.setEventCallback(function (event) { application.push(["second", event, this === webui]); });
  webui.connect();
  webui.setEventCallback(null);
  webui.disconnect();
  expect(application).toEqual([["first", 0, true], ["first", 1, true], ["second", 0, true]]);
  expect(states).toEqual([true, false, true, false]);
  expect(context.intervals).toEqual([]);
});

test("runic-cswebui.js gives the application's callback each event even when a listener throws", () => {
  const context = loadWebuiPage();
  const webui = context.domContentLoaded();
  const errors = [];
  context.console = { error: (...args) => errors.push(args[0]) };
  const application = [];
  webui.setEventCallback(event => application.push(event));
  context.__runicBridge.onConnectionChange(connected => { if (connected) throw new Error("listener"); });
  webui.connect();
  expect(application).toEqual([0]);
  expect(errors).toEqual(["Runic connection listener failed"]);
});

test("runic-cswebui.js rejects calls in flight when WebUI reports the close", async () => {
  const context = loadWebuiPage();
  const bridge = context.__runicBridge;
  const webui = context.domContentLoaded();
  webui.connect();
  const received = [];
  bridge.onMessage(message => received.push(message));
  const inFlight = outcome(bridge.request('{"kind":"set"}'));
  expect(webui.open(receiveRequest).length).toBe(1);
  webui.disconnect();
  expect(await inFlight).toEqual({ error: lostMessage });
  // The new connection starts its own receive loop at once.
  webui.connect();
  expect(webui.open(receiveRequest).length).toBe(2);
  webui.answer(webui.calls.at(-1), '{"ok":true,"messages":[{"kind":"operation","route":"a"}]}');
  await settle();
  expect(received).toEqual(['{"kind":"operation","route":"a"}']);
  expect(context.intervals).toEqual([]);
});

// WebUI reports an event only when it differs from the last one it reported,
// and records none while no callback is set. An application that cleared its
// callback can leave a stale last event, so the first close after the Bridge
// installs its own goes unreported; the next CONNECTED shows the new connection.
test("runic-cswebui.js reports a new connection when WebUI reports CONNECTED without the close", async () => {
  const context = loadWebuiPage();
  const bridge = context.__runicBridge;
  const webui = context.domContentLoaded();
  webui.setEventCallback(() => {});
  webui.connect();
  webui.disconnect();
  webui.setEventCallback(null);
  webui.connect();
  const states = [];
  bridge.onConnectionChange(connected => states.push(connected));
  const inFlight = outcome(bridge.request('{"kind":"set"}'));
  webui.disconnect();
  expect(states).toEqual([true]);
  webui.connect();
  expect(states).toEqual([true, false, true]);
  expect(await inFlight).toEqual({ error: lostMessage });
});

test("runic-cswebui.js polls when the page created window.webui before the script ran", async () => {
  const WebuiBridge = fakeWebuiBridge();
  const intervals = [];
  const webui = new WebuiBridge();
  webui.online = true;
  const context = load(csWebUi, {
    WebuiBridge, webui,
    setInterval: (callback, milliseconds) => { intervals.push(milliseconds); return setInterval(callback, milliseconds); },
  });
  const application = [];
  webui.setEventCallback(event => application.push(event));
  const states = [];
  const stop = context.__runicBridge.onConnectionChange(connected => states.push(connected));
  expect(intervals).toEqual([250]);
  webui.disconnect();
  await poll();
  expect(states).toEqual([true, false]);
  expect(application).toEqual([1]);
  stop();
});

// The mailbox: .NET writes each queued message into the receive reply as it
// was queued (BridgeMessages.Receive). The script slices the texts out, so
// the page parses each message once, and they keep their bytes.
test("runic-cswebui.js delivers the mailbox messages as .NET wrote them, without parsing the reply", async () => {
  const webui = fakeCsWebUi();
  let parsed = 0;
  let serialized = 0;
  const counting = {
    parse: (...args) => { parsed++; return JSON.parse(...args); },
    stringify: (...args) => { serialized++; return JSON.stringify(...args); },
  };
  const context = load(csWebUi, { JSON: counting });
  context.webui = webui;
  const received = [];
  context.__runicBridge.onMessage(message => received.push(message));
  const messages = [
    '{"kind":"interaction","route":"a","text":"Gr\\u00FC\\u00DFe \\u2028 } ] { [ \\"quoted\\" \\\\"}',
    '{"kind":"operation","route":"a","operation":"o","status":{"kind":"running","steps":[[1,{"a":[]}],{}]}}',
    '{"kind":"interaction","route":"b","text":"\\\\"}',
    '{}',
  ];
  webui.answer(webui.open(receiveRequest)[0], `{"ok":true,"messages":[${messages.join(",")}]}`);
  await settle();
  expect(received).toEqual(messages);
  expect([parsed, serialized]).toEqual([0, 0]);
  for (const message of received) JSON.parse(message);

  // Another layout of a successful reply is parsed and each message serialized, as before.
  webui.answer(webui.open(receiveRequest)[0], '{"ok":true, "messages":[ {"kind":"operation","route":"c"} ]}');
  await settle();
  expect(received.at(-1)).toBe('{"kind":"operation","route":"c"}');
  expect(webui.open(receiveRequest).length).toBe(1);
});

test("runic-cswebui.js pauses the mailbox after a reply that only looks like a receive reply", async () => {
  for (const reply of [
    '{"ok":true,"messages":[{"a":1},]}', '{"ok":true,"messages":[{"a":"}]}', '{"ok":true,"messages":[{"a":1}{"b":2}]}',
    '{"ok":true,"messages":[{"a":1}]]}', '{"ok":true,"messages":[1]}x]}',
  ]) {
    const webui = fakeCsWebUi();
    const received = [];
    loadCsWebUi(webui).__runicBridge.onMessage(message => received.push(message));
    webui.answer(webui.open(receiveRequest)[0], reply);
    await settle();
    expect(received, reply).toEqual([]);
    expect(webui.receives().length, reply).toBe(1);
  }
});

// Examples serve their own copy; each must match the package's script.
test("example copies of runic-cswebui.js match the package script", () => {
  const expected = readFileSync(resolve(root, csWebUi), "utf8");
  for (const example of ["notes-reactive-views", "notes-view-first"])
    for (const frontend of ["Frontend", "Angular"])
      expect(readFileSync(resolve(root, `examples/${example}/${frontend}/public/runic-cswebui.js`), "utf8"), `${example}/${frontend}`)
        .toBe(expected);
});
