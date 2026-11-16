// The Runic DevTools extension in Chromium (#1044): the unpacked extension from tools/dotnet-runic/devtools-extension
// loads into a persistent context, and against a page under `vite dev` with runic() (a plain-script page with a mock
// Bridge, as the template's pages have one generated client per View) the extension's eval protocol (page.js) returns
// the page's Views and recent Bridge calls from Runic Inspect's `snapshot()`, and the selected element's command from
// `describe`. DevTools itself cannot be automated here, so the panel page opens as a tab with a stand-in for
// `chrome.devtools.inspectedWindow.eval` that evaluates in the application's page, and the check of its UI stays light.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, writeFile, rm, symlink, realpath } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { chromium } from "playwright-core";
import { createServer } from "vite";
import { runic } from "../dist/index.js";
import { scaled } from "../../../../tests/support/timeouts.mjs";

const here = fileURLToPath(new URL("..", import.meta.url));
const packages = fileURLToPath(new URL("../../", import.meta.url));
const extensionDirectory = fileURLToPath(new URL("../../../../tools/dotnet-runic/devtools-extension", import.meta.url));
const { createEvaluator } = await import(new URL("../../../../tools/dotnet-runic/devtools-extension/page.js", import.meta.url).href);
const skip = !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;

const bridge = `
import { connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";

const channel = new MessageChannel();
const listeners = new Set();
const pending = [];
let revision = 1;
const state = { count: 10, step: 1, canIncrement: true };
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
        state.count += state.step;
        return change([{ field: "count", kind: "set", value: state.count }]);
      default: return reply({ ok: true });
    }
  },
};
const same = wire => wire;
export const client = await connectView({ contract: "Demo.CounterViewModel:ABC", route: "counter",
  members: { count: wire => bridgeWire.integer(wire, 0, 1000), step: wire => bridgeWire.integer(wire, 0, 100), canIncrement: same } });
`;

const main = `
import { client } from "./bridge.js";
const count = document.querySelector(".count");
const render = () => { count.textContent = String(client.snapshot.count); };
client.subscribe(render);
render();
document.querySelector("#increment").addEventListener("click", () => void client.command("increment"));
globalThis.extensionTest = { ready: true };
`;

function sourceMap(root) {
  const file = join(root, "CounterViewModel.cs");
  return {
    file,
    map: {
      format: "runic-bridge-sources/1",
      viewModels: [{ id: "Demo.CounterViewModel", name: "Counter", route: "counter", contract: "Demo.CounterViewModel:ABC",
        pageKinds: ["counter"], symbol: "Demo.CounterViewModel", location: { file, line: 3, column: 14 } }],
      members: [
        { id: "Demo.CounterViewModel/state/count", viewModel: "Demo.CounterViewModel", member: "count", kind: "state",
          symbol: "Demo.CounterViewModel.Count", location: { file, line: 5, column: 5 } },
        { id: "Demo.CounterViewModel/command/increment", viewModel: "Demo.CounterViewModel", member: "increment", kind: "command",
          symbol: "Demo.CounterViewModel.Increment()", property: "Demo.CounterViewModel.IncrementCommand", location: { file, line: 11, column: 5 } },
      ],
    },
  };
}

async function link(root, name, target) {
  const path = join(root, "node_modules", ...name.split("/"));
  await mkdir(join(path, ".."), { recursive: true });
  await symlink(await realpath(target), path, "dir");
}

/** Chromium's id of an unpacked extension: the SHA-256 of its absolute path, the first 32 hex digits as a–p. */
function unpackedExtensionId(path) {
  return [...createHash("sha256").update(path).digest("hex").slice(0, 32)]
    .map((digit) => String.fromCharCode(97 + Number.parseInt(digit, 16))).join("");
}

