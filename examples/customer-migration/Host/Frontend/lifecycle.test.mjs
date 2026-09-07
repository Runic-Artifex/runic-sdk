import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";
import { stopHost } from "./host-process.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const temporary = await mkdtemp(resolve(tmpdir(), "runic-host-lifecycle-"));
let browser;
try {
  browser = await chromium.launch({ headless: true,
    ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH
      ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
  // Fresh processes are required: the native WebUI runtime has process lifetime.
  for (const mode of ["unconnected", "connected", "disconnected", "connected", "unconnected"]) {
    const host = spawn(process.env.RUNIC_CUSTOMER_HOST_EXECUTABLE ?? "dotnet", process.env.RUNIC_CUSTOMER_HOST_EXECUTABLE ? ["--serve"] : [resolve(here,
      `../bin/${process.env.CONFIGURATION ?? "Debug"}/net10.0/CustomerDesktop.dll`), "--serve"], {
      cwd: here, stdio: ["pipe", "pipe", "pipe"],
      env: { ...process.env, RUNIC_CUSTOMERS_FILE: resolve(temporary, "customers.json"),
        XDG_CACHE_HOME: temporary, GSETTINGS_BACKEND: "memory" },
    });
    let page;
    let output = "";
    host.stderr.on("data", chunk => { output += chunk; });
    try {
      const url = await new Promise((accept, reject) => {
        const timer = setTimeout(() => reject(new Error(`Startup timed out: ${output}`)), 20000);
        host.once("error", reject);
        host.once("exit", (code, signal) => {
          clearTimeout(timer); reject(new Error(`Host exited ${code}/${signal}: ${output}`));
        });
        host.stdout.on("data", chunk => {
          output += chunk;
          const match = output.match(/Customer editor: (https?:\/\/\S+)/);
          if (match) { clearTimeout(timer); accept(match[1]); }
        });
      });
      assert.equal(new URL(url).hostname, "127.0.0.1", "Host URL must match its IPv4 loopback listener");
      if (mode !== "unconnected") {
        page = await browser.newPage();
        await page.goto(url);
        await page.waitForFunction(() => document.querySelector("#name")?.value === "Alex Morgan",
          undefined, { timeout: 10000 });
        if (mode === "disconnected") { await page.close(); page = null; }
      } else {
        // Startup must already have made the URL usable; do not retry here.
        const response = await fetch(url, { signal: AbortSignal.timeout(10000) });
        assert.equal(response.status, 200);
        await response.arrayBuffer();
      }
      await stopHost(host);
      console.log(`ok - ${mode} host shuts down cleanly`);
    } catch (error) {
      console.error(`Lifecycle failure (${mode}, pid ${host.pid}):\n${output}`);
      throw error;
    } finally {
      try { await stopHost(host); }
      finally { await page?.close(); }
    }
  }
} finally {
  await browser?.close();
  await rm(temporary, { recursive: true, force: true });
}
