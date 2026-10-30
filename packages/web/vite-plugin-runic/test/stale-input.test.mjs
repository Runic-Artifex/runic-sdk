import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { promisify } from "node:util";
import test from "node:test";
import { runic } from "../dist/index.js";
import { burstQuietPeriodMs, installStaleInputWarning } from "../dist/stale-input.js";

const execFile = promisify(execFileCallback);
const diagnosticsKey = Symbol.for("runic.views.diagnostics");
const warningKey = Symbol.for("runic.vite.staleInputWarning");

// A minimal DOM on `host`: text fields with a configurable `value` accessor on their
// prototypes, a document with capture listeners and a focused element, and a user who types
// by changing the field's value without the property setter, as a browser does.
// Self-contained, because the client test injects its source into a separate process.
function createFakeDom(host) {
  const raw = new WeakMap();
  class Field {
    constructor(tagName, attributes = {}) {
      this.tagName = tagName;
      this.id = attributes.id ?? "";
      this.attributes = attributes;
      raw.set(this, "");
    }
    getAttribute(name) { return this.attributes[name] ?? null; }
  }
  const valueDescriptor = {
    configurable: true,
    enumerable: true,
    get() { return raw.get(this); },
    set(value) { raw.set(this, value === null ? "" : String(value)); },
  };
  class HTMLInputElement extends Field { constructor(attributes) { super("INPUT", attributes); } }
  class HTMLTextAreaElement extends Field { constructor(attributes) { super("TEXTAREA", attributes); } }
  Object.defineProperty(HTMLInputElement.prototype, "value", valueDescriptor);
  Object.defineProperty(HTMLTextAreaElement.prototype, "value", valueDescriptor);
  const listeners = [];
  const document = {
    activeElement: null,
    addEventListener(type, listener, capture) { listeners.push({ type, listener, capture }); },
  };
  const dispatch = (type, target) => {
    for (const entry of listeners) if (entry.type === type) entry.listener({ type, target });
  };
  const warnings = [];
  Object.assign(host, {
    HTMLInputElement, HTMLTextAreaElement, document,
    console: { warn: (message) => warnings.push(message) },
  });
  return {
    warnings,
    listeners,
    focus(element) { document.activeElement = element; },
    blur(element) { document.activeElement = null; dispatch("focusout", element); },
    type(element, value) {
      dispatch("beforeinput", element);
      raw.set(element, value);
      dispatch("input", element);
    },
  };
}

// A host Bridge whose requests stay in flight until `settle()` is called.
function createBridge() {
  const pending = [];
  return {
    isConnected: () => true,
    request() { return new Promise((resolve) => pending.push(resolve)); },
    settle() { for (const resolve of pending.splice(0)) resolve("{}"); },
  };
}

function setup({ bridge = createBridge() } = {}) {
  let time = 1_000;
  const host = {};
  const dom = createFakeDom(host);
  host.__runicBridge = bridge;
  const diagnostics = [];
  host[diagnosticsKey] = new Set([(diagnostic) => diagnostics.push(diagnostic)]);
  const installed = installStaleInputWarning(host, { now: () => time });
  return { host, dom, bridge, diagnostics, installed, advance: (ms) => { time += ms; } };
}

