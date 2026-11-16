// The Runic DevTools extension's data path without a browser (#1044): Runic Inspect's call log and page snapshot
// (inspect-snapshot.ts), the extension's eval protocol against a stand-in page (tools/dotnet-runic/devtools-extension/
// page.js), and the rows and sections its panel and Elements pane draw (model.js).
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";
import { assembleSnapshot, CallLog } from "../dist/inspect-snapshot.js";

const extension = new URL("../../../../tools/dotnet-runic/devtools-extension/", import.meta.url);
const { createEvaluator, startExpression, takeExpression, probeExpression } = await import(new URL("page.js", extension).href);
const { callRows, describeSections, formatDuration, formatValue, locationLabel, shortType, stateMembers, statusMessage, viewRows } =
  await import(new URL("model.js", extension).href);

const file = "/src/Demo/EditorViewModel.cs";
const map = {
  format: "runic-bridge-sources/1",
  viewModels: [
    { id: "Demo.EditorViewModel", name: "Editor", route: "editor", contract: "Demo.EditorViewModel:F1", pageKinds: ["editor"],
      symbol: "Demo.EditorViewModel", location: { file, line: 5, column: 14 } },
  ],
  members: [
    { id: "Demo.EditorViewModel/state/title", viewModel: "Demo.EditorViewModel", member: "title", kind: "state",
      symbol: "Demo.EditorViewModel.Title", location: { file, line: 8, column: 5 } },
    { id: "Demo.EditorViewModel/setter/title", viewModel: "Demo.EditorViewModel", member: "title", kind: "setter",
      symbol: "Demo.EditorViewModel.Title.set", location: { file, line: 8, column: 40 } },
    { id: "Demo.EditorViewModel/command/save", viewModel: "Demo.EditorViewModel", member: "save", kind: "command",
      symbol: "Demo.EditorViewModel.SaveAsync()", property: "Demo.EditorViewModel.SaveCommand", location: { file, line: 42, column: 5 } },
  ],
};

const start = (id, member, kind = "command", extra = {}) => ({
  phase: "start", id, kind, request: kind, route: "editor", contract: "Demo.EditorViewModel:F1", member, start: id * 10, ...extra,
});

test("the call log keeps the newest calls with their outcome and C# handler", () => {
  const log = new CallLog(3);
  log.record(start(1, "save"));
  log.record({ phase: "end", id: 1, duration: 12.5, outcome: "ok", revision: 4,
    handler: { name: "EditorViewModel.SaveAsync", start: 2, end: 9.5, duration: 7.5 } });
  log.record(start(2, "title", "set"));
  log.record({ phase: "end", id: 2, duration: 3, outcome: "cancelled", reason: "superseded" });
  log.record(start(3, "save"));
  // Not a call event: ignored.
  log.record(null);
  log.record({ phase: "end", id: 99, outcome: "ok" });
  log.record({ phase: "start", id: 7 });
  let calls = log.list();
  assert.deepEqual(calls.map((call) => [call.id, call.outcome]), [[3, undefined], [2, "cancelled"], [1, "ok"]]);
  assert.deepEqual(calls[2].handler, { name: "EditorViewModel.SaveAsync", duration: 7.5 });
  assert.equal(calls[2].revision, 4);
  assert.equal(calls[1].reason, "superseded");
  // A handler with only start and end has the difference as its duration.
  log.record({ phase: "end", id: 3, duration: 1, outcome: "error", reason: "domain-failed", handler: { name: "H", start: 1, end: 3.5 } });
  assert.deepEqual(log.list()[0].handler, { name: "H", duration: 2.5 });
  // The oldest call goes when the log is full, and its end is ignored.
  log.record(start(4, "save"));
  log.record({ phase: "end", id: 1, outcome: "error" });
  calls = log.list();
  assert.deepEqual(calls.map((call) => call.id), [4, 3, 2]);
  // Copies: changing them does not change the log.
  calls[0].member = "changed";
  calls[1].handler.name = "changed";
  assert.equal(log.list()[0].member, "save");
  assert.equal(log.list()[1].handler.name, "H");
  log.clear();
  assert.deepEqual(log.list(), []);
});

