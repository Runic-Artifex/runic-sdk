import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile, rm, symlink } from "node:fs/promises";
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
    // An application installs this package; the injected client imports it.
    await mkdir(join(root, "node_modules", "@runic-artifex"), { recursive: true });
    await symlink(process.cwd(), join(root, "node_modules", "@runic-artifex", "vite-plugin-runic"), "dir");
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

    // The injected development client forwards a Views failure from the page.
    await page.waitForFunction(() => (globalThis[Symbol.for("runic.views.diagnostics")]?.size ?? 0) > 0, null, { timeout: 10_000 });
    await page.evaluate(() => {
      for (const listener of globalThis[Symbol.for("runic.views.diagnostics")]) listener({
        kind: "error", code: "failed", message: "Save failed.", route: "editorSave",
        detail: { type: "System.InvalidOperationException", message: "Disk full.",
          stack: "System.InvalidOperationException: Disk full.\n   at Notes.Editor.Save() in /src/Notes/Editor.cs:line 12" },
      });
    });
    await page.getByText("System.InvalidOperationException", { exact: true }).waitFor({ timeout: 10_000 });
    await page.getByText(/at Notes\.Editor\.Save\(\) in \/src\/Notes\/Editor\.cs:line 12/).first().waitFor({ timeout: 10_000 });
    assert.deepEqual(errors, []);
  } finally {
    await browser?.close();
    await server?.close();
    if (previousAuth === undefined) delete process.env.VITE_DEVTOOLS_DISABLE_CLIENT_AUTH;
    else process.env.VITE_DEVTOOLS_DISABLE_CLIENT_AUTH = previousAuth;
    await rm(root, { recursive: true, force: true });
  }
});