test("warns once per element when script writes an older burst value during a request", async () => {
  const { host, dom, diagnostics, installed } = setup();
  assert.equal(installed, true);
  const input = new host.HTMLInputElement({ id: "email" });
  dom.focus(input);
  dom.type(input, "a");
  const request = host.__runicBridge.request("{}");
  dom.type(input, "ab");
  input.value = "a";
  assert.equal(input.value, "a", "the write itself still happens");
  assert.equal(diagnostics.length, 1);
  assert.equal(diagnostics[0].kind, "error");
  assert.equal(diagnostics[0].code, "stale-input");
  assert.match(diagnostics[0].message, /^A script set the focused <input id="email"> to a value it had before/u);
  assert.match(diagnostics[0].message, /injectTextField/u);
  assert.match(diagnostics[0].message, /runic\(\{ staleInputWarning: false \}\)/u);
  assert.deepEqual(dom.warnings, [diagnostics[0].message]);

  dom.type(input, "abc");
  input.value = "ab";
  assert.equal(diagnostics.length, 1, "once per element");

  const other = new host.HTMLTextAreaElement({ name: "notes" });
  dom.focus(other);
  dom.type(other, "x");
  dom.type(other, "xy");
  other.value = "";
  assert.equal(diagnostics.length, 2, "the value before the first edit is part of the burst");
  assert.match(diagnostics[1].message, /<textarea name="notes">/u);
  assert.doesNotMatch(diagnostics[1].message, /xy/u, "never the typed value");
  host.__runicBridge.settle();
  await request;
});

test("does not warn without a request in flight", async () => {
  const { host, dom, diagnostics } = setup();
  const input = new host.HTMLInputElement();
  dom.focus(input);
  dom.type(input, "a");
  dom.type(input, "ab");
  input.value = "a";
  const request = host.__runicBridge.request("{}");
  host.__runicBridge.settle();
  await request;
  await Promise.resolve();
  input.value = "ab";
  dom.type(input, "abc");
  input.value = "ab";
  assert.equal(diagnostics.length, 0);
});

test("does not warn for the latest, a new, or an unfocused value, or after the burst", () => {
  const { host, dom, diagnostics, advance } = setup();
  const input = new host.HTMLInputElement();
  void host.__runicBridge.request("{}");
  dom.focus(input);
  dom.type(input, "a");
  dom.type(input, "ab ");
  input.value = "ab";
  input.value = "ab ";
  input.value = "AB";
  assert.equal(diagnostics.length, 0, "the latest and unrelated values are not stale");

  const unfocused = new host.HTMLInputElement();
  dom.focus(unfocused);
  dom.type(unfocused, "q");
  dom.type(unfocused, "qr");
  dom.focus(input);
  unfocused.value = "q";
  assert.equal(diagnostics.length, 0, "not the focused element");

  dom.type(input, "abc");
  advance(burstQuietPeriodMs + 1);
  input.value = "a";
  assert.equal(diagnostics.length, 0, "the burst ended after the quiet period");

  dom.type(input, "xy");
  dom.blur(input);
  dom.focus(input);
  input.value = "a";
  assert.equal(diagnostics.length, 0, "blur ends the burst");
});

test("wraps a Bridge installed or replaced after the client, and skips a read-only one", () => {
  const host = {};
  const dom = createFakeDom(host);
  const diagnostics = [];
  host[diagnosticsKey] = new Set([(diagnostic) => diagnostics.push(diagnostic)]);
  installStaleInputWarning(host);
  const input = new host.HTMLInputElement();
  dom.focus(input);

  host.__runicBridge = Object.freeze(createBridge());
  dom.type(input, "a");
  void host.__runicBridge.request("{}");
  dom.type(input, "ab");
  input.value = "a";
  assert.equal(diagnostics.length, 0, "a frozen Bridge is not observed");

  host.__runicBridge = createBridge();
  dom.type(input, "abc");
  void host.__runicBridge.request("{}");
  input.value = "ab";
  assert.equal(diagnostics.length, 1);
});