test("a snapshot names each View's C# ViewModel and each call's C# member", () => {
  const log = new CallLog();
  log.record(start(1, "save"));
  log.record({ phase: "end", id: 1, duration: 12, outcome: "ok", revision: 3, handler: { name: "EditorViewModel.SaveAsync", duration: 8 } });
  log.record(start(2, "title", "set"));
  // A route the source map does not know.
  log.record({ ...start(3, "ping"), route: "other", contract: "Demo.OtherViewModel:F9" });
  const snapshot = assembleSnapshot({
    views: new Map([
      ["editor", { state: { title: "Draft", items: Array.from({ length: 25 }, (_, index) => index), canSave: true }, revision: 3, epoch: 1, presentations: 1 }],
      ["other", { state: { value: 1 } }],
    ]),
    contracts: new Map([["editor", "Demo.EditorViewModel:F1"]]),
    calls: log.list(),
    sourceMap: map,
  });
  assert.equal(snapshot.version, 1);
  assert.deepEqual(snapshot.views[0], {
    route: "editor", revision: 3, epoch: 1, presentations: 1, contract: "Demo.EditorViewModel:F1",
    viewModel: { id: "Demo.EditorViewModel", name: "Editor", symbol: "Demo.EditorViewModel",
      location: { file, line: 5, column: 14, editor: "runic", id: "Demo.EditorViewModel" } },
    state: { title: "Draft", items: [...Array.from({ length: 20 }, (_, index) => index), "… 5 more"], canSave: true },
  });
  assert.deepEqual(snapshot.views[1], { route: "other", state: { value: 1 } });
  assert.deepEqual(snapshot.calls.map((call) => [call.id, call.member, call.symbol, call.location?.line]), [
    [3, "ping", undefined, undefined],
    [2, "title", "Demo.EditorViewModel.Title.set", 8],
    [1, "save", "Demo.EditorViewModel.SaveAsync()", 42],
  ]);
  assert.deepEqual(snapshot.calls[2].handler, { name: "EditorViewModel.SaveAsync", duration: 8 });
  assert.equal(snapshot.calls[2].viewModel, "Demo.EditorViewModel");
  assert.deepEqual(snapshot.notes, []);
  // The snapshot is JSON-safe, as inspectedWindow.eval needs.
  assert.deepEqual(JSON.parse(JSON.stringify(snapshot)), snapshot);
});

test("a snapshot says what it cannot tell", () => {
  const log = new CallLog();
  log.record(start(1, "save"));
  log.record({ phase: "end", id: 1, duration: 2, outcome: "ok" });
  const snapshot = assembleSnapshot({ views: new Map(), contracts: new Map(), calls: log.list(), sourceMapStatus: "missing" });
  assert.equal(snapshot.calls[0].symbol, undefined);
  assert.equal(snapshot.notes.length, 3);
  assert.match(snapshot.notes[0], /No development source map/u);
  assert.match(snapshot.notes[1], /No open View/u);
  assert.match(snapshot.notes[2], /handler-timing\/1/u);
  assert.match(assembleSnapshot({ views: new Map(), contracts: new Map(), calls: [], sourceMapStatus: "forbidden" }).notes[0], /RUNIC_DEV_ALLOW/u);
});

// A page in a VM context: `evaluate` is what inspectedWindow.eval does, the expression's value or the page's exception.
function standInPage(api) {
  const context = vm.createContext({});
  if (api !== undefined) vm.runInContext("globalThis", context)[Symbol.for("runic.vite.inspect")] = api;
  const evaluate = async (expression) => vm.runInContext(expression, context);
  return { context, evaluate };
}

test("the extension starts an API call in the page and takes its JSON result", async () => {
  const opened = [];
  const page = standInPage({
    version: 1,
    snapshot: async () => ({ version: 1, views: [{ route: "editor", state: { title: "Draft" } }], calls: [], notes: [] }),
    describe: async (element) => { if (element === undefined) throw new TypeError("describe needs an element."); return { element }; },
    open: async (location) => { opened.push(location); return true; },
  });
  const evaluator = createEvaluator(page.evaluate, { interval: 1 });
  // Values of the VM's realm compare through JSON, as inspectedWindow.eval passes them.
  assert.deepEqual(JSON.parse(JSON.stringify(await page.evaluate(probeExpression))), { available: true, version: 1, methods: ["snapshot", "describe", "open"] });
  assert.deepEqual(await evaluator.call("snapshot"), {
    status: "ok", value: { version: 1, views: [{ route: "editor", state: { title: "Draft" } }], calls: [], notes: [] },
  });
  // Arguments are expressions in the page: `$0` in DevTools, a JSON literal for a location.
  const location = { file: "/src/A.cs", line: 3, column: 1, editor: "runic", id: "A" };
  assert.deepEqual(await evaluator.call("open", [JSON.stringify(location)]), { status: "ok", value: true });
  assert.deepEqual(JSON.parse(JSON.stringify(opened)), [location]);
  // A rejection is the page's message.
  assert.deepEqual(await evaluator.call("describe", ["undefined"]), { status: "error", error: "describe needs an element." });
  // Results do not stay in the page.
  assert.equal(vm.runInContext(`globalThis[Symbol.for("runic.devtools.results")].size`, page.context), 0);
  // A method the API lacks (an older vite-plugin-runic), and no API at all.
  assert.deepEqual(await createEvaluator(standInPage({ version: 1 }).evaluate).call("snapshot"), { status: "unsupported" });
  assert.deepEqual(await createEvaluator(standInPage().evaluate).call("snapshot"), { status: "missing" });
  assert.deepEqual(JSON.parse(JSON.stringify(await standInPage().evaluate(probeExpression))), { available: false });
});

