// The development tools of @runic-artifex/views cost nothing in production (#975; views README, "Development build"):
// `vite build` of a React page like the runic-app template's (test/fixtures/production: @runic-artifex/react, the
// generated Counter client of examples/first-window and a Translations runtime) bundles the production build of views,
// with no Bridge-call reporting, async stack tagging or Translations text channel, and the page creates no development
// global in Chromium, also while a reader listens. Under `vite dev` the same page, and the React binding with it,
// resolves the development build, which reports the calls and follows the text channel.
import assert from "node:assert/strict";
import { mkdtemp, readdir, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";
import { build, createServer, preview } from "vite";
import { runic } from "../dist/index.js";
import { scaled } from "../../../../tests/support/timeouts.mjs";

const root = fileURLToPath(new URL("./fixtures/production/", import.meta.url));
const views = fileURLToPath(new URL("../../views/", import.meta.url));
// What only the development build of views contains.
const markers = ["runic.views.calls", "createTask", "runic.translations.text", "__RUNIC_DEVELOPMENT__",
  // The View descriptions of views and the React DevTools labels of the React binding (#1041).
  "runic.views.describe", "declared failure", " · rev ",
  // The development tools' Views protocol features and their trace context (#958).
  "trace-context/1", "handler-timing/1", "traceparent"];
const chromiumPath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;

async function files(directory) {
  const entries = await readdir(directory, { withFileTypes: true, recursive: true });
  return entries.filter(entry => entry.isFile()).map(entry => join(entry.parentPath, entry.name));
}

// Opens the page, increments the counter once and reports what the development tools left.
async function run(browser, url) {
  const page = await browser.newPage();
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  try {
    await page.goto(url);
    try {
      await page.locator("button:not([disabled])").click({ timeout: scaled(20_000) });
      await page.locator(".count").filter({ hasText: /^1$/ }).waitFor({ timeout: scaled(10_000) });
    } catch (error) {
      const shown = await page.locator(".error").textContent().catch(() => null);
      throw new Error(`${url}: ${[shown, ...errors].filter(Boolean).join("\n") || "no page error"}`, { cause: error });
    }
    return {
      errors,
      ...await page.evaluate(() => ({
        title: document.querySelector("h1")?.textContent,
        calls: productionTest.calls,
        tasks: productionTest.tasks,
        hello: productionTest.hello,
        commands: productionTest.commands,
        textChannel: Symbol.for("runic.translations.text") in globalThis,
        callChannel: Symbol.for("runic.views.calls") in globalThis,
        views: performance.getEntriesByType("resource").map(entry => entry.name).filter(name => name.includes("/views/dist/")),
      })),
    };
  } finally {
    await page.close();
  }
}

test("vite build bundles the production build of views: no development code, and no development global at run time", {
  timeout: scaled(120_000),
}, async () => {
  const outDir = await mkdtemp(join(tmpdir(), "runic-production-build-"));
  let server, browser;
  try {
    // Vite resolves `development` unless NODE_ENV is `production`, which `vite build` sets when it is unset; the test
    // runner sets `test`.
    const environment = process.env.NODE_ENV;
    process.env.NODE_ENV = "production";
    let output;
    try {
      output = await build({
        root, configFile: false, logLevel: "warn", plugins: [runic()],
        build: { outDir, emptyOutDir: true, target: "es2022" },
      });
    } finally {
      if (environment === undefined) delete process.env.NODE_ENV; else process.env.NODE_ENV = environment;
    }
    // Every module of views in the bundle is of the production build; the React binding resolved the same.
    const modules = [output].flat().flatMap(result => result.output).flatMap(chunk => chunk.type === "chunk" ? chunk.moduleIds : []);
    const viewsModules = modules.filter(id => id.startsWith(views));
    assert.ok(viewsModules.some(id => id === join(views, "dist/connection.js")), `views is not bundled: ${modules.filter(id => id.includes("views")).join(", ")}`);
    assert.deepEqual(viewsModules.filter(id => !id.startsWith(join(views, "dist/")) || id.startsWith(join(views, "dist/development/"))), []);
    assert.ok(modules.some(id => id.includes("/react/dist/")), "the React binding is bundled");
    assert.deepEqual(modules.filter(id => id.includes("/react/dist/development/") || id.endsWith("/react/dist/debug-labels.js")), [],
      "the React binding's production build is bundled");
    // The page's inline script installs the test's reader, so the JavaScript assets are what is checked.
    for (const file of (await files(outDir)).filter(name => name.endsWith(".js"))) {
      const text = await readFile(file, "utf8");
      for (const marker of markers) assert.equal(text.includes(marker), false, `${file} contains ${marker}`);
    }

    if (!chromiumPath) return;
    server = await preview({ root, configFile: false, logLevel: "silent", build: { outDir }, preview: { host: "127.0.0.1", port: 0 } });
    const url = server.resolvedUrls.local[0];
    browser = await chromium.launch({ executablePath: chromiumPath, headless: true });
    const page = await run(browser, url);
    assert.deepEqual(page.errors, []);
    assert.equal(page.title, "Counter");
    assert.deepEqual([page.callChannel, page.textChannel, page.tasks], [false, false, []], "no development global, no task");
    // A reader on the page gets nothing: the production build neither reports nor tags a call.
    const read = await run(browser, `${url}?reader`);
    assert.deepEqual([read.calls, read.tasks, read.textChannel, read.errors], [[], [], false, []]);
    // Its hello lists neither development feature, so a Development host's commands carry no trace context or timing.
    for (const feature of ["trace-context/1", "handler-timing/1"]) assert.equal(read.hello.includes(feature), false, feature);
    assert.deepEqual(read.commands, [["kind", "member", "route", "session"]]);
  } finally {
    await browser?.close();
    await new Promise(resolve => server ? server.httpServer.close(resolve) : resolve());
    await rm(outDir, { recursive: true, force: true });
  }
});

test("vite dev resolves the development build for the page and the React binding: calls are reported and tagged, and the text channel is followed", {
  timeout: scaled(120_000),
  skip: !chromiumPath,
}, async () => {
  let server, browser;
  try {
    server = await createServer({
      root, configFile: false, logLevel: "silent", plugins: [runic({ devtools: false })],
      server: { host: "127.0.0.1", port: 0, fs: { strict: false } },
    });
    await server.listen();
    browser = await chromium.launch({ executablePath: chromiumPath, headless: true });
    const page = await run(browser, `${server.resolvedUrls.local[0]}?reader`);
    assert.deepEqual(page.errors, []);
    assert.deepEqual(page.calls, ["start", "end"]);
    // The development build lists the development tools' features and sends their members for a followed call.
    for (const feature of ["trace-context/1", "handler-timing/1"]) assert.ok(page.hello.includes(feature), feature);
    assert.deepEqual(page.commands, [["kind", "member", "route", "session", "timing", "trace"]]);
    // React's development build tags its own tasks.
    assert.deepEqual(page.tasks.filter(name => name.startsWith("Runic ")), ["Runic command CounterViewModel.increment"]);
    assert.equal(page.textChannel, true, "configureTranslatedText follows the Translations text channel");
    assert.ok(page.views.length > 0, "no module of views was loaded");
    assert.deepEqual(page.views.filter(name => !name.includes("/views/dist/development/")), [], "one build of views");
  } finally {
    await browser?.close();
    await server?.close();
  }
});
