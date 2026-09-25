import assert from "node:assert/strict";
import { spawn, execFile as execFileCallback } from "node:child_process";
import { cp, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { chromium } from "playwright-core";

const execFile = promisify(execFileCallback);
const repository = resolve(fileURLToPath(new URL("../../../../", import.meta.url)));
const fixture = join(repository, "tests", "fixtures", "application", "DevRestartOwnership");
const tool = join(repository, "tools", "dotnet-runic", "Runic.Application.Tool.csproj");
const vite = join(repository, "packages", "web", "vite-plugin-runic", "node_modules", "vite", "bin", "vite.js");
const plugin = join(repository, "packages", "web", "vite-plugin-runic", "dist", "index.js");

test("dotnet runic dev owns the opt-in View Bridge handoff while Vite keeps frontend HMR local", {
  timeout: 150_000,
  skip: process.platform !== "linux" || !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH,
}, async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-dotnet-dev-view-bridge-"));
  const app = join(root, "app");
  const project = join(app, "DevRestartOwnership.csproj");
  const ready = join(app, "obj", "runic", "view-bridge.ready.json");
  const hostReady = join(app, "obj", "runic", "view-bridge-host.fingerprint");
  const starts = join(root, "host-starts.txt");
  const gate = join(root, "release-host-ready");
  const originPath = join(root, "vite-origin.txt");
  const contract = join(app, "Contract.cs");
  const valueModule = join(app, "Frontend", "src", "value.js");
  let dev;
  let browser;
  let output = "";
  try {
    await cp(fixture, app, { recursive: true });
    await execFile("dotnet", ["restore", project], { cwd: app, timeout: 45_000 });
    assert.equal(await exists(vite), true, "The workspace Vite binary must be installed before this browser probe runs.");
    assert.equal(await exists(plugin), true, "The built Runic Vite plugin must be available before this browser probe runs.");

    dev = spawn("dotnet", [
      "run", "--project", tool, "--", "dev", "--project", project, "--no-restore",
    ], {
      cwd: app,
      detached: true,
      stdio: ["ignore", "pipe", "pipe"],
      env: {
        ...process.env,
        DOTNET_WATCH_RESTART_ON_RUDE_EDIT: "1",
        RUNIC_PROBE_STARTS_PATH: starts,
        RUNIC_PROBE_HOST_READY_GATE_PATH: gate,
        RUNIC_PROBE_VITE_BIN: vite,
        RUNIC_PROBE_VITE_PLUGIN: plugin,
        RUNIC_PROBE_VITE_ORIGIN_PATH: originPath,
      },
    });
    dev.stdout.on("data", chunk => { output += chunk.toString(); });
    dev.stderr.on("data", chunk => { output += chunk.toString(); });

    await waitFor(async () => {
      const expected = await fingerprint(ready);
      return expected !== undefined && (await readOptional(hostReady))?.trim() === expected &&
        (await hostStarts(starts)).length === 1 && await readOptional(originPath) !== undefined;
    }, "dotnet runic dev to start Vite and the first matching host");
    const initial = await fingerprint(ready);
    const origin = (await readFile(originPath, "utf8")).trim();

    browser = await chromium.launch({
      executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH,
      headless: true,
    });
    const page = await browser.newPage();
    const pageErrors = [];
    page.on("pageerror", error => pageErrors.push(String(error)));
    await page.goto(origin);
    await page.waitForFunction(() => window.__loads === 1 && document.querySelector("#value")?.textContent === "one");

    await writeFile(valueModule, "export const revision = \"two\";\n");
    await page.waitForFunction(() => window.__hmrUpdates === 1 && document.querySelector("#value")?.textContent === "two");
    assert.equal(await page.evaluate(() => window.__loads), 1);
    assert.equal((await hostStarts(starts)).length, 1);

    await writeFile(contract, (await readFile(contract, "utf8")).replace("ContractBeforeEdit", "ContractAfterEdit"));
    await waitFor(async () => (await fingerprint(ready)) !== initial && (await hostStarts(starts)).length === 2,
      "dotnet watch to publish a changed manifest and start one replacement host");
    assert.equal((await readOptional(hostReady))?.trim(), initial,
      "Vite must wait until the replacement writes the fingerprint it loaded.");
    assert.equal(await page.evaluate(() => window.__loads), 1);

    await writeFile(gate, "ready\n");
    const changed = await fingerprint(ready);
    await waitFor(async () => (await readOptional(hostReady))?.trim() === changed,
      "the replacement host to acknowledge its compiled fingerprint");
    await page.waitForFunction(() => window.__loads === 2 && document.querySelector("#value")?.textContent === "two");
    await new Promise(resolve => setTimeout(resolve, 500));
    assert.equal((await hostStarts(starts)).length, 2);
    assert.deepEqual(pageErrors, []);
    assert.match(output, /Vite development server ready/);
    assert.match(output, /File changed:/,
      "dotnet watch must observe the contract edit before replacing the host.");
    console.log("DOTNET_RUNIC_VIEW_BRIDGE_DEV_OK|frontend-hmr|one-watch-restart|host-fingerprint-ack|one-guarded-browser-reload");
  } catch (error) {
    error.message += `\n--- dotnet runic dev ---\n${output.slice(-6000)}`;
    throw error;
  } finally {
    await browser?.close();
    if (dev?.pid) {
      const stopped = dev.exitCode === null
        ? new Promise(resolve => dev.once("exit", resolve))
        : Promise.resolve();
      try { process.kill(-dev.pid, "SIGTERM"); } catch { /* Already stopped. */ }
      await Promise.race([stopped, new Promise(resolve => setTimeout(resolve, 5_000))]);
    }
    await rm(root, { recursive: true, force: true });
  }
});

async function exists(path) {
  return readFile(path).then(() => true, () => false);
}

async function readOptional(path) {
  try { return await readFile(path, "utf8"); }
  catch (error) { if (error.code === "ENOENT") return undefined; throw error; }
}

async function fingerprint(path) {
  const text = await readOptional(path);
  return text === undefined ? undefined : JSON.parse(text).fingerprint;
}

async function hostStarts(path) {
  const text = await readOptional(path);
  return text?.trim() ? text.trim().split("\n") : [];
}

async function waitFor(predicate, label) {
  for (let attempt = 0; attempt < 500; attempt += 1) {
    if (await predicate()) return;
    await new Promise(resolve => setTimeout(resolve, 75));
  }
  assert.fail(`Timed out waiting for ${label}.`);
}
