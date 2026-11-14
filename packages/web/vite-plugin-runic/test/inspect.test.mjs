// Runic Inspect's data: the overview of an element assembled from learned bindings, View states, the development
// source map and the element's framework component; the framework lookups on stand-in nodes; stack and source map
// decoding; and a build that is the same with and without Runic Inspect.
import assert from "node:assert/strict";
import { mkdtemp, readdir, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { build } from "vite";
import { runic } from "../dist/index.js";
import {
  assembleDescription, findTextReferences, isTextReference, memberKinds, normalizeText, previewValue, viewModelForRoute,
  indexSourceMap,
} from "../dist/inspect-data.js";
import {
  absolutePath, angularComponent, applicationFrame, decodeMappings, findComponent, findComponentSync, inlineSourceMap,
  originalPosition, parseStack, sourceFile, svelteComponent, vueComponent,
} from "../dist/inspect-frameworks.js";

const file = "/src/Demo/EditorViewModel.cs";
const map = {
  format: "runic-bridge-sources/1",
  viewModels: [
    { id: "Demo.EditorViewModel", name: "Editor", route: "editor", contract: "Demo.EditorViewModel:F1", pageKinds: ["editor"],
      symbol: "Demo.EditorViewModel", location: { file, line: 5, column: 14 } },
    { id: "Demo.CounterViewModel", name: "Counter", route: "counter", contract: "Demo.CounterViewModel:F2", pageKinds: ["counter"],
      symbol: "Demo.CounterViewModel" },
  ],
  members: [
    { id: "Demo.EditorViewModel/state/title", viewModel: "Demo.EditorViewModel", member: "title", kind: "state",
      symbol: "Demo.EditorViewModel.Title", location: { file, line: 8, column: 5 } },
    { id: "Demo.EditorViewModel/setter/title", viewModel: "Demo.EditorViewModel", member: "title", kind: "setter",
      symbol: "Demo.EditorViewModel.Title.set", location: { file, line: 8, column: 40 } },
    { id: "Demo.EditorViewModel/state/status", viewModel: "Demo.EditorViewModel", member: "status", kind: "state",
      symbol: "Demo.EditorViewModel.Status" },
    { id: "Demo.EditorViewModel/command/save", viewModel: "Demo.EditorViewModel", member: "save", kind: "command",
      symbol: "Demo.EditorViewModel.SaveAsync()", property: "Demo.EditorViewModel.SaveCommand", location: { file, line: 42, column: 5 } },
    { id: "Demo.EditorViewModel/commandState/canSave", viewModel: "Demo.EditorViewModel", member: "canSave", kind: "commandState",
      symbol: "Demo.EditorViewModel.SaveAsync()", location: { file, line: 42, column: 5 } },
    { id: "Demo.CounterViewModel/state/count", viewModel: "Demo.CounterViewModel", member: "count", kind: "state",
      symbol: "Demo.CounterViewModel.Count" },
  ],
};
const status = { version: 2, catalog: "app", contractFingerprint: "F", key: "editor.saved", arguments: { name: "a.txt" }, fallbackText: "Saved" };

function element(overrides = {}) {
  return { tag: "button", classes: [], text: "Save", ownText: "Save", interactive: true, ...overrides };
}

function input(overrides = {}) {
  return {
    element: element(), learned: [], calls: [], states: new Map(), contracts: new Map(), sourceMap: map,
    component: { framework: "react", name: "Editor", owners: [] }, ...overrides,
  };
}

test("a learned command shows its C# handler, availability and calls", () => {
  const calls = [
    { id: 3, kind: "command", request: "command", route: "editor", member: "save", start: 10, duration: 12.5, outcome: "ok", revision: 7 },
    { id: 1, kind: "command", request: "command", route: "editor", member: "save", start: 1, duration: 3, outcome: "error", reason: "domain-failed" },
  ];
  const description = assembleDescription(input({
    learned: [{ route: "editor", contract: "Demo.EditorViewModel:F1", kind: "command", member: "save" }],
    calls,
    states: new Map([["editor", { title: "Draft", status, canSave: false }]]),
    contracts: new Map([["editor", "Demo.EditorViewModel:F1"]]),
  }));
  assert.deepEqual(description.commands, [{
    route: "editor", viewModel: "Demo.EditorViewModel", member: "save", symbol: "Demo.EditorViewModel.SaveAsync()",
    property: "Demo.EditorViewModel.SaveCommand",
    location: { file, line: 42, column: 5, editor: "runic", id: "Demo.EditorViewModel/command/save" },
    canExecute: false, calls: 2,
  }]);
  assert.deepEqual(description.viewModels, [{
    route: "editor", id: "Demo.EditorViewModel", name: "Editor", contract: "Demo.EditorViewModel:F1",
    location: { file, line: 5, column: 14, editor: "runic", id: "Demo.EditorViewModel" },
  }]);
  assert.deepEqual(description.calls, calls);
  assert.deepEqual(description.members, []);
  assert.deepEqual(description.notes, []);
  assert.deepEqual(description.element, { tag: "button", classes: [], text: "Save" });
});

test("a learned setter shows the member's value and C# setter", () => {
  const description = assembleDescription(input({
    element: element({ tag: "input", text: "", ownText: "", value: "Draft 2" }),
    learned: [{ route: "editor", contract: "Demo.EditorViewModel:F1", kind: "write", member: "title" }],
    states: new Map([["editor", { title: "Draft 2", status }]]),
    contracts: new Map([["editor", "Demo.EditorViewModel:F1"]]),
  }));
  // The call is exact, so the value match does not list the member again.
  assert.deepEqual(description.members, [{
    route: "editor", viewModel: "Demo.EditorViewModel", member: "title", kind: "setter", evidence: "call", value: "Draft 2",
    symbol: "Demo.EditorViewModel.Title.set",
    location: { file, line: 8, column: 40, editor: "runic", id: "Demo.EditorViewModel/setter/title" },
  }]);
  assert.deepEqual(description.commands, []);
});

test("an element that shows a state value or a ViewModel text lists it as a match", () => {
  const states = new Map([["content1", { title: "Draft", status, canSave: true }], ["counter", { count: 4 }]]);
  const resolveText = (reference) => reference.key === "editor.saved" ? { text: `Saved ${reference.arguments.name}`, locale: "en" } : undefined;
  let description = assembleDescription(input({
    element: element({ tag: "h1", text: "Draft", ownText: " Draft\n", interactive: false }), states, resolveText,
  }));
  // `content1` is not a route the map knows: the ViewModel whose state members are the state's keys is.
  assert.deepEqual(description.members, [{
    route: "content1", viewModel: "Demo.EditorViewModel", member: "title", kind: "state", evidence: "text", value: "Draft",
    symbol: "Demo.EditorViewModel.Title", location: { file, line: 8, column: 5, editor: "runic", id: "Demo.EditorViewModel/state/title" },
  }]);
  assert.equal(description.viewModels[0].id, "Demo.EditorViewModel");
  assert.deepEqual(description.notes, []);

  description = assembleDescription(input({
    element: element({ tag: "p", text: "Status: Saved a.txt", ownText: "Status: Saved a.txt", interactive: false }), states, resolveText,
  }));
  assert.deepEqual(description.texts, [{ route: "content1", path: "status", catalog: "app", key: "editor.saved", locale: "en",
    text: "Saved a.txt", arguments: { name: "a.txt" } }]);
  assert.deepEqual(description.members, []);
  // Without a Translations runtime, the reference's fallback text.
  description = assembleDescription(input({
    element: element({ tag: "p", text: "Saved", ownText: "Saved", interactive: false }), states,
  }));
  assert.deepEqual(description.texts.map((text) => [text.key, text.text, text.locale]), [["editor.saved", "Saved", undefined]]);

  description = assembleDescription(input({ element: element({ tag: "span", text: "4", ownText: "4", interactive: false }), states }));
  assert.deepEqual(description.members.map((member) => [member.route, member.member, member.value, member.viewModel]),
    [["counter", "count", 4, "Demo.CounterViewModel"]]);
});

test("notes say what the overview cannot tell", () => {
  const description = assembleDescription(input({ sourceMap: undefined, sourceMapStatus: "missing", component: undefined }));
  assert.equal(description.notes.length, 3);
  assert.match(description.notes[0], /No Bridge source map/u);
  assert.match(description.notes[1], /No Bridge call from this element yet/u);
  assert.match(description.notes[2], /No framework component/u);
  assert.equal(description.component, undefined);
  // Without a map, a learned command still shows the route and member.
  const learned = assembleDescription(input({
    sourceMap: undefined, sourceMapStatus: "unavailable",
    learned: [{ route: "editor", contract: "Demo.EditorViewModel:F1", kind: "command", member: "save" }],
    contracts: new Map([["editor", "Demo.EditorViewModel:F1"]]),
  }));
  assert.deepEqual(learned.commands, [{ route: "editor", member: "save", calls: 0 }]);
  assert.deepEqual(learned.viewModels, [{ route: "editor", contract: "Demo.EditorViewModel:F1" }]);
  assert.match(learned.notes[0], /does not serve the Bridge source map/u);
});

test("ViewModels resolve by contract, route, then state members", () => {
  const index = indexSourceMap(map);
  assert.equal(viewModelForRoute(index, "anything", "Demo.CounterViewModel:F2", undefined)?.id, "Demo.CounterViewModel");
  assert.equal(viewModelForRoute(index, "editor", undefined, undefined)?.id, "Demo.EditorViewModel");
  assert.equal(viewModelForRoute(index, "content3", undefined, { count: 1 })?.id, "Demo.CounterViewModel");
  assert.equal(viewModelForRoute(index, "content3", undefined, { count: 1, other: 2 }), undefined);
  assert.equal(viewModelForRoute(index, "content3", undefined, undefined), undefined);
  assert.deepEqual(memberKinds("set"), ["setter", "state"]);
  assert.deepEqual(memberKinds("operation"), ["command"]);
});

test("values and texts are shortened, and text references found", () => {
  assert.equal(normalizeText("  a \n\t b  "), "a b");
  assert.equal(normalizeText("x".repeat(300)).length, 200);
  assert.deepEqual(previewValue({ a: 1n, b: Number.NaN, c: () => 1, d: [1, 2], e: { f: { g: { h: 1 } } }, s: "y".repeat(500) }),
    { a: "1n", b: "NaN", d: [1, 2], e: { f: { g: "{…}" } }, s: `${"y".repeat(199)}…` });
  assert.equal(previewValue(Array.from({ length: 25 }, (_, index) => index)).at(-1), "… 5 more");
  assert.equal(isTextReference(status), true);
  assert.equal(isTextReference({ ...status, key: "" }), false);
  assert.deepEqual(findTextReferences({ status, items: [{ label: status }, { label: "plain" }] }).map((found) => found.path),
    ["status", "items.0.label"]);
});

test("stack frames of V8 and JavaScriptCore, and the first application frame", () => {
  const v8 = `Error: react-stack-top-frame
    at exports.jsxDEV (http://127.0.0.1:5173/node_modules/.vite/deps/react_jsx-dev-runtime.js?v=1:250:9)
    at CounterPage (http://127.0.0.1:5173/src/pages/CounterPage.tsx?t=17:23:12)
    at http://127.0.0.1:5173/src/main.tsx:4:1`;
  assert.deepEqual(parseStack(v8).map((frame) => [frame.name, frame.line, frame.column]),
    [["exports.jsxDEV", 250, 9], ["CounterPage", 23, 12], ["", 4, 1]]);
  assert.deepEqual(applicationFrame(parseStack(v8)), {
    name: "CounterPage", url: "http://127.0.0.1:5173/src/pages/CounterPage.tsx?t=17", line: 23, column: 12,
  });
  const jsc = `jsxDEV@http://localhost:5173/node_modules/.vite/deps/react_jsx-dev-runtime.js?v=1:250:22
CounterPage@http://localhost:5173/src/pages/CounterPage.tsx:23:30
renderWithHooks@http://localhost:5173/node_modules/.vite/deps/react-dom_client.js:10:1`;
  assert.deepEqual(applicationFrame(parseStack(jsc)), {
    name: "CounterPage", url: "http://localhost:5173/src/pages/CounterPage.tsx", line: 23, column: 30,
  });
});

test("source maps: mappings, original positions and inline maps", () => {
  // Line 1 maps to source 0 line 1; line 2 has two segments: column 0 → line 2 column 0, column 4 → line 2 column 4.
  const sourceMapValue = { version: 3, sources: ["CounterPage.tsx"], mappings: "AAAA;AACA,IAAI" };
  assert.deepEqual(decodeMappings(sourceMapValue.mappings), [[[0, 0, 0, 0]], [[0, 0, 1, 0], [4, 0, 1, 4]]]);
  assert.deepEqual(originalPosition(sourceMapValue, 2, 7), { source: "CounterPage.tsx", line: 2, column: 5 });
  assert.deepEqual(originalPosition(sourceMapValue, 2, 2), { source: "CounterPage.tsx", line: 2, column: 1 });
  assert.equal(originalPosition(sourceMapValue, 9, 1), undefined);
  const code = `export const a = 1;\n//# sourceMappingURL=data:application/json;base64,${Buffer.from(JSON.stringify(sourceMapValue)).toString("base64")}\n`;
  assert.deepEqual(inlineSourceMap(code)?.sources, ["CounterPage.tsx"]);
  assert.equal(inlineSourceMap("export {};"), undefined);
  // Vite names absolute files; a relative source resolves below the root; /@fs/ is a file path.
  assert.equal(sourceFile("/app/src/A.tsx", "http://h/src/A.tsx", "/app"), "/app/src/A.tsx");
  assert.equal(sourceFile("A.tsx", "http://h/src/A.tsx", "/app"), "/app/src/A.tsx");
  assert.equal(sourceFile("../../lib/B.tsx", "http://h/@fs/repo/lib/x/A.tsx", "/app"), "/repo/lib/B.tsx");
  assert.equal(absolutePath("src/App.svelte", "/app/"), "/app/src/App.svelte");
  assert.equal(absolutePath("C:\\app\\x.ts", "/app"), "C:\\app\\x.ts");
});

test("React: the component, its owners, and the element's JSX location through the module's source map", async () => {
  function CounterPage() {}
  function App() {}
  const appFiber = { tag: 0, type: App, return: { tag: 3, type: null, return: null } };
  const pageFiber = { tag: 0, type: CounterPage, return: { tag: 7, type: Symbol.for("react.fragment"), return: appFiber } };
  const button = {
    "__reactFiber$abc": {
      tag: 5, type: "button", return: { tag: 5, type: "section", return: pageFiber },
      _debugStack: { stack: "Error: react-stack-top-frame\n    at jsxDEV (http://h/node_modules/.vite/deps/react.js:1:1)\n    at CounterPage (http://h/src/CounterPage.tsx:2:7)" },
    },
    parentElement: null,
  };
  const sourceMapValue = { version: 3, sources: ["/app/src/CounterPage.tsx"], mappings: "AAAA;AACA,IAAI" };
  const module = `code\n//# sourceMappingURL=data:application/json;base64,${Buffer.from(JSON.stringify(sourceMapValue)).toString("base64")}`;
  const fetched = [];
  const component = await findComponent({ parentElement: button }, {
    root: "/app", fetchText: async (url) => { fetched.push(url); return module; },
  });
  assert.deepEqual(component, {
    framework: "react", name: "CounterPage", owners: ["App"],
    location: { file: "/app/src/CounterPage.tsx", line: 2, column: 5, editor: "vite" },
  });
  assert.deepEqual(fetched, ["http://h/src/CounterPage.tsx"]);
  // Without a source map, the served path below the root.
  const fallback = await findComponent(button, { root: "/app", fetchText: async () => undefined });
  assert.deepEqual(fallback.location, { file: "/app/src/CounterPage.tsx", line: 2, column: 7, editor: "vite" });
  assert.equal(findComponentSync(button, "/app", undefined)?.component.name, "CounterPage");
});

test("Vue, Svelte and Angular components from their development metadata", () => {
  const vue = vueComponent({
    __vueParentComponent: { type: { __name: "CounterPage", __file: "/app/src/pages/CounterPage.vue" },
      parent: { type: { name: "App" }, parent: { type: {} } } },
  });
  assert.deepEqual(vue, { framework: "vue", name: "CounterPage", owners: ["App"],
    location: { file: "/app/src/pages/CounterPage.vue", line: 1, column: 1, editor: "vite" } });
  assert.equal(vueComponent({ __vueParentComponent: { type: { __file: "/app/src/Panel.vue" } } }).name, "Panel");

  const svelte = svelteComponent({
    __svelte_meta: { loc: { file: "src/pages/CounterPage.svelte", line: 12, column: 2 },
      parent: { type: "component", file: "src/App.svelte", parent: null } },
  }, "/app");
  assert.deepEqual(svelte, { framework: "svelte", name: "CounterPage", owners: ["App"],
    location: { file: "/app/src/pages/CounterPage.svelte", line: 12, column: 3, editor: "vite" } });

  class CounterPageComponent {
    static ɵcmp = { debugInfo: { className: "CounterPageComponent", filePath: "src/app/counter-page.component.ts", lineNumber: 9 } };
  }
  class AppComponent {}
  const page = new CounterPageComponent();
  const app = new AppComponent();
  const appHost = { parentElement: { parentElement: null } };
  const pageHost = { parentElement: { owner: app } };
  const button = { owner: page };
  const ng = {
    getComponent: () => null,
    getOwningComponent: (node) => node.owner ?? null,
    getHostElement: (component) => component === page ? pageHost : component === app ? appHost : null,
  };
  assert.deepEqual(angularComponent(button, ng, "/app"), { framework: "angular", name: "CounterPageComponent", owners: ["AppComponent"],
    location: { file: "/app/src/app/counter-page.component.ts", line: 10, column: 1, editor: "vite" } });
  assert.equal(angularComponent(button, undefined, "/app"), undefined);
  // The nearest ancestor with metadata.
  assert.equal(findComponentSync({ parentElement: { parentElement: { __svelte_meta: { loc: { file: "src/A.svelte", line: 1, column: 0 } } } } },
    "/app", undefined)?.component.name, "A");
});

test("vite build output is byte-identical with and without Runic Inspect", async () => {
  const root = fileURLToPath(new URL("./fixtures/vite/", import.meta.url));
  const directory = await mkdtemp(join(tmpdir(), "runic-inspect-build-"));
  const builds = {};
  try {
    for (const [name, options] of [["inspect", {}], ["without-inspect", { inspect: false }], ["without-client", { devtools: false }]]) {
      await build({ root, configFile: false, logLevel: "silent", plugins: [runic(options)], build: { outDir: join(directory, name) } });
      builds[name] = await files(join(directory, name));
    }
    assert.ok(Object.keys(builds.inspect).length > 0);
    assert.deepEqual(builds.inspect, builds["without-inspect"]);
    assert.deepEqual(builds.inspect, builds["without-client"]);
    const text = Object.values(builds.inspect).map((content) => content.toString("utf8")).join("\n");
    assert.doesNotMatch(text, /runic\.vite\.inspect|runic-inspect|Runic Inspect|__open-in-editor|__runic\//u);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test("inspect: false tells the development client to leave Runic Inspect out", () => {
  assert.throws(() => runic({ inspect: "yes" }), /inspect must be true or false/u);
  const options = (plugin) => {
    const host = {};
    new Function("globalThis", plugin.load("\0virtual:runic/client-options"))(host);
    return host[Symbol.for("runic.vite.client")];
  };
  assert.equal(options(runic({ inspect: false })).inspect, false);
  assert.equal("inspect" in options(runic()), false);
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
