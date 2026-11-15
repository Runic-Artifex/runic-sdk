import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { chromium, type Browser, type Page } from "playwright-core";

// The development Bridge-call channel in Chromium, of the development build: while a reader listens, a click's command is reported, and a
// breakpoint in the state subscriber the reply notifies has a "Runic command …" segment that leads to the click
// (console.createTask). Without a reader there is neither. Drawing the Performance track is the reader's
// (vite-plugin-runic's test/calls.browser.test.mjs): views makes no Performance entries. The Bridge answers over a
// MessageChannel, as a host transport does. The production build reports nothing, also while a reader listens.
const dist = new URL("../dist/", import.meta.url);

const page = `<!doctype html><html><body>
<button id="increment">Increment</button>
<script type="module">
const query = new URLSearchParams(location.search);
// The development build (the \`development\` export condition) unless the page asks for the production build.
const build = query.has("production") ? "/dist/" : "/dist/development/";
const { connectView } = await import(build + "generated.js");
const bridgeWire = await import(build + "wire.js");
const listen = query.has("listen");
const calls = [];
if (listen) globalThis[Symbol.for("runic.views.calls")] = { listeners: new Set([event => calls.push(event)]), requested: true };
const tasks = [];
const createTask = console.createTask;
if (typeof createTask === "function") console.createTask = name => { tasks.push(name); return createTask.call(console, name); };
const measures = [];
new PerformanceObserver(list => { for (const entry of list.getEntries()) measures.push({ name: entry.name, detail: entry.detail }); })
  .observe({ type: "measure" });

// A Bridge whose replies arrive on a MessageChannel; a command's frame and reply arrive together.
const channel = new MessageChannel();
const listeners = new Set();
const pending = [];
let revision = 1;
channel.port2.onmessage = ({ data }) => {
  if (data.frame) for (const listener of [...listeners]) listener(data.frame);
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
        const frame = JSON.stringify({ kind: "frame", route: "counter", baseRevision: revision - 1, revision, changes: [{ field: "count", kind: "set", value: revision - 1 }] });
        setTimeout(() => channel.port1.postMessage({ frame, reply: JSON.stringify({ ok: true, revision }) }), 15);
        return new Promise(resolve => pending.push(resolve));
      }
      default: return reply({ ok: true });
    }
  },
};
const client = await connectView({ contract: "Demo.CounterViewModel:ABC", route: "counter", members: { count: wire => bridgeWire.integer(wire, 0, 100) } });
client.subscribe(state => {
  if (state.count === 0) return;
  globalThis.callsTest.seen.push(state.count);
  if (globalThis.callsTest.pause) debugger;
});
document.getElementById("increment").addEventListener("click", function onIncrementClick() { void client.command("increment"); });
globalThis.callsTest = { calls, tasks, measures, seen: [], pause: false };
</script></body></html>`;

const chromiumPath = process.env["PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH"];

type CallsTest = { calls: { phase: string; outcome?: string }[]; tasks: string[]; measures: { name: string; detail: unknown }[]; seen: number[]; pause: boolean };

async function open(browser: Browser, query: string): Promise<{ tab: Page; errors: string[] }> {
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
  await tab.waitForFunction(() => (globalThis as { callsTest?: unknown }).callsTest !== undefined);
  return { tab, errors };
}

const state = (tab: Page) => tab.evaluate(() => {
  const { calls, tasks, measures, seen } = (globalThis as unknown as { callsTest: CallsTest }).callsTest;
  return { calls, tasks, measures, seen };
});

test("Chromium: with a reader, a click's command is reported and the reply's subscriber has the click's async stack",
  { timeout: 60_000, skip: !chromiumPath }, async () => {
    let browser: Browser | undefined;
    try {
      browser = await chromium.launch({ executablePath: chromiumPath!, headless: true });
      const { tab, errors } = await open(browser, "?listen");

      await tab.click("#increment");
      await tab.waitForFunction(() => (globalThis as unknown as { callsTest: CallsTest }).callsTest.calls.length === 2);
      const recorded = await state(tab);
      assert.deepEqual(recorded.seen, [1]);
      assert.deepEqual(recorded.calls.map(call => [call.phase, call.outcome]), [["start", undefined], ["end", "ok"]]);
      assert.deepEqual(recorded.tasks, ["Runic command CounterViewModel.increment"]);
      assert.deepEqual(recorded.measures, []);

      // Paused in the subscriber that the reply notifies, the async stack leads through the call's task to the click.
      const cdp = await tab.context().newCDPSession(tab);
      await cdp.send("Debugger.enable");
      await cdp.send("Debugger.setAsyncCallStackDepth", { maxDepth: 32 });
      const paused = new Promise<{ asyncStackTrace?: AsyncStack }>(resolve => {
        cdp.once("Debugger.paused", event => { resolve(event as never); void cdp.send("Debugger.resume"); });
      });
      await tab.evaluate(() => { (globalThis as unknown as { callsTest: CallsTest }).callsTest.pause = true; });
      await tab.click("#increment");
      const chain: { description?: string; functions: string[] }[] = [];
      for (let stack = (await paused).asyncStackTrace; stack !== undefined; stack = stack.parent)
        chain.push({ ...(stack.description === undefined ? {} : { description: stack.description }), functions: stack.callFrames.map(frame => frame.functionName) });
      const task = chain.find(segment => segment.description === "Runic command CounterViewModel.increment");
      assert.ok(task, `no Runic task in ${JSON.stringify(chain)}`);
      assert.ok(task.functions.includes("onIncrementClick"), `the task does not lead to the click: ${JSON.stringify(chain)}`);
      // The task segment follows the call's own await chain directly.
      assert.equal(chain.indexOf(task), 1, JSON.stringify(chain));
      await cdp.detach();
      assert.deepEqual(errors, []);
    } finally {
      await browser?.close();
    }
  });

type AsyncStack = { description?: string; callFrames: { functionName: string }[]; parent?: AsyncStack };

test("Chromium: without a reader, a command is not reported and makes no task", { timeout: 60_000, skip: !chromiumPath }, async () => {
  let browser: Browser | undefined;
  try {
    browser = await chromium.launch({ executablePath: chromiumPath!, headless: true });
    const { tab, errors } = await open(browser, "");
    await tab.click("#increment");
    await tab.waitForFunction(() => (globalThis as unknown as { callsTest: CallsTest }).callsTest.seen.length === 1);
    const recorded = await state(tab);
    assert.deepEqual(recorded.calls, []);
    assert.deepEqual(recorded.tasks, []);
    assert.deepEqual(recorded.measures, []);
    assert.deepEqual(errors, []);
  } finally {
    await browser?.close();
  }
});

test("Chromium: the production build reports nothing and makes no task while a reader listens", { timeout: 60_000, skip: !chromiumPath }, async () => {
  let browser: Browser | undefined;
  try {
    browser = await chromium.launch({ executablePath: chromiumPath!, headless: true });
    const { tab, errors } = await open(browser, "?listen&production");
    await tab.click("#increment");
    await tab.waitForFunction(() => (globalThis as unknown as { callsTest: CallsTest }).callsTest.seen.length === 1);
    const recorded = await state(tab);
    assert.deepEqual(recorded.calls, []);
    assert.deepEqual(recorded.tasks, []);
    assert.equal(await tab.evaluate(() => (globalThis as Record<symbol, { sequence?: number }>)[Symbol.for("runic.views.calls")]?.sequence), undefined);
    assert.deepEqual(errors, []);
  } finally {
    await browser?.close();
  }
});
