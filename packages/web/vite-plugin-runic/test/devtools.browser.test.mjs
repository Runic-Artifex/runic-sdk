import assert from "node:assert/strict";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { chromium } from "playwright-core";
import { createServer } from "vite";
import { DevTools } from "@vitejs/devtools";
import { runic } from "../dist/index.js";

test("Bun renders the official dock and streams live diagnostics to Chromium", {
  timeout: 30_000,
  skip: !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH,
}, async () => {
  assert.ok(process.versions.bun, "The default development runtime must be exercised.");
  const root = await mkdtemp(join(tmpdir(), "runic-dock-browser-"));
  const previousAuth = process.env.VITE_DEVTOOLS_DISABLE_CLIENT_AUTH;
  // Only this loopback test server bypasses the upstream interactive OTP prompt.
  process.env.VITE_DEVTOOLS_DISABLE_CLIENT_AUTH = "true";
  let server, browser, context;
  try {
    await writeFile(join(root, "index.html"), "<!doctype html><html><body>Dock fixture</body></html>");
    const plugin = runic({ devtools: true, contract: { identity: "bun-browser-canary" } });
    const setup = plugin.devtools.setup;
    plugin.devtools.setup = async value => {
      context = value;
      await setup(value);
    };
    server = await createServer({ root, configFile: false, logLevel: "silent",
      plugins: [DevTools({ embeddedVisibility: "normal" }), plugin],
      server: { host: "127.0.0.1", port: 0 } });
    await server.listen();
    browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, headless: true });
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(String(error)));
    await page.goto(`http://127.0.0.1:${server.httpServer.address().port}`);
    await page.locator('button[aria-label="Runic"]').waitFor({ state: "attached", timeout: 10_000 });
    context.docks.activate("runic:overview");
    await page.getByText("bun-browser-canary", { exact: true }).waitFor({ timeout: 10_000 });
    plugin.diagnostics.report({ source: "assets", kind: "event", label: "Live Bun diagnostic" });
    await page.getByText("Live Bun diagnostic", { exact: true }).waitFor({ timeout: 10_000 });
    assert.deepEqual(errors, []);
  } finally {
    await browser?.close();
    await server?.close();
    if (previousAuth === undefined) delete process.env.VITE_DEVTOOLS_DISABLE_CLIENT_AUTH;
    else process.env.VITE_DEVTOOLS_DISABLE_CLIENT_AUTH = previousAuth;
    await rm(root, { recursive: true, force: true });
  }
});
