import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { chromium, type Browser, type CDPSession, type Page } from "playwright-core";

// Custom object formatters (#1042) in Chromium with "Enable custom formatters" on (the DevTools setting, which CDP's
// `Runtime.setCustomObjectFormatterEnabled` sets): the development build shows a View client, its state and a field's
// state readably in the console and the Sources panel. The production build registers no formatter.
const dist = new URL("../dist/", import.meta.url);

const page = `<!doctype html><html><body>
<script type="module">
const build = new URLSearchParams(location.search).has("production") ? "/dist/" : "/dist/development/";
const { connectView } = await import(build + "generated.js");
const { createFieldController } = await import(build + "index.js");
const bridgeWire = await import(build + "wire.js");
globalThis.__runicBridge = {
  isConnected: () => true,
  onConnectionChange(report) { report(true); return () => {}; },
  onMessage() { return () => {}; },
  request(text) {
    const message = JSON.parse(text);
    const reply = value => Promise.resolve(JSON.stringify(value));
    switch (message.kind) {
      case "hello": return reply({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features: [] });
      case "open": return reply({ ok: true, presentation: "ok", revision: 7, state: { title: "Draft", count: 3, tags: ["a", "b"], owner: { name: "Ada" } } });
      default: return reply({ ok: true });
    }
  },
};
const members = { title: bridgeWire.string, count: wire => bridgeWire.integer(wire, 0, 100), tags: wire => wire, owner: wire => wire };
const client = await connectView({ contract: "Demo.Notes.NotesViewModel:ABC", route: "notes", members });
const field = createFieldController({ name: "title", kind: "text", read: state => state.title, write: (c, value) => c.set("title", value) });
field.setSource({ state: client.snapshot, client });
globalThis.formattersTest = { client, state: client.snapshot, field: field.current, plain: { title: "Draft" } };
</script></body></html>`;

const chromiumPath = process.env["PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH"];

async function open(browser: Browser, query: string): Promise<{ tab: Page; cdp: CDPSession; errors: string[] }> {
  const tab = await browser.newPage();
  const errors: string[] = [];
  tab.on("pageerror", error => errors.push(String(error)));
  await tab.route("http://localhost/**", async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/") return route.fulfill({ contentType: "text/html", body: page });
    if (path.startsWith("/dist/") && !path.includes(".."))
      return route.fulfill({ contentType: "text/javascript", body: await readFile(new URL(path.slice("/dist/".length), dist), "utf8") });
    return route.fulfill({ status: 404, body: "" });
  });
  await tab.goto(`http://localhost/${query}`);
  await tab.waitForFunction(() => (globalThis as { formattersTest?: unknown }).formattersTest !== undefined);
  const cdp = await tab.context().newCDPSession(tab);
  await cdp.send("Runtime.enable");
  await cdp.send("Runtime.setCustomObjectFormatterEnabled", { enabled: true });
  return { tab, cdp, errors };
}

// The text of a JsonML tree, without its styles and child objects.
function text(node: unknown): string {
  if (!Array.isArray(node)) return typeof node === "string" ? node : "";
  if (node[0] === "object") return "<object>";
  return node.slice(2).map(text).join("");
}

// What DevTools shows for an expression: the formatter's header and, expanded, its body (or none without one).
async function show(cdp: CDPSession, expression: string): Promise<{ header?: string; body?: string; objects?: number }> {
  const { result } = await cdp.send("Runtime.evaluate", { expression, generatePreview: true }) as {
    result: { customPreview?: { header: string; bodyGetterId?: string } };
  };
  const preview = result.customPreview;
  if (!preview) return {};
  const header = text(JSON.parse(preview.header));
  if (!preview.bodyGetterId) return { header };
  const body = await cdp.send("Runtime.callFunctionOn", {
    objectId: preview.bodyGetterId, functionDeclaration: "function () { return JSON.stringify(this(), (key, value) => key === 'object' ? '<object>' : value); }",
    returnByValue: true,
  }) as { result: { value: string } };
  const tree = JSON.parse(body.result.value) as unknown;
  return { header, body: text(tree), objects: (body.result.value.match(/<object>/g) ?? []).length };
}

test("Chromium with custom formatters: a client, its state and a field's state show readably", { timeout: 60_000, skip: !chromiumPath }, async () => {
  let browser: Browser | undefined;
  try {
    browser = await chromium.launch({ executablePath: chromiumPath!, headless: true });
    const { cdp, errors } = await open(browser, "");
    const client = await show(cdp, "formattersTest.client");
    assert.equal(client.header, "NotesViewModel · notes · rev 7");
    assert.match(client.body!, /view model: "NotesViewModel"/);
    assert.match(client.body!, /route: "notes"/);
    assert.match(client.body!, /contract: "Demo.Notes.NotesViewModel:ABC"/);
    assert.match(client.body!, /revision: 7/);
    assert.match(client.body!, /presentations: 1/);
    assert.match(client.body!, /state: <object>/);
    assert.match(client.body!, /client: <object>/);

    const state = await show(cdp, "formattersTest.state");
    assert.equal(state.header, 'NotesViewModel state · rev 7 {title: "Draft", count: 3, tags: Array(2), …}');
    assert.match(state.body!, /title: "Draft"count: 3tags: <object>owner: <object>route notes, contract Demo.Notes.NotesViewModel:ABC/);

    const field = await show(cdp, "formattersTest.field");
    assert.equal(field.header, 'title = "Draft"');
    assert.match(field.body!, /value: "Draft"/);
    assert.match(field.body!, /editing: false/);

    // Other objects keep DevTools' own presentation.
    assert.deepEqual(await show(cdp, "formattersTest.plain"), {});
    assert.deepEqual(await show(cdp, "formattersTest.state.owner"), {});
    // The client's raw view, for its members, does not use the formatter again.
    assert.deepEqual(errors, []);
    await cdp.detach();
  } finally {
    await browser?.close();
  }
});

test("Chromium: the production build registers no formatter", { timeout: 60_000, skip: !chromiumPath }, async () => {
  let browser: Browser | undefined;
  try {
    browser = await chromium.launch({ executablePath: chromiumPath!, headless: true });
    const { tab, cdp, errors } = await open(browser, "?production");
    assert.equal(await tab.evaluate(() => "devtoolsFormatters" in globalThis), false);
    assert.deepEqual(await show(cdp, "formattersTest.client"), {});
    assert.deepEqual(await show(cdp, "formattersTest.state"), {});
    assert.deepEqual(errors, []);
    await cdp.detach();
  } finally {
    await browser?.close();
  }
});
