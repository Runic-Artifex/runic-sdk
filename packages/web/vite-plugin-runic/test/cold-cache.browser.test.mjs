import assert from "node:assert/strict";
import { cp, mkdir, mkdtemp, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { chromium } from "playwright-core";
import { createServer } from "vite";
import { runic } from "../dist/index.js";

// The development client is injected into HTML as a virtual module that Vite's
// dependency scanner cannot see. With a cold optimizer cache, the first page
// load discovered @runic-artifex/vite-plugin-runic/client, optimized it, and
// with a larger dependency graph, such as Svelte's, reloaded the page. The
// reload closed the host's Bridge connection under a script in flight. The page
// must keep its first document without a retry, and nothing may be discovered
// after start-up.
test("serves the development client with a cold optimizer cache without a reload or late optimization", {
  timeout: 60_000,
  skip: !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH,
}, async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-vite-cold-cache-"));
  let server, browser;
  try {
    await writeFile(join(root, "index.html"),
      '<!doctype html><html><body><script type="module" src="/main.js"></script></body></html>');
    await writeFile(join(root, "main.js"),
      'import { loaded } from "runic-fixture-dependency";\nglobalThis.__runicMainLoaded = loaded;\n');
    // An application dependency, which the start-up scan finds.
    const dependency = join(root, "node_modules", "runic-fixture-dependency");
    await mkdir(dependency, { recursive: true });
    await writeFile(join(dependency, "package.json"), JSON.stringify(
      { name: "runic-fixture-dependency", version: "1.0.0", type: "module", exports: "./index.js" }));
    await writeFile(join(dependency, "index.js"), "export const loaded = true;\n");
    // Installed, not linked, as in an application: Vite pre-bundles packages
    // under node_modules and serves linked sources as they are.
    const installed = join(root, "node_modules", "@runic-artifex", "vite-plugin-runic");
    await mkdir(installed, { recursive: true });
    await cp(join(process.cwd(), "package.json"), join(installed, "package.json"));
    await cp(join(process.cwd(), "dist"), join(installed, "dist"), { recursive: true });
    const cacheDir = join(root, "node_modules", ".vite");
    await assert.rejects(stat(cacheDir), { code: "ENOENT" });

    const messages = [];
    const record = (message) => { messages.push(String(message)); };
    server = await createServer({
      root,
      cacheDir,
      configFile: false,
      logLevel: "info",
      customLogger: {
        hasWarned: false,
        info: record,
        warn: record,
        warnOnce: record,
        error: record,
        clearScreen() {},
        hasErrorLogged: () => false,
      },
      plugins: [runic()],
      server: { host: "127.0.0.1", port: 0, strictPort: false },
    });
    const reloads = [];
    for (const channel of new Set([server.ws, server.environments?.client?.hot].filter(Boolean))) {
      const send = channel.send.bind(channel);
      channel.send = (payload, ...rest) => {
        if (typeof payload === "object" && payload?.type === "full-reload") reloads.push(payload);
        return send(payload, ...rest);
      };
    }
    await server.listen();
    const port = server.httpServer.address().port;

    browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, headless: true });
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", (error) => errors.push(String(error)));
    let documents = 0;
    page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) documents += 1; });
    await page.goto(`http://127.0.0.1:${port}/`);
    await page.waitForFunction(() => globalThis.__runicMainLoaded === true, null, { timeout: 15_000 });
    // The development client registers the Views diagnostics listener.
    await page.waitForFunction(() => (globalThis[Symbol.for("runic.views.diagnostics")]?.size ?? 0) > 0,
      null, { timeout: 15_000 });
    const marker = await page.evaluate(() => (globalThis.__runicFirstDocument = crypto.randomUUID()));
    // A dependency discovered by the page is optimized after a debounce and
    // logged 200 ms later; wait well past both.
    await new Promise((resolve) => setTimeout(resolve, 3_000));

    assert.equal(await page.evaluate(() => globalThis.__runicFirstDocument), marker,
      "A reload replaced the first document.");
    assert.equal(documents, 1, "The page navigated more than once.");
    assert.deepEqual(reloads, []);
    assert.deepEqual(messages.filter((message) => /dependenc(y|ies) optimized:|optimized dependencies changed/.test(message)),
      [], "The page made Vite optimize a dependency after start-up.");
    assert.deepEqual(errors, []);
  } finally {
    await browser?.close();
    await server?.close();
    await rm(root, { recursive: true, force: true });
  }
});