test("is turned off by the registered symbol and by the plugin option", () => {
  const host = {};
  const dom = createFakeDom(host);
  host[warningKey] = false;
  assert.equal(installStaleInputWarning(host), false);
  assert.equal(dom.listeners.length, 0);

  const { host: running, dom: runningDom, diagnostics } = setup();
  const input = new running.HTMLInputElement();
  runningDom.focus(input);
  runningDom.type(input, "a");
  void running.__runicBridge.request("{}");
  runningDom.type(input, "ab");
  running[warningKey] = false;
  input.value = "a";
  assert.equal(diagnostics.length, 0, "turned off at runtime");

  const plugin = runic({ staleInputWarning: false });
  const client = plugin.load("\0virtual:runic/client");
  assert.equal(client, 'import "virtual:runic/client-options";\nimport "@runic-artifex/vite-plugin-runic/client";');
  assert.equal(plugin.resolveId("virtual:runic/client-options"), "\0virtual:runic/client-options");
  const flag = {};
  new Function("globalThis", plugin.load("\0virtual:runic/client-options"))(flag);
  assert.equal(flag[warningKey], false);
  assert.deepEqual(flag[Symbol.for("runic.vite.client")], { channel: "hot" });
  const defaults = {};
  new Function("globalThis", runic().load("\0virtual:runic/client-options"))(defaults);
  assert.equal(warningKey in defaults, false, "the warning stays on by default");
  assert.throws(() => runic({ staleInputWarning: "no" }), /staleInputWarning must be true or false/u);
});

test("does nothing without a DOM", () => {
  assert.equal(installStaleInputWarning({}), false);
});

test("the development client installs once across HMR and forwards the warning to the timeline", async () => {
  const harness = String.raw`
import { readFile } from "node:fs/promises";
import { createContext, SourceTextModule } from "node:vm";

const createFakeDom = ${createFakeDom.toString()};
const createBridge = ${createBridge.toString()};
const sent = [];
const hot = { data: {}, send(event, data) { sent.push({ event, data }); }, on() {}, prune() {} };
const context = createContext({ setTimeout, clearTimeout, Promise });
const dom = createFakeDom(context);
context.__runicBridge = createBridge();
context[Symbol.for("runic.vite.client")] = { channel: "hot" };
async function evaluateClient(generation) {
  const modules = new Map();
  const load = async (path, identifier, meta) => {
    const module = new SourceTextModule(await readFile(path, "utf8"), {
      context, identifier, ...(meta ? { initializeImportMeta: meta } : {}),
    });
    modules.set(identifier, module);
    return module;
  };
  await load("dist/diagnostics.js", "diagnostics");
  await load("dist/stale-input.js", "stale-input");
  await load("dist/channel.js", "channel");
  const client = await load("dist/client.js", "client", (meta) => { meta.hot = hot; });
  await client.link((specifier) => modules.get(specifier.replace("./", "").replace(".js", "")));
  await client.evaluate();
}
const setter = () => Object.getOwnPropertyDescriptor(context.HTMLInputElement.prototype, "value").set;
await evaluateClient(1);
const first = setter();
await evaluateClient(2);
const input = new context.HTMLInputElement({ id: "title" });
dom.focus(input);
dom.type(input, "a");
void context.__runicBridge.request("{}");
dom.type(input, "ab");
input.value = "a";
process.stdout.write(JSON.stringify({
  sameSetter: first === setter(),
  warnings: dom.warnings.length,
  sent: sent.filter((entry) => entry.event === "runic:diagnostic"),
}));
`;
  const { stdout } = await execFile(process.execPath, [
    "--experimental-vm-modules", "--input-type=module", "--eval", harness,
  ], { cwd: process.cwd() });
  const result = JSON.parse(stdout);
  assert.equal(result.sameSetter, true, "HMR re-evaluation does not wrap the setter again");
  assert.equal(result.warnings, 1);
  assert.equal(result.sent.length, 1, "one timeline entry: the replaced listener is gone");
  const entry = result.sent[0].data;
  assert.equal(entry.source, "views");
  assert.equal(entry.kind, "error");
  assert.equal(entry.label, "Bridge stale-input");
  assert.equal(entry.detail.code, "stale-input");
  assert.equal(entry.failure.type, "StaleInputWarning");
  assert.match(entry.failure.message, /^A script set the focused <input id="title">/u);
});

