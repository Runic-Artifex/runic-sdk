import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";
import { stopHost } from "./host-process.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, "../../../..");
const configuration = process.env.CONFIGURATION ?? "Debug";
const css = resolve(here, "src/style.css");
const original = await readFile(css, "utf8");
const modified = original + "\n:root { --runic-development-probe: ready; }\n";
const temporary = await mkdtemp(resolve(tmpdir(), "runic-host-dev-"));
const logs = resolve(root, "artifacts/host-dev");
await mkdir(logs, { recursive: true });
let browser;
try {
  browser = await chromium.launch({ headless: true,
    ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH
      ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}) });
  for (const selection of ["cswebui", "desktop"]) {
    const host = spawn("dotnet", [resolve(root,
      `tools/dotnet-runic/bin/${configuration}/net10.0/dotnet-runic.dll`),
      "dev", "--project", resolve(here, ".."), "--configuration", configuration,
      "--host", selection, "--no-dotnet-watch", "--", "--serve"], {
      cwd: root, stdio: ["ignore", "pipe", "pipe"],
      env: { ...process.env, RUNIC_CUSTOMERS_FILE: resolve(temporary, "customers.json"),
        // Preserve config/dependency startup phases in the CI artifact when
        // Vite stalls before it can print its ready message.
        RUNIC_TOOLKIT_VITE_DEBUG: "vite:config,vite:deps",
        XDG_CACHE_HOME: temporary, GSETTINGS_BACKEND: "memory" },
    });
    let output = "";
    let page;
    let failure;
    host.stderr.on("data", chunk => { output += chunk; });
    try {
      const url = await new Promise((accept, reject) => {
        const timer = setTimeout(() => reject(new Error(`Development startup timed out: ${output}`)), 120000);
        host.once("error", error => { clearTimeout(timer); reject(error); });
        host.once("exit", (code, signal) => {
          clearTimeout(timer); reject(new Error(`Development host exited ${code}/${signal}: ${output}`));
        });
        host.stdout.on("data", chunk => {
          output += chunk;
          const match = output.match(/Customer editor: (https?:\/\/\S+)/);
          if (match) { clearTimeout(timer); accept(match[1]); }
        });
      });
      page = await browser.newPage();
      await page.goto(url);
      await page.waitForFunction(() => document.querySelector("#name")?.value === "Alex Morgan",
        undefined, { timeout: 20000 });
      assert.ok(await page.locator('script[src*="/@vite/client"]').count(), "Host is serving the Vite development document");
      await page.getByLabel("Full name", { exact: true }).fill("Draft kept through HMR");
      await writeFile(css, modified);
      await page.waitForFunction(() => getComputedStyle(document.documentElement)
        .getPropertyValue("--runic-development-probe").trim() === "ready");
      assert.equal(await page.getByLabel("Full name", { exact: true }).inputValue(), "Draft kept through HMR");
      console.log(`ok - ${selection}: CLI startup, live bridge and frontend HMR preserve the draft`);
    } catch (error) {
      console.error(output);
      failure = error;
    } finally {
      const failures = failure ? [failure] : [];
      for (const cleanup of [
        async () => { if (await readFile(css, "utf8") === modified) await writeFile(css, original); },
        async () => { await page?.close(); },
        async () => { await stopHost(host); },
        async () => { await writeFile(resolve(logs, `${selection}.log`), output); },
      ]) {
        try { await cleanup(); } catch (error) { failures.push(error); }
      }
      if (failures.length === 1) throw failures[0];
      if (failures.length > 1) throw new AggregateError(failures, `${selection} development acceptance and cleanup failed`);
    }
  }
} finally {
  await browser?.close();
  await rm(temporary, { recursive: true, force: true });
}