test("the DevTools extension loads unpacked and reads the page's Views and Bridge calls", { skip }, async () => {
  const root = await realpath(await mkdtemp(join(tmpdir(), "runic-devtools-extension-")));
  const extension = await realpath(extensionDirectory);
  let server, context;
  try {
    const { file, map } = sourceMap(root);
    await writeFile(file, "namespace Demo;\n\npublic partial class CounterViewModel\n{\n}\n");
    await writeFile(join(root, "sources.json"), JSON.stringify(map));
    await writeFile(join(root, "bridge.js"), bridge);
    await writeFile(join(root, "main.js"), main);
    await writeFile(join(root, "index.html"),
      '<!doctype html><html><body><p class="count"></p><button id="increment">Increment</button><script type="module" src="/main.js"></script></body></html>');
    await link(root, "@runic-artifex/vite-plugin-runic", here);
    await link(root, "@runic-artifex/views", join(packages, "views"));
    server = await createServer({
      root, configFile: false, logLevel: "silent",
      plugins: [runic({ sourceMap: "sources.json" })],
      server: { host: "127.0.0.1", port: 0, fs: { strict: false } },
    });
    await server.listen();

    // Playwright disables extensions by default; Chromium (not branded Chrome) honours --load-extension.
    context = await chromium.launchPersistentContext(join(root, "profile"), {
      executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH,
      headless: true,
      ignoreDefaultArgs: ["--disable-extensions"],
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`],
    });
    const page = context.pages()[0] ?? await context.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(String(error)));
    await page.goto(`http://127.0.0.1:${server.httpServer.address().port}`);
    await page.waitForFunction(() => globalThis.extensionTest?.ready === true, null, { timeout: scaled(30_000) });
    await page.click("#increment");
    await page.waitForFunction(() => document.querySelector(".count")?.textContent === "11");

    // The data path: what inspectedWindow.eval of the extension's expressions returns in the page.
    const evaluator = createEvaluator((expression) => page.evaluate(expression));
    let snapshot;
    const settled = Date.now() + scaled(10_000);
    do {
      const result = await evaluator.call("snapshot");
      assert.equal(result.status, "ok", JSON.stringify(result));
      snapshot = result.value;
    } while (snapshot.calls[0]?.outcome !== "ok" && Date.now() < settled);
    assert.equal(snapshot.version, 1);
    const view = snapshot.views.find((candidate) => candidate.route === "counter");
    assert.ok(view, JSON.stringify(snapshot.views));
    assert.equal(view.state.count, 11);
    assert.equal(view.contract, "Demo.CounterViewModel:ABC");
    assert.deepEqual(view.viewModel, { id: "Demo.CounterViewModel", name: "Counter", symbol: "Demo.CounterViewModel",
      location: { file, line: 3, column: 14, editor: "runic", id: "Demo.CounterViewModel" } });
    const call = snapshot.calls.find((candidate) => candidate.member === "increment");
    assert.ok(call, JSON.stringify(snapshot.calls));
    assert.equal(call.kind, "command");
    assert.equal(call.outcome, "ok");
    assert.equal(typeof call.duration, "number");
    assert.ok(call.revision >= 2);
    assert.equal(call.symbol, "Demo.CounterViewModel.Increment()");
    assert.deepEqual(call.location, { file, line: 11, column: 5, editor: "runic", id: "Demo.CounterViewModel/command/increment" });

    // The Elements pane's data: in DevTools the argument is `$0`.
    const described = await evaluator.call("describe", ['document.querySelector("#increment")']);
    assert.equal(described.status, "ok", JSON.stringify(described));
    assert.deepEqual(described.value.commands.map(({ member, symbol, evidence, calls }) => ({ member, symbol, evidence, calls })),
      [{ member: "increment", symbol: "Demo.CounterViewModel.Increment()", evidence: "call", calls: 1 }]);

    // The extension loaded: its panel page opens, and with a stand-in for DevTools' eval it draws the same data.
    const panel = await context.newPage();
    await panel.exposeFunction("runicTestEvaluate", (expression) => page.evaluate(expression));
    await panel.addInitScript(() => {
      globalThis.chrome ??= {};
      globalThis.chrome.devtools = {
        inspectedWindow: {
          eval(expression, callback) {
            globalThis.runicTestEvaluate(expression).then((value) => callback(value), (error) => callback(undefined, { isException: true, value: String(error) }));
          },
        },
        panels: { themeName: "default" },
        network: { onNavigated: { addListener() {} } },
      };
    });
    const response = await panel.goto(`chrome-extension://${unpackedExtensionId(extension)}/panel.html`);
    assert.ok(response === null || response.ok(), "The extension's panel page did not load.");
    await panel.locator("tr[data-call] code", { hasText: "CounterViewModel.increment" }).first().waitFor({ timeout: scaled(10_000) });
    assert.match(await panel.locator("main").textContent(), /Demo\.CounterViewModel\.Increment\(\)/u);
    assert.match(await panel.locator("details summary").first().textContent(), /counter.*Counter/u);

    // The Elements pane, with `$0` standing for the selected button.
    const sidebar = await context.newPage();
    await sidebar.exposeFunction("runicTestEvaluate", (expression) => page.evaluate(expression.replaceAll("$0", 'document.querySelector("#increment")')));
    await sidebar.addInitScript(() => {
      globalThis.chrome ??= {};
      globalThis.chrome.devtools = {
        inspectedWindow: {
          eval(expression, callback) {
            globalThis.runicTestEvaluate(expression).then((value) => callback(value), (error) => callback(undefined, { isException: true, value: String(error) }));
          },
        },
        panels: { themeName: "dark", elements: { onSelectionChanged: { addListener() {} } } },
      };
    });
    await sidebar.goto(`chrome-extension://${unpackedExtensionId(extension)}/sidebar.html`);
    await sidebar.locator("h2", { hasText: "Commands" }).waitFor({ timeout: scaled(10_000) });
    assert.match(await sidebar.locator("main").textContent(), /button#increment.*Demo\.CounterViewModel\.Increment\(\)/su);
    assert.equal(await sidebar.evaluate(() => document.documentElement.dataset.theme), "dark");
    assert.deepEqual(errors, []);
  } finally {
    await context?.close();
    await server?.close();
    await rm(root, { recursive: true, force: true });
  }
});
