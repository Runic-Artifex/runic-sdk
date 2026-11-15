// Bridge calls in Chromium's developer tools under `vite dev`: the injected development client listens to the
// Bridge-call channel of @runic-artifex/views, so a click's command is an entry of the "Bridge calls" track in the
// "Runic" group of a Performance recording, and a breakpoint in the state subscriber the reply notifies has a
// "Runic command …" async stack segment that leads to the click. The page's Bridge answers over a MessageChannel, as
// a host transport does.
import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile, rm, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { chromium } from "playwright-core";
import { createServer } from "vite";
import { runic } from "../dist/index.js";
import { scaled } from "../../../../tests/support/timeouts.mjs";

const views = fileURLToPath(new URL("../../views/", import.meta.url));

const main = `
import { connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";

const channel = new MessageChannel();
const listeners = new Set();
const pending = [];
let revision = 1;
channel.port2.onmessage = ({ data }) => {
  for (const listener of [...listeners]) listener(data.frame);
  pending.shift()(data.reply);
};
globalThis.__runicBridge = {
  isConnected: () => true,
  onConnectionChange(report) { report(true); return () => {}; },
  onMessage(next) { listeners.add(next); return () => { listeners.delete(next); }; },
  request(text) {
    const message = JSON.parse(text);
    const reply = value => Promise.resolve(JSON.stringify(value));
    switch (message.kind) {
      case "hello": return reply({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features: [] });
      case "open": return reply({ ok: true, presentation: "ok", revision, state: { count: 0 } });
      case "command": {
        revision++;
        const frame = JSON.stringify({ kind: "frame", route: "counter", baseRevision: revision - 1, revision,
          changes: [{ field: "count", kind: "set", value: revision - 1 }] });
        setTimeout(() => channel.port1.postMessage({ frame, reply: JSON.stringify({ ok: true, revision }) }), 15);
        return new Promise(resolve => pending.push(resolve));
      }
      default: return reply({ ok: true });
    }
  },
};
const measures = [];
new PerformanceObserver(list => { for (const entry of list.getEntries()) measures.push({ name: entry.name, detail: entry.detail }); })
  .observe({ type: "measure" });
const client = await connectView({ contract: "Demo.CounterViewModel:ABC", route: "counter",
  members: { count: wire => bridgeWire.integer(wire, 0, 100) } });
const test = { measures, seen: [], pause: false };
client.subscribe(state => {
  if (state.count === 0) return;
  test.seen.push(state.count);
  if (test.pause) debugger;
});
document.getElementById("increment").addEventListener("click", function onIncrementClick() { void client.command("increment"); });
globalThis.callsTest = test;
`;

test("vite dev: a click's command is a Runic Performance track entry, and the reply's subscriber has the click's async stack", {
  timeout: scaled(60_000),
  skip: !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH,
}, async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-calls-browser-"));
  let server, browser;
  try {
    await writeFile(join(root, "index.html"),
      '<!doctype html><html><body><button id="increment">Increment</button><script type="module" src="/main.js"></script></body></html>');
    await writeFile(join(root, "main.js"), main);
    await mkdir(join(root, "node_modules", "@runic-artifex"), { recursive: true });
    await symlink(process.cwd(), join(root, "node_modules", "@runic-artifex", "vite-plugin-runic"), "dir");
    await symlink(views, join(root, "node_modules", "@runic-artifex", "views"), "dir");
    server = await createServer({ root, configFile: false, logLevel: "silent", plugins: [runic()],
      server: { host: "127.0.0.1", port: 0, fs: { strict: false } } });
    await server.listen();
    browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, headless: true });
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(String(error)));
    await page.goto(`http://127.0.0.1:${server.httpServer.address().port}`);
    await page.waitForFunction(() => globalThis.callsTest !== undefined, null, { timeout: scaled(20_000) });
    // The injected client listens to the channel.
    assert.equal(await page.evaluate(() => globalThis[Symbol.for("runic.views.calls")]?.requested), true);

    // A Performance recording has the call on the Runic track.
    await browser.startTracing(page, { categories: ["blink.user_timing", "devtools.timeline"] });
    await page.click("#increment");
    await page.waitForFunction(() => globalThis.callsTest.measures.length === 1, null, { timeout: scaled(10_000) });
    const trace = JSON.parse((await browser.stopTracing()).toString("utf8"));
    const entries = trace.traceEvents.filter(event => event.name === "CounterViewModel.increment #1");
    assert.ok(entries.length > 0, "the trace has no Bridge call entry");
    const args = JSON.stringify(entries.map(event => event.args));
    for (const expected of ["track-entry", "Bridge calls", "Runic", "Request id"]) assert.ok(args.includes(expected), `${expected} is not in ${args}`);
    const { measures, seen } = await page.evaluate(() => globalThis.callsTest);
    assert.deepEqual(seen, [1]);
    assert.deepEqual(measures.map(entry => [entry.name, entry.detail.devtools.track, entry.detail.devtools.trackGroup, entry.detail.devtools.color]),
      [["CounterViewModel.increment #1", "Bridge calls", "Runic", "primary"]]);
    // The entry does not stay in the page's timeline buffer.
    assert.equal(await page.evaluate(() => performance.getEntriesByType("measure").length), 0);

    // Paused in the subscriber the reply notifies, the async stack has the call's task, which leads to the click.
    const cdp = await page.context().newCDPSession(page);
    await cdp.send("Debugger.enable");
    await cdp.send("Debugger.setAsyncCallStackDepth", { maxDepth: 32 });
    const paused = new Promise(resolve => {
      cdp.once("Debugger.paused", event => { resolve(event); void cdp.send("Debugger.resume"); });
    });
    await page.evaluate(() => { globalThis.callsTest.pause = true; });
    await page.click("#increment");
    const chain = [];
    for (let stack = (await paused).asyncStackTrace; stack !== undefined; stack = stack.parent)
      chain.push({ description: stack.description, functions: stack.callFrames.map(frame => frame.functionName) });
    const task = chain.find(segment => segment.description === "Runic command CounterViewModel.increment");
    assert.ok(task?.functions.includes("onIncrementClick"), `no Runic task that leads to the click in ${JSON.stringify(chain)}`);
    await cdp.detach();
    assert.deepEqual(errors, []);
  } finally {
    await browser?.close();
    await server?.close();
    await rm(root, { recursive: true, force: true });
  }
});
