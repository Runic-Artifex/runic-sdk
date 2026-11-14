// Runic Inspect under `vite dev` in Chromium: the injected development client learns which ViewModel members an
// element calls, Alt+Shift+X turns select mode on, a selecting click shows the overview without running the page's
// handler, its links open C# locations through /__runic/open, and `describe(element)` returns the same data. A React
// page and a Svelte page, each with a mock Bridge that answers over a MessageChannel, as a host transport does.
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdir, mkdtemp, writeFile, rm, symlink, realpath } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { chromium, webkit } from "playwright-core";
import { createServer } from "vite";
import { runic } from "../dist/index.js";
import { scaled } from "../../../../tests/support/timeouts.mjs";

const here = fileURLToPath(new URL("..", import.meta.url));
const packages = fileURLToPath(new URL("../../", import.meta.url));
const skip = !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;

// WebKit, where installed (CI installs it for this package): JavaScriptCore's stacks and WebKit's events, as in
// WKWebView and WebKitGTK.
function webkitExecutable() {
  if (process.env.PLAYWRIGHT_WEBKIT_EXECUTABLE_PATH) return process.env.PLAYWRIGHT_WEBKIT_EXECUTABLE_PATH;
  try {
    const path = webkit.executablePath();
    return existsSync(path) ? path : undefined;
  } catch {
    return undefined;
  }
}
const engines = {
  chromium: { type: chromium, executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH },
  webkit: { type: webkit, executablePath: webkitExecutable() },
};

// The Bridge: `counter` has count, step, label, a status text reference and canIncrement; `increment` adds step.
const bridge = `
import { connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";
import { configureTranslatedText } from "@runic-artifex/views";

const channel = new MessageChannel();
const listeners = new Set();
const pending = [];
let revision = 1;
const state = { count: 10, step: 1, label: "Clicks", canIncrement: true,
  status: { version: 2, catalog: "app", contractFingerprint: "F00", key: "status.ready", arguments: {}, fallbackText: "Ready" } };
export const received = [];
channel.port2.onmessage = ({ data }) => {
  for (const listener of [...listeners]) listener(data.frame);
  pending.shift()(data.reply);
};
function change(changes) {
  revision++;
  const frame = JSON.stringify({ kind: "frame", route: "counter", baseRevision: revision - 1, revision, changes });
  setTimeout(() => channel.port1.postMessage({ frame, reply: JSON.stringify({ ok: true, revision }) }), 5);
  return new Promise(resolve => pending.push(resolve));
}
globalThis.__runicBridge = {
  isConnected: () => true,
  onConnectionChange(report) { report(true); return () => {}; },
  onMessage(next) { listeners.add(next); return () => { listeners.delete(next); }; },
  request(text) {
    const message = JSON.parse(text);
    const reply = value => Promise.resolve(JSON.stringify(value));
    switch (message.kind) {
      case "hello": return reply({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features: [] });
      case "open": return reply({ ok: true, presentation: "ok", revision, state });
      case "command":
        received.push(message.member);
        state.count += state.step;
        return change([{ field: "count", kind: "set", value: state.count }]);
      case "set":
        received.push("set " + message.member);
        state[message.member] = message.value;
        return change([{ field: message.member, kind: "set", value: message.value }]);
      default: return reply({ ok: true });
    }
  },
};
configureTranslatedText({
  resolve: (reference, locale) => reference.key === "status.ready" ? (locale === "en" ? "Ready to count" : undefined) : undefined,
  getLocale: () => "en",
});
const same = wire => wire;
export const client = await connectView({ contract: "Demo.CounterViewModel:ABC", route: "counter",
  members: { count: wire => bridgeWire.integer(wire, 0, 1000), step: wire => bridgeWire.integer(wire, 0, 100),
    label: same, canIncrement: same, status: same } });
`;