// Evaluates the built client as a page would load it without runic(): no client options, and an HMR context only
// when `withHot` (the Angular CLI's development server gives one to prebundled packages).
async function evaluateHttpClient({ status, withHot }) {
  const harness = String.raw`
import { readFile } from "node:fs/promises";
import { createContext, SourceTextModule } from "node:vm";

const createFakeDom = ${createFakeDom.toString()};
const posted = [];
const hotSent = [];
const hot = { data: {}, send(event, data) { hotSent.push(event); }, on() {}, prune() {} };
const context = createContext({ setTimeout, clearTimeout, Promise, TextEncoder });
createFakeDom(context);
context.fetch = async (url, init) => {
  posted.push({ url, method: init.method, type: init.headers["Content-Type"], message: JSON.parse(init.body) });
  // A server answers later than the module evaluates.
  await new Promise((resolve) => setTimeout(resolve, 2));
  return { status: ${status} };
};
const modules = new Map();
const load = async (path, identifier, meta) => {
  const module = new SourceTextModule(await readFile(path, "utf8"), {
    context, identifier, ...(meta ? { initializeImportMeta: meta } : {}),
  });
  modules.set(identifier, module);
  return module;
};
await load("dist/diagnostics.js", "diagnostics");
await load("dist/stale-input.js", "stale-input");
await load("dist/channel.js", "channel");
const client = await load("dist/client.js", "client", (meta) => { if (${withHot}) meta.hot = hot; });
await client.link((specifier) => modules.get(specifier.replace("./", "").replace(".js", "")));
await client.evaluate();
const installedAtLoad = context[Symbol.for("runic.vite.staleInput")] === true;
const listeners = context[Symbol.for("runic.views.diagnostics")];
for (const listener of [...listeners]) {
  listener({ kind: "error", code: "failed", message: "Save failed.", route: "editorSave" });
}
const connection = context[Symbol.for("runic.views.connection")];
connection.state = "connected";
for (const listener of [...connection.listeners]) listener("connected");
await new Promise((resolve) => setTimeout(resolve, 50));
process.stdout.write(JSON.stringify({
  posted, hotSent, installedAtLoad,
  installed: context[Symbol.for("runic.vite.staleInput")] === true,
  listeners: listeners.size, connectionListeners: connection.listeners.size,
}));
`;
  const { stdout } = await execFile(process.execPath, [
    "--experimental-vm-modules", "--input-type=module", "--eval", harness,
  ], { cwd: process.cwd() });
  return JSON.parse(stdout);
}

test("without runic() the client posts to /__runic/diagnostics in order", async () => {
  for (const withHot of [true, false]) {
    const result = await evaluateHttpClient({ status: 204, withHot });
    assert.deepEqual(result.hotSent, [], "no plugin listens on the HMR channel");
    assert.deepEqual(result.posted.map(({ url, method, type }) => [url, method, type]),
      Array(3).fill(["/__runic/diagnostics", "POST", "application/json"]));
    assert.deepEqual(result.posted.map(({ message }) => message.event),
      ["runic:connection", "runic:diagnostic", "runic:connection"]);
    assert.deepEqual(result.posted[0].message.data, { state: "unknown" }, "the first message opens the channel");
    assert.equal(result.posted[1].message.data.label, "editorSave failed");
    assert.equal(result.posted[1].message.data.failure.type, "BridgeError");
    assert.deepEqual(result.posted[2].message.data, { state: "connected" });
    // With an HMR context the warning installs at once, otherwise once the development server answered.
    assert.equal(result.installedAtLoad, withHot);
    assert.equal(result.installed, true);
  }
});

test("the client stops when nothing answers /__runic/diagnostics", async () => {
  const result = await evaluateHttpClient({ status: 404, withHot: false });
  assert.equal(result.posted.length, 1, "one probe, then nothing");
  assert.equal(result.installed, false, "a page without a development server gets no warning");
  assert.equal(result.listeners, 0);
  assert.equal(result.connectionListeners, 0);
});
