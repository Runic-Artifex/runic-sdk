// The development state inspector: View states and superseded command replies from the page
// (`runic.views.inspector`), bounded and redacted, in /__runic/state and the dock.
import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { promisify } from "node:util";
import test from "node:test";
import { createServer } from "vite";
import { runic, runicDevSessionProxy } from "../dist/index.js";
import { sanitizeViewState } from "../dist/diagnostics.js";

const execFile = promisify(execFileCallback);

test("a View state is redacted like timeline details and bounded", () => {
  const state = sanitizeViewState({
    title: "Draft",
    count: 3,
    ratio: Number.NaN,
    done: false,
    missing: null,
    skipped: undefined,
    callback: () => undefined,
    password: "hunter2",
    accessToken: "abc",
    documentPath: "/home/user/notes.txt",
    note: "token=abc123",
    location: "C:\\Users\\me\\notes.txt",
    nested: { items: [{ name: "a" }, { name: "b" }], when: new Date(0) },
    long: "x".repeat(500),
    big: 12n,
  });
  assert.deepEqual(state, {
    title: "Draft",
    count: 3,
    ratio: "NaN",
    done: false,
    missing: null,
    password: "[redacted]",
    accessToken: "[redacted]",
    documentPath: "[redacted]",
    note: "[redacted]",
    location: "[redacted]",
    nested: { items: [{ name: "a" }, { name: "b" }], when: "1970-01-01T00:00:00.000Z" },
    long: `${"x".repeat(199)}…`,
    big: "12n",
  });
  assert.doesNotMatch(JSON.stringify(state), /hunter2|abc123|notes\.txt/u);

  // Large and deep states are cut down to fit, and cycles end.
  const cyclic = { name: "loop" };
  cyclic.self = cyclic;
  assert.deepEqual(sanitizeViewState(cyclic), { name: "loop", self: "[circular]" });
  const rows = Array.from({ length: 10_000 }, (_, index) => ({ index, text: "y".repeat(300) }));
  const bounded = sanitizeViewState({ rows });
  assert.ok(JSON.stringify(bounded).length <= 16_000);
  assert.match(bounded.rows.at(-1), /^… \d+ more$/u);
  let deep = { leaf: true };
  for (let level = 0; level < 20; level++) deep = { deeper: deep };
  assert.match(JSON.stringify(sanitizeViewState(deep)), /"…"/u);
  // Hostile keys are left out and counted.
  const hostile = JSON.parse('{"__proto__":{"polluted":true},"a/b":1,"ok":2}');
  assert.deepEqual(sanitizeViewState(hostile), { ok: 2, "…": "2 more" });
  assert.equal({}.polluted, undefined);
});

test("the Angular proxy keeps View states per page, re-sanitizes them, and forgets closed Views and pages", async () => {
  const server = await createServer({ configFile: false, logLevel: "silent", plugins: [],
    server: { host: "127.0.0.1", port: 0, strictPort: false, proxy: runicDevSessionProxy() } });
  try {
    await server.listen();
    const base = `http://127.0.0.1:${server.httpServer.address().port}`;
    const post = async (event, data) => {
      const response = await fetch(`${base}/__runic/diagnostics`, {
        method: "POST", headers: { "Content-Type": "application/json", Origin: base },
        body: JSON.stringify({ event, data }),
      });
      await response.arrayBuffer();
      return response.status;
    };
    const state = () => fetch(`${base}/__runic/state`).then((response) => response.json());
    assert.deepEqual((await state()).views, []);

    assert.equal(await post("runic:view", { page: "p1", route: "wizard", revision: 4, epoch: 3, presentations: 1,
      state: { page: 2, password: "hunter2" } }), 204);
    assert.equal(await post("runic:view", { page: "p2", route: "wizard", revision: 1, presentations: 2, state: { page: 1 } }), 204);
    assert.equal(await post("runic:view", { page: "p1", route: "editor", revision: 9, presentations: 1, state: { title: "a" } }), 204);
    // Invalid reports are answered but change nothing.
    for (const invalid of [{ page: "P 1", route: "x", state: {} }, { page: "p1", route: "../x", state: {} }, { page: "p1", route: "x" },
      { page: "p1", route: "../x", closed: true }]) {
      assert.equal(await post("runic:view", invalid), 204);
    }
    let views = (await state()).views;
    assert.deepEqual(views.map((view) => `${view.page}:${view.route}`), ["p1:editor", "p2:wizard", "p1:wizard"]);
    const wizard = views[2];
    assert.deepEqual([wizard.revision, wizard.epoch, wizard.presentations], [4, 3, 1]);
    assert.deepEqual(wizard.state, { page: 2, password: "[redacted]" }, "the server sanitizes again");
    assert.equal("epoch" in views[1], false);

    // A superseded command is a timeline entry.
    assert.equal(await post("runic:diagnostic", { source: "views", kind: "command", label: "next superseded",
      detail: { code: "superseded", route: "wizard", member: "next", epoch: 3, revision: 4 } }), 204);
    assert.deepEqual((await state()).timeline.map((entry) => [entry.kind, entry.label, entry.detail.code]),
      [["command", "next superseded", "superseded"]]);

    assert.equal(await post("runic:view", { page: "p1", route: "editor", closed: true }), 204);
    assert.deepEqual((await state()).views.map((view) => `${view.page}:${view.route}`), ["p2:wizard", "p1:wizard"]);
    assert.equal(await post("runic:view", { page: "p1", closed: true }), 204);
    views = (await state()).views;
    assert.deepEqual(views.map((view) => `${view.page}:${view.route}`), ["p2:wizard"]);

    // At most 32 Views, the most recently changed.
    for (let index = 0; index < 40; index++) {
      await post("runic:view", { page: "p3", route: `route${index}`, presentations: 1, state: { index } });
    }
    views = (await state()).views;
    assert.equal(views.length, 32);
    assert.equal(views[0].route, "route39");
  } finally { await server.close(); }
});

