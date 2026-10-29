import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile, rm, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { chromium } from "playwright-core";
import { createServer } from "vite";
import { DevTools } from "@vitejs/devtools";
import { runic, runicDevSessionProxy } from "../dist/index.js";

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
    plugin.api.reportDiagnostic({ source: "assets", kind: "event", label: "Live Bun diagnostic" });
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

    // The page's connection, as @runic-artifex/views publishes it, reaches the dock (D40 a).
    await page.waitForFunction(() => (globalThis[Symbol.for("runic.views.connection")]?.listeners.size ?? 0) > 0, null, { timeout: 10_000 });
    await page.evaluate(() => {
      const channel = globalThis[Symbol.for("runic.views.connection")];
      channel.state = "reconnecting";
      for (const listener of channel.listeners) listener("reconnecting");
    });
    await page.getByText("reconnecting", { exact: true }).waitFor({ timeout: 10_000 });
    assert.deepEqual(errors, []);
  } finally {
    await browser?.close();
    await server?.close();
    if (previousAuth === undefined) delete process.env.VITE_DEVTOOLS_DISABLE_CLIENT_AUTH;
    else process.env.VITE_DEVTOOLS_DISABLE_CLIENT_AUTH = previousAuth;
    await rm(root, { recursive: true, force: true });
  }
});

// The two setups without runic()'s HTML injection: the Angular CLI (a proxy configuration, no Vite plugins) and
// SvelteKit (runic({ html: false })). The page imports the client package itself.
for (const setup of ["proxy", "html-false"]) {
  test(`the client reports a page's failures to /__runic/state without HTML injection (${setup})`, {
    timeout: 30_000,
    skip: !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH,
  }, async () => {
    const root = await mkdtemp(join(tmpdir(), "runic-client-browser-"));
    const printed = [];
    const warn = console.warn;
    let server, browser;
    try {
      await writeFile(join(root, "index.html"),
        '<!doctype html><html><body><input id="title"><script type="module" src="/main.js"></script></body></html>');
      await writeFile(join(root, "main.js"),
        'import "@runic-artifex/vite-plugin-runic/client";\nglobalThis.__mainLoaded = true;\n');
      await mkdir(join(root, "node_modules", "@runic-artifex"), { recursive: true });
      await symlink(process.cwd(), join(root, "node_modules", "@runic-artifex", "vite-plugin-runic"), "dir");
      console.warn = (...values) => printed.push(values.join(" "));
      server = await createServer({ root, configFile: false, logLevel: "silent",
        plugins: setup === "proxy" ? [] : [runic({ html: false })],
        // Pre-bundled as the Angular CLI and runic() do, so a cold cache does not reload the page.
        optimizeDeps: { include: ["@runic-artifex/vite-plugin-runic/client"] },
        server: { host: "127.0.0.1", port: 0, ...(setup === "proxy" ? { proxy: runicDevSessionProxy() } : {}) } });
      await server.listen();
      const base = `http://127.0.0.1:${server.httpServer.address().port}`;
      browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, headless: true });
      const page = await browser.newPage();
      const errors = [];
      page.on("pageerror", error => errors.push(String(error)));
      await page.goto(base);
      await page.waitForFunction(() => globalThis.__mainLoaded === true, null, { timeout: 10_000 });
      await page.waitForFunction(() => globalThis[Symbol.for("runic.vite.staleInput")] === true, null, { timeout: 10_000 });
      await page.evaluate(() => {
        for (const listener of globalThis[Symbol.for("runic.views.diagnostics")]) listener({
          kind: "error", code: "failed", message: "Save failed.", route: "editorSave",
          detail: { type: "System.InvalidOperationException", message: "Disk full.",
            stack: "System.InvalidOperationException: Disk full.\n   at Notes.Editor.Save() in /src/Notes/Editor.cs:line 12" },
        });
      });
      let state;
      for (let attempt = 0; attempt < 50; attempt += 1) {
        state = await fetch(`${base}/__runic/state`).then((response) => response.json());
        if (state.timeline.length > 0) break;
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
      assert.deepEqual(state.timeline.map((entry) => entry.label), ["editorSave failed"]);
      assert.equal("failure" in state.timeline[0], false);
      if (setup === "proxy") {
        assert.equal(printed.length, 1);
        assert.equal(printed[0], "[runic] views editorSave failed\n  System.InvalidOperationException: Disk full.\n" +
          "    at Notes.Editor.Save() in /src/Notes/Editor.cs:line 12");
      }
      assert.deepEqual(errors, []);
    } finally {
      console.warn = warn;
      await browser?.close();
      await server?.close();
      await rm(root, { recursive: true, force: true });
    }
  });
}
