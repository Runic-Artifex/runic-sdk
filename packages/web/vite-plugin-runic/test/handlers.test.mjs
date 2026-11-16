// Runic Inspect before the first click (#976): the per-file analysis of event handlers for React, Vue, Svelte and
// Angular (direct client calls, Runic's helpers, local declarations, other files), matching an element's probes to
// the analyzed bindings, the overview's source commands, `/__runic/handlers` under `runic()` and under
// `runicDevSessionProxy()` (the Angular CLI), and served and built modules that are the same without the analysis.
import assert from "node:assert/strict";
import { mkdir, mkdtemp, readdir, readFile, realpath, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { build, createServer } from "vite";
import * as svelteCompiler from "svelte/compiler";
import * as vueCompiler from "vue/compiler-sfc";
import { runic, runicDevSessionProxy } from "../dist/index.js";
import {
  analyzeAngular, analyzeJsx, analyzeSvelte, analyzeVue, angularTemplateBindings, createHandlerAnalyzer, loadHandlerParser, mayAnalyze,
} from "../dist/handlers.js";
import { matchBindings, normalizeHandler, probeFiles, svelteProbes, vueProbes, angularProbes } from "../dist/inspect-handlers.js";
import { assembleDescription } from "../dist/inspect-data.js";

const here = fileURLToPath(new URL("..", import.meta.url));
await loadHandlerParser();

/** Each binding as `tag event: members (via)`, for readable expectations. */
const summary = (bindings) => bindings.map((binding) =>
  `${binding.tag} ${binding.event}: ${binding.calls.map((call) => `${call.member}(${call.via})`).join(" ")}`
  + (binding.external.length === 0 ? "" : ` [${binding.external.map((entry) => `${entry.name} from ${entry.source}`).join(", ")}]`));

/** The 1-based line and column of `needle` in `text`. */
function positionOf(text, needle) {
  const index = text.indexOf(needle);
  assert.notEqual(index, -1, needle);
  const lines = text.slice(0, index).split("\n");
  return { line: lines.length, column: lines.at(-1).length + 1 };
}

test("React: direct calls, helpers, local declarations, components and other files", () => {
  const code = `import { useCommand } from "@runic-artifex/react";
import { saveAll } from "./store.js";

export function Editor({ client }: { client: EditorClient }) {
  const save = useCommand(() => client.save());
  const remove = () => client?.remove(1);
  function publish() { return client.command("publish"); }
  const { run: archive } = useCommand(async () => { await client!.archive(); });
  return <div>
    <button onClick={() => client.reload()}>Reload</button>
    <button onClick={() => void save.run()}>Save</button>
    <button onClick={remove}>Remove</button>
    <button onClick={publish}>Publish</button>
    <button onClickCapture={archive}>Archive</button>
    <button onClick={() => saveAll(client)}>All</button>
    <Toolbar.Item onSave={() => save.run()} />
  </div>;
}
`;
  const bindings = analyzeJsx("/app/src/Editor.tsx", code);
  assert.deepEqual(summary(bindings), [
    "button click: reload(direct)",
    "button click: save(helper)",
    "button click: remove(local)",
    "button click: publish(local)",
    "button click: archive(helper)",
    "button click:  [saveAll from ./store.js]",
    "Toolbar.Item save: save(helper)",
  ]);
  // The element's `<` and the client call's member, 1-based.
  assert.deepEqual({ line: bindings[1].line, column: bindings[1].column }, positionOf(code, "<button onClick={() => void save"));
  assert.deepEqual({ ...bindings[1].calls[0], file: undefined }, { member: "save", via: "helper", file: undefined, ...positionOf(code, "save());") });
  assert.equal(bindings[1].expression, "() => void save.run()");
  // A .js file with JSX, as Vite's React plugin accepts it.
  assert.deepEqual(summary(analyzeJsx("/app/src/a.js", "export const A = ({ c }) => <a onClick={() => c.open()} />;")), ["a click: open(direct)"]);
});

test("Vue: <script setup> with useCommand, the Options API, and other files", () => {
  const setup = `<script setup lang="ts">
import { useCommand } from "@runic-artifex/vue";
import { exportAll } from "@/export";
const props = defineProps<{ client: Client }>();
const { run: increment } = useCommand(() => props.client.increment());
const step = useField(view, Fields.step);
function reset(): void { void props.client.command("reset"); }
</script>

<template>
  <section>
    <button @click="increment()">Increment</button>
    <button v-on:click="reset">Reset</button>
    <select @blur="step.commit"></select>
    <template v-if="props.client"><a @click.prevent="props.client.open(1)">Open</a></template>
    <button @click="exportAll(props.client)">Export</button>
  </section>
</template>
`;
  const bindings = analyzeVue("/app/src/Counter.vue", setup, vueCompiler);
  assert.deepEqual(summary(bindings), [
    "button click: increment(helper)",
    "button click: reset(local)",
    "select blur: ",
    "a click: open(direct)",
    "button click:  [exportAll from @/export]",
  ]);
  assert.deepEqual({ line: bindings[0].line, column: bindings[0].column }, positionOf(setup, "<button @click"));
  assert.deepEqual({ line: bindings[0].calls[0].line, column: bindings[0].calls[0].column }, positionOf(setup, "increment());"));

  const options = `<script>
export default {
  props: ["client"],
  methods: {
    save() { return this.client.save(); },
  },
};
</script>
<template><button @click="save">Save</button></template>
`;
  assert.deepEqual(summary(analyzeVue("/app/src/Options.vue", options, vueCompiler)), ["button click: save(local)"]);
});

test("Svelte 5: onclick and on:click, useCommand, components, and other files", () => {
  const code = `<script lang="ts">
  import { useCommand } from "@runic-artifex/svelte";
  import { refreshAll } from "$lib/refresh";
  let { counter }: { counter: CounterView } = $props();
  const increment = useCommand(() => counter.client?.increment());
  const decrement = () => counter.client?.command("decrement");
</script>

<button onclick={() => increment.run()}>+</button>
<button on:click={decrement}>-</button>
{#if counter.client}
  <Fancy onpress={() => counter.client.reset()} />
{/if}
<button onclick={() => refreshAll()}>Refresh</button>
`;
  const bindings = analyzeSvelte("/app/src/Counter.svelte", code, svelteCompiler);
  assert.deepEqual(summary(bindings), [
    "button click: increment(helper)",
    "button click: decrement(local)",
    "Fancy press: reset(direct)",
    "button click:  [refreshAll from $lib/refresh]",
  ]);
  assert.deepEqual({ line: bindings[0].line, column: bindings[0].column }, positionOf(code, "<button onclick={() => increment"));
});

test("Angular: inline templates and templateUrl, injectCommand, class methods, and other files", () => {
  const code = `import { Component } from "@angular/core";
import { injectCommand, injectView } from "@runic-artifex/angular";
import { shareAll } from "./share";

@Component({
  selector: "app-counter",
  template: \`
    <button (click)="increment.run()">Increment</button>
    <button (click)="save()" (keydown.enter)="save()">Save</button>
    @if (counter.client(); as client) { <a on-click="client.open()">Open</a> }
    <button (click)="share()">Share</button>
  \`,
})
export class CounterComponent {
  readonly page = input.required<CounterPageReference>();
  readonly counter = injectView(this.page);
  readonly increment = injectCommand(() => this.counter.client()?.increment());
  save(): void { void this.counter.client()?.save(); }
  share(): void { shareAll(this.counter); }
}
`;
  const bindings = analyzeAngular("/app/src/app/counter.ts", code, () => undefined);
  assert.deepEqual(summary(bindings), [
    "button click: increment(helper)",
    "button click: save(local)",
    "button keydown: save(local)",
    "a click: open(direct)",
    "button click:  [shareAll from ./share]",
  ]);
  // Inline: positions are in the script.
  assert.equal(bindings[0].file, "/app/src/app/counter.ts");
  assert.deepEqual({ line: bindings[0].line, column: bindings[0].column }, positionOf(code, "<button (click)=\"increment"));

  // templateUrl: the bindings are in the template file.
  const external = code.replace(/template: `[\s\S]*?`,/u, 'templateUrl: "./counter.html",');
  const template = '<section>\n  <button [disabled]="!counter.client()" (click)="increment.run()">Increment</button>\n</section>\n';
  const read = [];
  const fromFile = analyzeAngular("/app/src/app/counter.ts", external, (path) => { read.push(path); return template; });
  assert.deepEqual(read, ["/app/src/app/counter.html"]);
  assert.deepEqual(fromFile.map(({ file, line, column }) => ({ file, line, column })), [{ file: "/app/src/app/counter.html", line: 2, column: 3 }]);
  assert.deepEqual(fromFile[0].calls.map(({ member, file }) => ({ member, file })), [{ member: "increment", file: "/app/src/app/counter.ts" }]);
  // Quotes and comments in templates.
  assert.deepEqual(angularTemplateBindings(`<!-- <b (click)="x()"> --><i title="a > b" (click)='y("z")'></i>`).map(({ tag, event, expression }) =>
    ({ tag, event, expression })), [{ tag: "i", event: "click", expression: 'y("z")' }]);
});

// Matching ---------------------------------------------------------------------------------------------------------

const analysis = (file, bindings) => ({ format: "runic.vite.handlers/1", file, framework: "react", bindings });
const call = (member, file = "/app/A.tsx") => ({ member, file, line: 3, column: 9, via: "direct" });
const bound = (tag, event, line, column, expression, calls, file = "/app/A.tsx") =>
  ({ file, line, column, tag, event, expression, calls: calls.map((member) => call(member, file)), external: [] });

test("matching: a location probe takes the element on its line, then components around it", () => {
  const a = analysis("/app/A.tsx", [bound("button", "click", 4, 5, "() => x.save()", ["save"]), bound("button", "click", 4, 40, "() => x.load()", ["load"]),
    bound("Toolbar", "click", 9, 5, "() => x.publish()", ["publish"])]);
  const b = analysis("/app/Button.tsx", [bound("button", "click", 2, 10, "onClick", [], "/app/Button.tsx")]);
  const at = (file, line, column, tag, events) => ({ kind: "location", file, line, column, tag, ...(events ? { events } : {}) });
  assert.deepEqual(matchBindings([at("/app/A.tsx", 4, 40, "button")], [a]).calls.map((entry) => entry.member), ["load"]);
  // The nearest on the line, when the column differs (the transform's mapping).
  assert.deepEqual(matchBindings([at("/app/A.tsx", 4, 6, "button")], [a]).calls.map((entry) => entry.member), ["save"]);
  // <button onClick={onClick}> inside Button, then <Toolbar onClick> around it, for the element's own events.
  const nested = matchBindings([at("/app/Button.tsx", 2, 10, "button", ["click"]), at("/app/A.tsx", 9, 5, "Toolbar", ["click"])], [a, b]);
  assert.deepEqual(nested.calls.map((entry) => entry.member), ["publish"]);
  assert.deepEqual(matchBindings([at("/app/Button.tsx", 2, 10, "button", ["input"]), at("/app/A.tsx", 9, 5, "Toolbar", ["input"])], [a, b]).calls, []);
  assert.deepEqual(probeFiles([at("/app/A.tsx", 1, 1, "a"), at("/app/A.tsx", 2, 1, "b"), at("/app/B.tsx", 1, 1, "c")]), ["/app/A.tsx", "/app/B.tsx"]);
});

test("matching: Vue and Angular listeners by their compiled handlers", () => {
  const vue = analysis("/app/C.vue", [bound("button", "click", 3, 5, "increment()", ["increment"], "/app/C.vue"),
    bound("button", "click", 4, 5, "incrementBy(2)", ["incrementBy"], "/app/C.vue"),
    bound("button", "click", 5, 5, "save", ["save"], "/app/C.vue")]);
  const listeners = (framework, files, list, resolve) => ({ kind: "listeners", framework, files, tag: "button", listeners: list, ...(resolve ? { resolve } : {}) });
  const match = (probe) => matchBindings([probe], [vue]).calls.map((entry) => entry.member);
  assert.deepEqual(match(listeners("vue", ["/app/C.vue"], [{ event: "click", text: "$event => ($setup.increment())" }])), ["increment"]);
  assert.deepEqual(match(listeners("vue", ["/app/C.vue"], [{ event: "click", text: "$event => (_ctx.incrementBy(2))" }])), ["incrementBy"]);
  // `@click="save"` compiles to the function itself: matched by the component's value.
  const run = () => {};
  assert.deepEqual(match(listeners("vue", ["/app/C.vue"], [{ event: "click", text: "function run() {}", value: run }],
    (file, path) => file === "/app/C.vue" && path === "save" ? run : undefined)), ["save"]);
  // Not recognizable and not unique: nothing.
  assert.deepEqual(match(listeners("vue", ["/app/C.vue"], [{ event: "click", text: "() => {}" }])), []);
  // Another file's component: nothing.
  assert.deepEqual(match(listeners("vue", ["/app/D.vue"], [{ event: "click", text: "$event => ($setup.increment())" }])), []);

  // Angular: an external template's bindings are named by the analysis of the component's script.
  const angular = { format: "runic.vite.handlers/1", file: "/app/counter.ts", framework: "angular", bindings: [
    bound("button", "click", 2, 3, "increment.run()", ["increment"], "/app/counter.html"),
    bound("button", "click", 3, 3, "save()", ["save"], "/app/counter.html")] };
  const ng = (text) => matchBindings([listeners("angular", ["/app/counter.ts"], [{ event: "click", text }])], [angular]).calls.map((entry) => entry.member);
  assert.deepEqual(ng("function CounterComponent_Template_button_click_8_listener() { return ctx.increment.run(); }"), ["increment"]);
  assert.deepEqual(ng("function CounterComponent_Conditional_3_Template_button_click_0_listener() { i0.ɵɵrestoreView(_r1); const ctx_r1 = i0.ɵɵnextContext(); return i0.ɵɵresetView(ctx_r1.save()); }"), ["save"]);
  // Angular 17+ gives the listener's wrapper: the element's position among its component's buttons with the event.
  // Under the Angular CLI the component's file is relative to the project, the analysis's absolute.
  const wrapper = "function wrapListenerIn_markDirtyAndPreventDefault(event) { markViewDirty(lView); }";
  const relative = { ...angular, file: "/project/app/counter.ts", requested: "app/counter.ts" };
  const at = (index, count) => matchBindings([listeners("angular", ["app/counter.ts"], [{ event: "click", text: wrapper, position: { index, count } }])],
    [relative]).calls.map((entry) => entry.member);
  assert.deepEqual(at(0, 2), ["increment"]);
  assert.deepEqual(at(1, 2), ["save"]);
  // More buttons on the page than in the template (an @for): not known.
  assert.deepEqual(at(1, 3), []);
  assert.equal(normalizeHandler("$event => ($setup.step.commit($event))"), " $event = > step . commit $event ");
  assert.equal(normalizeHandler("step.commit( $event );"), normalizeHandler("step.commit($event)"));
});

test("probes: Svelte, Vue and Angular development metadata", () => {
  assert.deepEqual(svelteProbes({ localName: "button", __svelte_meta: { loc: { file: "src/A.svelte", line: 4, column: 2 },
    parent: { type: "component", file: "src/Page.svelte", line: 9, column: 4, componentTag: "A", parent: null } } }, "/app"), [
    { kind: "location", file: "/app/src/A.svelte", line: 4, column: 3, tag: "button" },
    { kind: "location", file: "/app/src/Page.svelte", line: 9, column: 5, tag: "A" },
  ]);
  const handler = () => {};
  const element = { localName: "button", __vueParentComponent: { type: { __file: "/app/src/C.vue" }, parent: { type: { __file: "/app/src/App.vue" } } },
    [Symbol("_vei")]: { onClick: { value: handler } } };
  const [vue] = vueProbes(element, "/app");
  assert.deepEqual({ ...vue, resolve: undefined, listeners: vue.listeners.map(({ event }) => event) },
    { kind: "listeners", framework: "vue", files: ["/app/src/C.vue", "/app/src/App.vue"], tag: "button", listeners: ["click"], resolve: undefined });
  class CounterComponent { static ɵcmp = { debugInfo: { filePath: "src/app/counter.ts" } }; }
  const ng = { getListeners: () => [{ name: "click", callback: function listener() { return 1; }, type: "dom" }],
    getOwningComponent: () => new CounterComponent(), getHostElement: () => ({ parentElement: null }) };
  const [angular] = angularProbes({ localName: "button" }, ng, undefined);
  assert.deepEqual({ ...angular, listeners: angular.listeners.map(({ event }) => event) },
    { kind: "listeners", framework: "angular", files: ["src/app/counter.ts"], tag: "button", listeners: ["click"] });
});

// The overview -----------------------------------------------------------------------------------------------------

const sourceMap = {
  format: "runic.views.bridge-sources/1",
  viewModels: [{ id: "Demo.CounterViewModel", name: "Counter", route: "counter", contract: "Demo.CounterViewModel:F", pageKinds: ["counter"],
    symbol: "Demo.CounterViewModel" }],
  members: [{ id: "Demo.CounterViewModel/command/increment", viewModel: "Demo.CounterViewModel", member: "increment", kind: "command",
    symbol: "Demo.CounterViewModel.Increment()", location: { file: "/app/Counter.cs", line: 9, column: 5 } }],
};
const describeWith = (source, overrides = {}) => assembleDescription({
  element: { tag: "button", classes: [], text: "+", ownText: "+", interactive: true }, learned: [], calls: [],
  states: new Map([["content1", { count: 1, canIncrement: true }]]), contracts: new Map(), sourceMap, source, ...overrides,
});

test("the overview lists source commands before a click, with the ViewModel's route", () => {
  const source = { bindings: [], calls: [call("increment", "/app/A.tsx"), call("run"), call("setStep")], external: [], unavailable: [] };
  // content1 is the Counter by its state members.
  sourceMap.members.push({ id: "Demo.CounterViewModel/state/count", viewModel: "Demo.CounterViewModel", member: "count", kind: "state", symbol: "C" },
    { id: "Demo.CounterViewModel/commandState/canIncrement", viewModel: "Demo.CounterViewModel", member: "canIncrement", kind: "commandState", symbol: "C" });
  let description = describeWith(source);
  assert.deepEqual(description.commands, [{
    route: "content1", viewModel: "Demo.CounterViewModel", member: "increment", evidence: "source",
    frontend: { file: "/app/A.tsx", line: 3, column: 9, editor: "vite" }, via: "direct", symbol: "Demo.CounterViewModel.Increment()",
    location: { file: "/app/Counter.cs", line: 9, column: 5, editor: "runic", id: "Demo.CounterViewModel/command/increment" },
    canExecute: true, calls: 0,
  }]);
  assert.ok(!description.notes.some((note) => note.startsWith("No Bridge call")));
  // No open route of the ViewModel: the source map's route.
  description = describeWith(source, { states: new Map() });
  assert.equal(description.commands[0]?.route, "counter");
  // Without the source map, candidates are not shown as commands.
  assert.deepEqual(describeWith(source, { sourceMap: undefined, sourceMapStatus: "missing" }).commands, []);
  // After a call, the learned command keeps where the source calls it.
  description = describeWith(source, { learned: [{ route: "content1", contract: "", kind: "command", member: "increment" }] });
  assert.deepEqual(description.commands.map(({ evidence, frontend }) => ({ evidence, line: frontend?.line })), [{ evidence: "call", line: 3 }]);
  // A handler that calls another file's function: learned on first click.
  description = describeWith({ bindings: [], calls: [], external: [{ name: "saveAll", source: "./store" }], unavailable: [] });
  assert.ok(description.notes.some((note) => note.includes("saveAll from ./store") && note.includes("learned on first click")), JSON.stringify(description.notes));
});

// The endpoint -----------------------------------------------------------------------------------------------------

async function project() {
  const root = await realpath(await mkdtemp(join(tmpdir(), "runic-handlers-")));
  await mkdir(join(root, "src"));
  // A stand-in for @runic-artifex/react's useCommand, so that the project builds without it.
  await writeFile(join(root, "src", "runic.js"), "export const useCommand = (command) => ({ run: command });\n");
  const page = `import { useCommand } from "./runic.js";
export function Page({ client }) {
  const save = useCommand(() => client.save());
  return <button id="save" onClick={() => void save.run()}>Save</button>;
}
`;
  await writeFile(join(root, "src", "Page.jsx"), page);
  await writeFile(join(root, "src", "main.jsx"), 'import { Page } from "./Page.jsx";\nglobalThis.page = Page;\n');
  await writeFile(join(root, "index.html"), '<!doctype html><html><body><script type="module" src="/src/main.jsx"></script></body></html>');
  await writeFile(join(root, "src", "counter.ts"), '@Component({ selector: "c", templateUrl: "./counter.html" })\nexport class C {\n  save() { this.client.save(); }\n}\n');
  await writeFile(join(root, "src", "counter.html"), '<button (click)="save()">Save</button>\n');
  await writeFile(join(root, "src", "Counter.svelte"), '<script>\n  let { client } = $props();\n</script>\n<button onclick={() => client.save()}>Save</button>\n');
  await mkdir(join(root, "node_modules"));
  for (const name of ["svelte", "react"]) await symlink(await realpath(join(here, "node_modules", name)), join(root, "node_modules", name), "dir");
  await writeFile(join(root, "sources.json"), JSON.stringify(sourceMap));
  return root;
}

test("analyzed files: frontend sources below the root, not node_modules", async () => {
  assert.equal(mayAnalyze("/app/src/A.tsx", "/app"), true);
  assert.equal(mayAnalyze("/app/../etc/passwd", "/app"), false);
  assert.equal(mayAnalyze("/etc/A.tsx", "/app"), false);
  assert.equal(mayAnalyze("/app/node_modules/x/A.tsx", "/app"), false);
  assert.equal(mayAnalyze("/app/src/a.json", "/app"), false);
  const root = await project();
  try {
    const analyze = createHandlerAnalyzer(() => root);
    const svelte = await analyze(join(root, "src", "Counter.svelte"));
    assert.deepEqual(summary(svelte.bindings), ["button click: save(direct)"]);
    const angular = await analyze(join(root, "src", "counter.ts"));
    assert.equal(angular.framework, "angular");
    assert.deepEqual(summary(angular.bindings), ["button click: save(local)"]);
    // A changed template is analyzed again.
    await writeFile(join(root, "src", "counter.html"), '<button (click)="save()">Save</button>\n<a (click)="client.open()">Open</a>\n');
    assert.deepEqual(summary((await analyze(join(root, "src", "counter.ts"))).bindings), ["button click: save(local)", "a click: open(direct)"]);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("/__runic/handlers answers pages of this machine under runic() and runicDevSessionProxy()", async () => {
  const root = await project();
  const servers = [];
  const cwd = process.cwd();
  try {
    const plugin = await createServer({ root, configFile: false, logLevel: "silent", plugins: [runic({ sourceMap: "sources.json" })],
      server: { host: "127.0.0.1", port: 0 } });
    servers.push(plugin);
    // The Angular CLI: no Vite plugins, the proxy entries, the working directory as the root.
    process.chdir(root);
    const proxy = await createServer({ root, configFile: false, logLevel: "silent", plugins: [],
      server: { host: "127.0.0.1", port: 0, proxy: runicDevSessionProxy({ sourceMap: "sources.json" }) } });
    process.chdir(cwd);
    servers.push(proxy);
    for (const server of servers) {
      await server.listen();
      const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
      const get = (file, headers = {}) => fetch(`${origin}/__runic/handlers?file=${encodeURIComponent(file)}`, { headers });
      let response = await get(join(root, "src", "Page.jsx"));
      assert.equal(response.status, 200);
      const body = await response.json();
      assert.equal(body.format, "runic.vite.handlers/1");
      assert.deepEqual(summary(body.bindings), ["button click: save(helper)"]);
      // Relative to the root, as Angular's debug info names a component's file.
      response = await get("src/counter.ts");
      assert.deepEqual(summary((await response.json()).bindings), ["button click: save(local)"]);
      assert.equal((await get("/etc/passwd")).status, 404);
      assert.equal((await get(join(root, "node_modules", "svelte", "package.json"))).status, 404);
      assert.equal((await get(join(root, "src", "Page.jsx"), { "sec-fetch-site": "cross-site" })).status, 403);
      assert.equal((await fetch(`${origin}/__runic/handlers`)).status, 400);
      // The source map for the C# handlers, under both.
      response = await fetch(`${origin}/__runic/sources`);
      assert.equal(response.status, 200);
      assert.equal((await response.json()).members[0].symbol, "Demo.CounterViewModel.Increment()");
    }
    assert.deepEqual(Object.keys(runicDevSessionProxy({ inspect: false, sourceMap: false })).length, 2);
    assert.throws(() => runicDevSessionProxy({ inspect: "no" }), /inspect must be true or false/u);
    // inspect: false: no analysis under runic().
    const off = await createServer({ root, configFile: false, logLevel: "silent", plugins: [runic({ inspect: false })], server: { host: "127.0.0.1", port: 0 } });
    servers.push(off);
    await off.listen();
    const response = await fetch(`http://127.0.0.1:${off.httpServer.address().port}/__runic/handlers?file=${encodeURIComponent(join(root, "src", "Page.jsx"))}`);
    assert.notEqual(response.headers.get("content-type"), "application/json; charset=utf-8");
  } finally {
    process.chdir(cwd);
    for (const server of servers) await server.close();
    await rm(root, { recursive: true, force: true });
  }
});

test("served modules and the build are the same with and without the analysis", async () => {
  const root = await project();
  const directory = await mkdtemp(join(tmpdir(), "runic-handlers-build-"));
  try {
    // Served: the analysis changes no module.
    const served = {};
    for (const [name, options] of [["inspect", {}], ["without-inspect", { inspect: false }]]) {
      const server = await createServer({ root, configFile: false, logLevel: "silent", plugins: [runic(options)], server: { host: "127.0.0.1", port: 0 } });
      try {
        await server.listen();
        const response = await fetch(`http://127.0.0.1:${server.httpServer.address().port}/src/Page.jsx`);
        served[name] = (await response.text()).replace(/\?v=[0-9a-f]+/gu, "");
      } finally { await server.close(); }
    }
    assert.match(served.inspect, /onClick/u);
    assert.equal(served.inspect, served["without-inspect"]);
    // Built: byte-identical.
    const builds = {};
    for (const [name, options] of [["inspect", {}], ["without-inspect", { inspect: false }], ["without-client", { devtools: false }]]) {
      await build({ root, configFile: false, logLevel: "silent", plugins: [runic(options)], build: { outDir: join(directory, name) } });
      builds[name] = await files(join(directory, name));
    }
    assert.ok(Object.keys(builds.inspect).length > 0);
    assert.deepEqual(builds.inspect, builds["without-inspect"]);
    assert.deepEqual(builds.inspect, builds["without-client"]);
    const text = Object.values(builds.inspect).map((content) => content.toString("utf8")).join("\n");
    assert.doesNotMatch(text, /__runic\/handlers|runic\.vite\.inspect/u);
  } finally {
    await rm(directory, { recursive: true, force: true });
    await rm(root, { recursive: true, force: true });
  }
});

async function files(directory) {
  const result = {};
  for (const entry of await readdir(directory, { recursive: true, withFileTypes: true })) {
    if (!entry.isFile()) continue;
    const path = join(entry.parentPath ?? entry.path, entry.name);
    result[relative(directory, path).replaceAll("\\", "/")] = await readFile(path);
  }
  return result;
}