test("the extension gives up on a call that does not answer, and reports eval failures", async () => {
  let time = 0;
  const page = standInPage({ version: 1, snapshot: () => new Promise(() => {}) });
  const evaluator = createEvaluator(page.evaluate, { timeout: 100, interval: 10, now: () => time, sleep: async (ms) => { time += ms; } });
  assert.deepEqual(await evaluator.call("snapshot"), { status: "error", error: "No answer within 0 s." });
  const failing = createEvaluator(async () => { throw { isException: true, value: "ReferenceError: $0 is not defined" }; });
  assert.deepEqual(await failing.call("describe", ["$0"]), { status: "error", error: "ReferenceError: $0 is not defined" });
  assert.throws(() => startExpression("1", "snapshot()"), /Not an API method/u);
  assert.match(takeExpression("a\"b"), /"a\\"b"/u);
});

test("the panel's rows and the Elements pane's sections", () => {
  assert.equal(formatDuration(0.05), "<0.1 ms");
  assert.equal(formatDuration(4.25), "4.3 ms");
  assert.equal(formatDuration(42.4), "42 ms");
  assert.equal(formatDuration(1520), "1.52 s");
  assert.equal(formatDuration(undefined), "");
  assert.equal(formatValue("x".repeat(200)).length, 120);
  assert.equal(formatValue({ a: 1 }), '{"a":1}');
  assert.equal(locationLabel({ file: "C:\\src\\EditorViewModel.cs", line: 42 }), "EditorViewModel.cs:42");
  assert.equal(shortType("Demo.EditorViewModel:F1"), "EditorViewModel");
  assert.deepEqual(stateMembers(5), [{ name: "(value)", value: "5" }]);
  assert.deepEqual(stateMembers(null), []);
  assert.match(statusMessage("missing"), /vite dev/u);
  assert.match(statusMessage("unsupported"), /0\.21/u);

  const snapshot = {
    version: 1,
    views: [{ route: "editor", revision: 3, presentations: 1, contract: "Demo.EditorViewModel:F1",
      viewModel: { id: "Demo.EditorViewModel", name: "Editor", symbol: "Demo.EditorViewModel", location: { file, line: 5, column: 14, editor: "runic" } },
      state: { title: "Draft", canSave: true } }],
    calls: [
      { id: 2, kind: "set", request: "set", route: "editor", member: "title", start: 5 },
      { id: 1, kind: "command", request: "command", route: "editor", member: "save", start: 1, contract: "Demo.EditorViewModel:F1",
        duration: 12.3, outcome: "error", reason: "domain-failed", revision: 4, handler: { name: "EditorViewModel.SaveAsync", duration: 8 },
        viewModel: "Demo.EditorViewModel", symbol: "Demo.EditorViewModel.SaveAsync()", location: { file, line: 42, column: 5, editor: "runic" } },
    ],
    notes: [],
  };
  assert.deepEqual(viewRows(snapshot), [{
    key: "editor", route: "editor", viewModel: "Editor", symbol: "Demo.EditorViewModel", location: snapshot.views[0].viewModel.location,
    revision: "3", presentations: 1, members: [{ name: "title", value: '"Draft"' }, { name: "canSave", value: "true" }],
  }]);
  const rows = callRows(snapshot);
  assert.deepEqual(rows.map(({ label, outcome, state, duration, handler, revision }) => ({ label, outcome, state, duration, handler, revision })), [
    { label: "editor.title", outcome: "running", state: "running", duration: "", handler: "", revision: "" },
    { label: "EditorViewModel.save", outcome: "error (domain-failed)", state: "error", duration: "12 ms",
      handler: "EditorViewModel.SaveAsync (8.0 ms)", revision: "4" },
  ]);
  assert.equal(rows[1].location.line, 42);

  const sections = describeSections({
    element: { tag: "button", id: "save", classes: ["primary"], text: "Save" },
    component: { framework: "svelte", name: "Toolbar", owners: ["App"], location: { file: "/src/Toolbar.svelte", line: 3, column: 1, editor: "vite" } },
    viewModels: [{ route: "editor", id: "Demo.EditorViewModel", name: "Editor", location: { file, line: 5, column: 14, editor: "runic" } }],
    members: [{ route: "editor", member: "title", kind: "state", evidence: "text", value: "Draft", symbol: "Demo.EditorViewModel.Title" }],
    commands: [{ route: "editor", member: "save", evidence: "call", symbol: "Demo.EditorViewModel.SaveAsync()", calls: 1, canExecute: true,
      location: { file, line: 42, column: 5, editor: "runic" }, frontend: { file: "/src/Toolbar.svelte", line: 9, column: 3, editor: "vite" } }],
    calls: [{ id: 1, kind: "command", request: "command", route: "editor", member: "save", start: 1, duration: 3, outcome: "ok", revision: 4 }],
    texts: [{ route: "editor", path: "status", catalog: "app", key: "status.ready", text: "Ready", arguments: {} }],
    messages: [],
    notes: ["A note."],
  });
  assert.deepEqual(sections.map((section) => section.title), ["Element", "Component", "ViewModels", "Bindings", "Commands", "Recent calls", "Texts", "Notes"]);
  assert.equal(sections[0].items[0].text, "button#save.primary");
  assert.equal(sections[1].items[0].text, "Toolbar (svelte)");
  assert.equal(sections[1].items[0].detail, "in App");
  assert.equal(sections[3].items[0].text, 'title = "Draft"');
  assert.equal(sections[3].items[0].detail, "state · matched by text · Demo.EditorViewModel.Title");
  assert.deepEqual(sections[4].items[0], {
    text: "Demo.EditorViewModel.SaveAsync()", detail: "1 call · can execute: true",
    location: { file, line: 42, column: 5, editor: "runic" }, frontend: { file: "/src/Toolbar.svelte", line: 9, column: 3, editor: "vite" },
  });
  assert.equal(sections[5].items[0].detail, "ok · 3.0 ms · rev 4");
  assert.deepEqual(sections[7].items, [{ text: "A note.", note: true }]);
  assert.deepEqual(describeSections(undefined), []);
});

test("the extension is a Manifest V3 DevTools extension without other permissions", async () => {
  const manifest = JSON.parse(await readFile(new URL("manifest.json", extension), "utf8"));
  assert.equal(manifest.manifest_version, 3);
  assert.equal(manifest.devtools_page, "devtools.html");
  assert.equal(manifest.permissions, undefined);
  assert.equal(manifest.host_permissions, undefined);
  assert.equal(manifest.content_scripts, undefined);
  assert.equal(manifest.background, undefined);
  // The public key fixes the id wherever the folder is: Chromium's id is the key's SHA-256, 32 hex digits as a-p. The
  // build stamps "version" over the "0.0.0" placeholder (Runic.Application.Tool.csproj).
  const id = [...createHash("sha256").update(Buffer.from(manifest.key, "base64")).digest("hex").slice(0, 32)]
    .map((digit) => String.fromCharCode(97 + Number.parseInt(digit, 16))).join("");
  assert.equal(id, "cddbfcinbkaindmfgcfdgapddkjeadgh");
  assert.ok((await readFile(new URL("../../../../docs/debugging.md", import.meta.url), "utf8")).includes(id));
  assert.equal(manifest.version, "0.0.0");
  // The extension's pages load only its own scripts: MV3 allows no inline script.
  for (const page of ["devtools.html", "panel.html", "sidebar.html"]) {
    const html = await readFile(new URL(page, extension), "utf8");
    for (const [, attributes] of html.matchAll(/<script\b([^>]*)>/gu)) assert.match(attributes, /\bsrc="[a-z]+\.js"/u, page);
  }
  // Page data is drawn as text, never as markup.
  for (const script of ["panel.js", "sidebar.js", "render.js"]) {
    assert.doesNotMatch(await readFile(new URL(script, extension), "utf8"), /innerHTML|outerHTML|insertAdjacentHTML/u, script);
  }
});