function sourceMap(root) {
  const file = join(root, "CounterViewModel.cs");
  const member = (kind, name, symbol, line, extra = {}) => ({
    id: `Demo.CounterViewModel/${kind}/${name}`, viewModel: "Demo.CounterViewModel", member: name, kind, symbol,
    location: { file, line, column: 5 }, ...extra,
  });
  return {
    file,
    map: {
      format: "runic-bridge-sources/1",
      viewModels: [{ id: "Demo.CounterViewModel", name: "Counter", route: "counter", contract: "Demo.CounterViewModel:ABC",
        pageKinds: ["counter"], symbol: "Demo.CounterViewModel", location: { file, line: 3, column: 14 } }],
      members: [
        member("state", "count", "Demo.CounterViewModel.Count", 5),
        member("state", "step", "Demo.CounterViewModel.Step", 6),
        member("setter", "step", "Demo.CounterViewModel.Step.set", 6),
        member("state", "label", "Demo.CounterViewModel.Label", 7),
        member("state", "status", "Demo.CounterViewModel.Status", 8),
        member("command", "increment", "Demo.CounterViewModel.Increment()", 11, { property: "Demo.CounterViewModel.IncrementCommand" }),
        member("commandState", "canIncrement", "Demo.CounterViewModel.Increment()", 11),
      ],
    },
  };
}

async function link(root, name, target) {
  const path = join(root, "node_modules", ...name.split("/"));
  await mkdir(join(path, ".."), { recursive: true });
  await symlink(await realpath(target), path, "dir");
}

async function withPage(files, plugins, run, engine = engines.chromium) {
  const root = await realpath(await mkdtemp(join(tmpdir(), "runic-inspect-browser-")));
  let server, browser;
  try {
    const { file, map } = sourceMap(root);
    await writeFile(file, "namespace Demo;\n\npublic partial class CounterViewModel\n{\n}\n");
    await writeFile(join(root, "sources.json"), JSON.stringify(map));
    await writeFile(join(root, "bridge.js"), bridge);
    for (const [name, content] of Object.entries(files)) await writeFile(join(root, name), content);
    await link(root, "@runic-artifex/vite-plugin-runic", here);
    await link(root, "@runic-artifex/views", join(packages, "views"));
    await link(root, "@runic-artifex/react", join(packages, "react"));
    await link(root, "@runic-artifex/vue", join(packages, "vue"));
    await link(root, "@runic-artifex/angular", join(packages, "angular"));
    for (const name of ["core", "common", "compiler", "platform-browser"]) {
      await link(root, `@angular/${name}`, join(packages, "angular", "node_modules", "@angular", name));
    }
    await link(root, "react", join(here, "node_modules", "react"));
    await link(root, "react-dom", join(here, "node_modules", "react-dom"));
    await link(root, "svelte", join(here, "node_modules", "svelte"));
    await link(root, "vue", join(here, "node_modules", "vue"));
    server = await createServer({
      root, configFile: false, logLevel: "silent",
      plugins: [...plugins, runic({ sourceMap: "sources.json" })],
      resolve: { dedupe: ["react", "react-dom", "vue"] },
      server: { host: "127.0.0.1", port: 0, fs: { strict: false } },
    });
    await server.listen();
    browser = await engine.type.launch({ executablePath: engine.executablePath, headless: true });
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(String(error)));
    await page.goto(`http://127.0.0.1:${server.httpServer.address().port}`);
    await page.waitForFunction(() => globalThis.inspectTest?.ready === true, null, { timeout: scaled(30_000) });
    await run({ page, root, csharp: file });
    assert.deepEqual(errors, []);
  } finally {
    await browser?.close();
    await server?.close();
    await rm(root, { recursive: true, force: true });
  }
}

/** The 1-based line and column of `needle` in `text`. */
function positionOf(text, needle) {
  const before = text.slice(0, text.indexOf(needle));
  assert.notEqual(text.indexOf(needle), -1, needle);
  const lines = before.split("\n");
  return { line: lines.length, column: lines.at(-1).length + 1 };
}

const describe = (page, selector) => page.evaluate(
  selector => globalThis[Symbol.for("runic.vite.inspect")].describe(document.querySelector(selector)), selector);
const received = page => page.evaluate(() => globalThis.inspectTest.received.slice());