test("the dock shows each View's state and the superseded commands", async () => {
  const plugin = runic();
  const specs = [];
  await plugin.devtools.setup({
    rpc: { sharedState: { get: async (_key, options) => ({ value: () => options.initialValue, mutate: () => undefined }) } },
    createJsonRenderer: (spec) => { specs.push(spec); return { view: {}, updateSpec: (next) => specs.push(next) }; },
    docks: { register: () => undefined },
    commands: { register: () => undefined },
  });
  assert.equal(specs[0].elements.views.children[0], "views-none");
  const server = await createServer({ configFile: false, logLevel: "silent", plugins: [plugin],
    server: { host: "127.0.0.1", port: 0, strictPort: false } });
  try {
    await server.listen();
    const base = `http://127.0.0.1:${server.httpServer.address().port}`;
    const post = (event, data) => fetch(`${base}/__runic/diagnostics`, {
      method: "POST", headers: { "Content-Type": "application/json", Origin: base }, body: JSON.stringify({ event, data }),
    }).then((response) => response.arrayBuffer());
    await post("runic:view", { page: "p1", route: "wizard", revision: 4, epoch: 3, presentations: 1, state: { page: 2 } });
    await post("runic:diagnostic", { source: "views", kind: "command", label: "next superseded",
      detail: { code: "superseded", route: "wizard", member: "next", epoch: 2, revision: 4 } });
    const spec = specs.at(-1);
    assert.deepEqual(spec.elements.views.children, ["views-0"]);
    assert.equal(spec.elements["views-0"].props.title, "wizard · revision 4 · epoch 3");
    assert.deepEqual(spec.elements["views-0-state"], { type: "Tree", props: { data: { page: 2 }, defaultExpanded: true } });
    assert.deepEqual(Object.values(spec.elements["superseded-data"].props.data), ["wizard.next from epoch 2"]);
  } finally { await server.close(); }
});

test("the client sends each View's latest state, closed Views and pages, and superseded commands", async () => {
  const harness = String.raw`
import { readFile } from "node:fs/promises";
import { createContext, SourceTextModule } from "node:vm";

const sent = [];
const listeners = new Map();
const hot = { data: {}, send(event, data) { sent.push({ event, data }); }, on() {}, prune() {} };
const context = createContext({
  setTimeout, clearTimeout, Promise,
  addEventListener(type, listener) { listeners.set(type, listener); },
  removeEventListener(type, listener) { if (listeners.get(type) === listener) listeners.delete(type); },
});
context[Symbol.for("runic.vite.client")] = { channel: "hot" };
const modules = new Map();
const load = async (path, identifier, meta) => {
  const module = new SourceTextModule(await readFile(path, "utf8"), { context, identifier, ...(meta ? { initializeImportMeta: meta } : {}) });
  modules.set(identifier, module);
  return module;
};
await load("dist/diagnostics.js", "diagnostics");
await load("dist/stale-input.js", "stale-input");
await load("dist/channel.js", "channel");
const client = await load("dist/client.js", "client", (meta) => { meta.hot = hot; });
await client.link((specifier) => modules.get(specifier.replace("./", "").replace(".js", "")));
await client.evaluate();
const inspector = context[Symbol.for("runic.views.inspector")];
const emit = (event) => { for (const listener of [...inspector.listeners]) listener(event); };
const requested = inspector.requested;
emit({ kind: "state", route: "wizard", revision: 1, epoch: 1, presentations: 1, state: { page: 1 } });
emit({ kind: "state", route: "wizard", revision: 2, epoch: 1, presentations: 1, state: { page: 2, secret: "s3cr3t" } });
emit({ kind: "superseded", route: "wizard", member: "next", epoch: 1, revision: 2 });
const beforeInterval = sent.filter((entry) => entry.event === "runic:view").length;
await new Promise((resolve) => setTimeout(resolve, 400));
emit({ kind: "state", route: "editor", revision: 1, presentations: 1, state: { title: "a" } });
emit({ kind: "closed", route: "editor" });
listeners.get("pagehide")();
await new Promise((resolve) => setTimeout(resolve, 400));
process.stdout.write(JSON.stringify({ requested, beforeInterval, listeners: inspector.listeners.size, sent }));
`;
  const { stdout } = await execFile(process.execPath, ["--experimental-vm-modules", "--input-type=module", "--eval", harness],
    { cwd: process.cwd() });
  const result = JSON.parse(stdout);
  assert.equal(result.requested, true);
  assert.equal(result.listeners, 1);
  assert.equal(result.beforeInterval, 0, "states wait for the interval");
  const views = result.sent.filter((entry) => entry.event === "runic:view").map((entry) => entry.data);
  const page = views[0].page;
  assert.match(page, /^[a-z0-9]{1,32}$/u);
  assert.deepEqual(views, [
    // Only the latest state of the interval, sanitized in the page.
    { page, route: "wizard", revision: 2, epoch: 1, presentations: 1, state: { page: 2, secret: "[redacted]" } },
    // A View that closed before the interval ended sends no state.
    { page, route: "editor", closed: true },
    { page, closed: true },
  ]);
  const superseded = result.sent.filter((entry) => entry.event === "runic:diagnostic").map((entry) => entry.data);
  assert.deepEqual(superseded, [{
    source: "views", kind: "command", label: "next superseded",
    detail: { code: "superseded", route: "wizard", member: "next", epoch: 1, revision: 2 },
  }]);
});