async function center(page, selector) {
  const box = await page.locator(selector).boundingBox();
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

const handler = csharp => ({
  route: "counter", viewModel: "Demo.CounterViewModel", member: "increment", symbol: "Demo.CounterViewModel.Increment()",
  property: "Demo.CounterViewModel.IncrementCommand",
  location: { file: csharp, line: 11, column: 5, editor: "runic", id: "Demo.CounterViewModel/command/increment" },
  canExecute: true,
});

// The shared steps: the command before any click (from the source, #976) or learned by use, select mode, the
// overview card, open in editor. `source` is where the page's handler reaches the client call, when the analysis
// can see it; without it, the command is learned on first click.
async function exercise({ page, root, csharp }, { framework, component, componentFile, source }) {
  let description = await describe(page, "#increment");
  assert.equal(description.component?.framework, framework);
  assert.equal(description.component?.name, component);
  assert.equal(description.component?.location?.file, join(root, componentFile.file));
  assert.equal(description.component?.location?.line, componentFile.line);
  const frontend = source === undefined ? {}
    : { frontend: { file: join(root, source.file), line: source.line, column: source.column, editor: "vite" }, via: source.via };
  if (source === undefined) {
    assert.deepEqual(description.commands, []);
    assert.ok(description.notes.some(note => note.startsWith("No Bridge call from this element yet")), JSON.stringify(description.notes));
  } else {
    // Before any click: the command and its C# handler, from the component's source.
    assert.deepEqual(await received(page), []);
    assert.deepEqual(description.commands, [{ ...handler(csharp), evidence: "source", ...frontend, calls: 0 }]);
    assert.ok(!description.notes.some(note => note.startsWith("No Bridge call from this element yet")), JSON.stringify(description.notes));
    assert.deepEqual(description.viewModels.map(viewModel => viewModel.id), ["Demo.CounterViewModel"]);
  }

  // A normal click runs the app's handler; the overview learns the command from it.
  await page.click("#increment");
  await page.waitForFunction(() => document.querySelector(".count")?.textContent === "11");
  assert.deepEqual(await received(page), ["increment"]);
  await page.waitForFunction(async () => (await globalThis[Symbol.for("runic.vite.inspect")]
    .describe(document.querySelector("#increment"))).calls[0]?.outcome === "ok");
  description = await describe(page, "#increment");
  assert.deepEqual(description.commands, [{ ...handler(csharp), evidence: "call", ...frontend, calls: 1 }]);
  assert.deepEqual(description.viewModels, [{ route: "counter", id: "Demo.CounterViewModel", name: "Counter",
    contract: "Demo.CounterViewModel:ABC", location: { file: csharp, line: 3, column: 14, editor: "runic", id: "Demo.CounterViewModel" } }]);
  assert.equal(description.calls.length, 1);
  assert.equal(description.calls[0].kind, "command");
  assert.equal(description.calls[0].member, "increment");
  assert.equal(typeof description.calls[0].duration, "number");
  assert.ok(description.calls[0].revision >= 2);

  // A state value shown as text, a text reference, and a setter learned from an edit.
  description = await describe(page, ".count");
  assert.deepEqual(description.members.map(({ member, kind, evidence, value, symbol }) => ({ member, kind, evidence, value, symbol })),
    [{ member: "count", kind: "state", evidence: "text", value: 11, symbol: "Demo.CounterViewModel.Count" }]);
  description = await describe(page, ".status");
  assert.deepEqual(description.texts, [{ route: "counter", path: "status", catalog: "app", key: "status.ready", locale: "en",
    text: "Ready to count", arguments: {} }]);
  await page.fill("#step", "3");
  await page.waitForFunction(() => globalThis.inspectTest.received.includes("set step"));
  description = await describe(page, "#step");
  assert.deepEqual(description.members.map(({ member, kind, evidence, value, symbol, location }) => ({ member, kind, evidence, value, symbol, line: location?.line })),
    [{ member: "step", kind: "setter", evidence: "call", value: 3, symbol: "Demo.CounterViewModel.Step.set", line: 6 }]);

  // Select mode: the keyboard turns it on; a click selects without running the app's handler.
  await page.keyboard.press("Alt+Shift+X");
  assert.equal(await page.evaluate(() => globalThis[Symbol.for("runic.vite.inspect")].active), true);
  const before = await received(page);
  const { x, y } = await center(page, "#increment");
  await page.mouse.move(x, y);
  await page.waitForFunction(() => document.querySelector("runic-inspect")?.shadowRoot.querySelector(".tip")?.textContent.startsWith("button#increment"));
  await page.mouse.click(x, y);
  const card = page.locator("runic-inspect .card");
  await card.getByText("Demo.CounterViewModel.Increment()").waitFor({ timeout: scaled(10_000) });
  assert.deepEqual(await received(page), before, "the selecting click ran the app's handler");
  assert.equal(await page.locator(".count").textContent(), "11");
  assert.match(await card.textContent(), new RegExp(`${component}.*${framework}`));

  // The handler's link opens the C# location through /__runic/open with the source map entry's id.
  let opened;
  await page.route("**/__runic/open", async route => {
    opened = { method: route.request().method(), type: route.request().headers()["content-type"], body: route.request().postDataJSON() };
    await route.fulfill({ status: 200, body: "" });
  });
  await card.locator("a", { hasText: "CounterViewModel.cs:11" }).click();
  for (let attempt = 0; opened === undefined && attempt < 50; attempt++) await new Promise(resolve => setTimeout(resolve, 50));
  assert.deepEqual(opened, { method: "POST", type: "application/json", body: { id: "Demo.CounterViewModel/command/increment" } });
  // A link to the component opens through Vite's /__open-in-editor.
  let editorRequest;
  await page.route("**/__open-in-editor*", async route => {
    editorRequest = new URL(route.request().url()).searchParams.get("file");
    await route.fulfill({ status: 200, body: "" });
  });
  // The component section comes first; a client call on the same line links the same file and line.
  await card.locator("a", { hasText: `${componentFile.file}:${componentFile.line}` }).first().click();
  for (let attempt = 0; editorRequest === undefined && attempt < 50; attempt++) await new Promise(resolve => setTimeout(resolve, 50));
  assert.match(editorRequest, new RegExp(`${componentFile.file.replace(".", "\\.")}:${componentFile.line}:\\d+$`));

  // Escape leaves select mode, and clicks reach the app again, also with an overview that select() shows.
  await page.keyboard.press("Escape");
  assert.equal(await page.evaluate(() => globalThis[Symbol.for("runic.vite.inspect")].active), false);
  const selected = await page.evaluate(() => globalThis[Symbol.for("runic.vite.inspect")].select(document.querySelector(".count")));
  assert.equal(selected.members[0]?.member, "count");
  await card.getByText("matches the text").waitFor({ timeout: scaled(10_000) });
  await page.click("#increment");
  await page.waitForFunction(() => globalThis.inspectTest.received.filter(member => member === "increment").length === 2);
}

for (const [name, engine] of Object.entries(engines)) test(`Runic Inspect on a React page in ${name}`, {
  timeout: scaled(90_000), skip: !engine.executablePath,
}, async () => {
  const counter = `import { useSyncExternalStore } from "react";
import { useCommand } from "@runic-artifex/react";
import { resolveText } from "@runic-artifex/views";

export function CounterPage({ client }) {
  const state = useSyncExternalStore(client.subscribe, () => client.snapshot);
  const increment = useCommand(() => client.command("increment"));
  return <section>
    <p className="count">{state.count}</p>
    <p className="status">{resolveText(state.status)}</p>
    <input id="step" value={state.step} onChange={event => void client.set("step", Number(event.target.value))} />
    <button id="increment" onClick={() => void increment.run()}>Increment</button>
  </section>;
}
`;
  const main = `import { createRoot } from "react-dom/client";
import { client, received } from "./bridge.js";
import { CounterPage } from "./CounterPage.jsx";

createRoot(document.getElementById("root")).render(<CounterPage client={client} />);
globalThis.inspectTest = { received, ready: true };
`;
  await withPage({
    "index.html": '<!doctype html><html><body><div id="root"></div><script type="module" src="/main.jsx"></script></body></html>',
    "main.jsx": main,
    "CounterPage.jsx": counter,
  }, [], page => exercise(page, {
    framework: "react", component: "CounterPage", componentFile: { file: "CounterPage.jsx", line: 12 },
    source: { file: "CounterPage.jsx", ...positionOf(counter, '"increment"));'), via: "helper" },
  }), engine);
});

test("Runic Inspect on a Svelte page", { timeout: scaled(90_000), skip }, async () => {
  const { svelte } = await import("@sveltejs/vite-plugin-svelte");
  const counter = `<script>
  import { resolveText } from "@runic-artifex/views";
  let { client } = $props();
  let state = $state(client.snapshot);
  $effect(() => client.subscribe(next => { state = next; }));
</script>

<section>
  <p class="count">{state.count}</p>
  <p class="status">{resolveText(state.status)}</p>
  <input id="step" value={state.step} oninput={event => void client.set("step", Number(event.currentTarget.value))} />
  <button id="increment" onclick={() => void client.command("increment")}>Increment</button>
</section>
`;
  const main = `import { mount } from "svelte";
import { client, received } from "./bridge.js";
import CounterPage from "./CounterPage.svelte";

mount(CounterPage, { target: document.getElementById("root"), props: { client } });
globalThis.inspectTest = { received, ready: true };
`;
  await withPage({
    "index.html": '<!doctype html><html><body><div id="root"></div><script type="module" src="/main.js"></script></body></html>',
    "main.js": main,
    "CounterPage.svelte": counter,
  }, [svelte()], page => exercise(page, {
    framework: "svelte", component: "CounterPage", componentFile: { file: "CounterPage.svelte", line: 12 },
    source: { file: "CounterPage.svelte", ...positionOf(counter, '"increment")}>'), via: "direct" },
  }));
});

// A render-function component has no template for the analysis, so its command is learned on first click.
// Without @vitejs/plugin-vue, a render-function component carries the `__name` and `__file` that the SFC compiler
// adds in development; the element's `__vueParentComponent` is Vue's own.
test("Runic Inspect on a Vue page", { timeout: scaled(90_000), skip }, async () => {
  const main = `import { createApp, defineComponent, h, onUnmounted, shallowRef } from "vue";
import { resolveText } from "@runic-artifex/views";
import { client, received } from "./bridge.js";

const CounterPage = defineComponent({
  __name: "CounterPage",
  __file: globalThis[Symbol.for("runic.vite.client")].root + "/CounterPage.vue",
  props: ["client"],
  setup(props) {
    const state = shallowRef(props.client.snapshot);
    onUnmounted(props.client.subscribe(next => { state.value = next; }));
    return () => h("section", [
      h("p", { class: "count" }, String(state.value.count)),
      h("p", { class: "status" }, resolveText(state.value.status)),
      h("input", { id: "step", value: state.value.step, onInput: event => void props.client.set("step", Number(event.target.value)) }),
      h("button", { id: "increment", onClick: () => void props.client.command("increment") }, "Increment"),
    ]);
  },
});
createApp(CounterPage, { client }).mount("#root");
globalThis.inspectTest = { received, ready: true };
`;
  await withPage({
    "index.html": '<!doctype html><html><body><div id="root"></div><script type="module" src="/main.js"></script></body></html>',
    "main.js": main,
  }, [], page => exercise(page, {
    framework: "vue", component: "CounterPage", componentFile: { file: "CounterPage.vue", line: 1 },
  }));
});

// Before the first click (#976): a single-file component compiled by @vitejs/plugin-vue, whose template calls the
// `run` of @runic-artifex/vue's useCommand, as the starter template does.
test("Runic Inspect before the first click on a Vue single-file component", { timeout: scaled(90_000), skip }, async () => {
  const { default: vue } = await import("@vitejs/plugin-vue");
  const counter = `<script setup>
import { onUnmounted, shallowRef } from "vue";
import { useCommand } from "@runic-artifex/vue";
import { resolveText } from "@runic-artifex/views";

const props = defineProps(["client"]);
const state = shallowRef(props.client.snapshot);
onUnmounted(props.client.subscribe(next => { state.value = next; }));
const { run: increment } = useCommand(() => props.client.command("increment"));
</script>

<template>
  <section>
    <p class="count">{{ state.count }}</p>
    <p class="status">{{ resolveText(state.status) }}</p>
    <input id="step" :value="state.step" @input="event => void props.client.set('step', Number(event.target.value))">
    <button id="increment" @click="increment()">Increment</button>
  </section>
</template>
`;
  const main = `import { createApp } from "vue";
import { client, received } from "./bridge.js";
import CounterPage from "./CounterPage.vue";

createApp(CounterPage, { client }).mount("#root");
globalThis.inspectTest = { received, ready: true };
`;
  await withPage({
    "index.html": '<!doctype html><html><body><div id="root"></div><script type="module" src="/main.js"></script></body></html>',
    "main.js": main,
    "CounterPage.vue": counter,
  }, [vue()], async context => {
    // The card says the command is from the source and not called yet, with a link to the client call.
    const { page } = context;
    await page.evaluate(() => globalThis[Symbol.for("runic.vite.inspect")].select(document.querySelector("#increment")));
    const card = page.locator("runic-inspect .card");
    await card.getByText("from the source, not called yet").waitFor({ timeout: scaled(10_000) });
    assert.ok(await card.locator("a", { hasText: "CounterPage.vue:9" }).count() > 0);
    await page.evaluate(() => { document.querySelector("runic-inspect").shadowRoot.querySelector(".card").hidden = true; });
    await exercise(context, {
      framework: "vue", component: "CounterPage", componentFile: { file: "CounterPage.vue", line: 1 },
      source: { file: "CounterPage.vue", ...positionOf(counter, '"increment"));'), via: "helper" },
    });
  });
});

// Before the first click: a React component's handler that reaches the command through a child component
// (`<Button onClick={…}>`), and one that calls a function of another file, which is learned on first click.
test("Runic Inspect before the first click through a React component and another file", { timeout: scaled(90_000), skip }, async () => {
  const counter = `import { useSyncExternalStore } from "react";
import { useCommand } from "@runic-artifex/react";
import { Button } from "./Button.jsx";
import { incrementTwice } from "./actions.js";

export function CounterPage({ client }) {
  const state = useSyncExternalStore(client.subscribe, () => client.snapshot);
  const increment = useCommand(() => client.command("increment"));
  function onIncrement() {
    void increment.run();
  }
  return <section>
    <p className="count">{state.count}</p>
    <Button id="increment" onClick={onIncrement}>Increment</Button>
    <button id="twice" onClick={() => void incrementTwice(client)}>Twice</button>
  </section>;
}
`;
  const main = `import { createRoot } from "react-dom/client";
import { client, received } from "./bridge.js";
import { CounterPage } from "./CounterPage.jsx";

createRoot(document.getElementById("root")).render(<CounterPage client={client} />);
globalThis.inspectTest = { received, ready: true };
`;
  await withPage({
    "index.html": '<!doctype html><html><body><div id="root"></div><script type="module" src="/main.jsx"></script></body></html>',
    "main.jsx": main,
    "CounterPage.jsx": counter,
    "Button.jsx": "export function Button({ id, onClick, children }) {\n  return <button id={id} onClick={onClick}>{children}</button>;\n}\n",
    "actions.js": 'export async function incrementTwice(client) {\n  await client.command("increment");\n  return client.command("increment");\n}\n',
  }, [], async ({ page, root, csharp }) => {
    // The <button> is Button's; its onClick is a prop, so the command comes from <Button onClick> in CounterPage.
    let description = await describe(page, "#increment");
    assert.equal(description.component?.name, "Button");
    assert.deepEqual(description.commands, [{
      ...handler(csharp), evidence: "source", via: "helper", calls: 0,
      frontend: { file: join(root, "CounterPage.jsx"), ...positionOf(counter, '"increment"));'), editor: "vite" },
    }]);

    // A function of another file: not followed, and the note says the command is learned on first click.
    description = await describe(page, "#twice");
    assert.deepEqual(description.commands, []);
    assert.ok(description.notes.some(note => note.includes("incrementTwice from ./actions.js")
      && note.includes("learned on first click")), JSON.stringify(description.notes));
    await page.click("#twice");
    await page.waitForFunction(() => globalThis.inspectTest.received.length === 2);
    await page.waitForFunction(async () => (await globalThis[Symbol.for("runic.vite.inspect")]
      .describe(document.querySelector("#twice"))).commands[0]?.calls === 2);
    description = await describe(page, "#twice");
    assert.deepEqual(description.commands.map(({ member, evidence, symbol }) => ({ member, evidence, symbol })),
      [{ member: "increment", evidence: "call", symbol: "Demo.CounterViewModel.Increment()" }]);
  });
});

// Before the first click on an Angular component: Angular's own template compiler (JIT here; Analog and the Angular
// CLI compile ahead of time) and `ng.getListeners`, with the class debug info that the AOT compiler adds in
// development (`ɵsetClassDebugInfo`, which JIT leaves out).
test("Runic Inspect before the first click on an Angular component", { timeout: scaled(90_000), skip }, async () => {
  const angular = join(packages, "angular", "node_modules", "@angular");
  const counter = `import { Component, ɵsetClassDebugInfo } from "@angular/core";
import { injectCommand } from "@runic-artifex/angular";
import { client } from "./bridge.js";

@Component({
  selector: "app-counter",
  template: \`
    <section>
      <p class="count">{{ count }}</p>
      <input id="step" [value]="step" (input)="setStep($event)">
      <button id="increment" (click)="increment.run()">Increment</button>
      @if (count >= 0) { <button id="again" (click)="again()">Again</button> }
    </section>
  \`,
})
export class CounterComponent {
  count = client.snapshot.count;
  step = client.snapshot.step;
  readonly increment = injectCommand(() => client.command("increment"));
  constructor() { client.subscribe(next => { this.count = next.count; this.step = next.step; }); }
  setStep(event: Event): void { void client.set("step", Number((event.target as HTMLInputElement).value)); }
  again(): void { void client.command("increment"); }
}
ɵsetClassDebugInfo(CounterComponent, { className: "CounterComponent", filePath: "counter.ts", lineNumber: 5 });
`;
  const main = `import "@angular/compiler";
import { ChangeDetectorRef, provideZonelessChangeDetection } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { received } from "./bridge.js";
import { CounterComponent } from "./counter.ts";

const application = await bootstrapApplication(CounterComponent, { providers: [provideZonelessChangeDetection()] });
globalThis.inspectTest = { received, ready: true, application };
`;
  await withPage({
    "index.html": '<!doctype html><html><body><app-counter></app-counter><script type="module" src="/main.ts"></script></body></html>',
    "main.ts": main,
    "counter.ts": counter,
    "tsconfig.json": JSON.stringify({ compilerOptions: { experimentalDecorators: true, useDefineForClassFields: false } }),
  }, [], async ({ page, root, csharp }) => {
    const listeners = await page.evaluate(() => globalThis.ng.getListeners(document.querySelector("#increment"))
      .map(listener => ({ name: listener.name, type: listener.type, text: String(listener.callback) })));
    assert.deepEqual(listeners.map(({ name }) => name), ["click"], JSON.stringify(listeners));

    // The command and its C# handler before any click: through injectCommand, and through a method in an @if block.
    for (const [selector, needle, via] of [["#increment", '"increment"));', "helper"], ["#again", '"increment"); }', "local"]]) {
      const description = await describe(page, selector);
      assert.equal(description.component?.framework, "angular");
      assert.deepEqual(description.commands, [{
        ...handler(csharp), evidence: "source", via, calls: 0,
        frontend: { file: join(root, "counter.ts"), ...positionOf(counter, needle), editor: "vite" },
      }], `${selector} ${JSON.stringify(listeners)}`);
    }
    assert.deepEqual(await received(page), []);
    await page.click("#increment");
    await page.waitForFunction(() => globalThis.inspectTest.received.includes("increment"));
    await page.waitForFunction(async () => (await globalThis[Symbol.for("runic.vite.inspect")]
      .describe(document.querySelector("#increment"))).commands[0]?.evidence === "call");
  });
});
