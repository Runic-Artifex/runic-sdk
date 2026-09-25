import assert from "node:assert/strict";
import { spawn, execFile as execFileCallback } from "node:child_process";
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { chromium } from "playwright-core";
import { createServer } from "vite";
import { viewBridgeHandoffTestPlugin } from "./view-bridge-handoff-test-plugin.mjs";

const execFile = promisify(execFileCallback);
const fixture = fileURLToPath(new URL("../../../../tests/fixtures/application/DevRestartOwnership/", import.meta.url));

test("a managed contract edit reloads the browser once after host acknowledgment while frontend HMR stays local", {
  timeout: 120_000,
  skip: process.platform !== "linux" || !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH,
}, async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-view-bridge-watch-"));
  const app = join(root, "app");
  const frontend = join(root, "frontend");
  const project = join(app, "DevRestartOwnership.csproj");
  const ready = join(app, "obj", "runic", "view-bridge.ready.json");
  const hostReady = join(root, "host-ready.fingerprint");
  const starts = join(root, "host-starts.txt");
  const gate = join(root, "release-host-ready");
  const contract = join(app, "Contract.cs");
  const valueModule = join(frontend, "value.js");
  let watch;
  let server;
  let browser;
  let watchOutput = "";
  const messages = [];
  try {
    await mkdir(app);
    await mkdir(frontend);
    for (const file of ["DevRestartOwnership.csproj", "Program.cs", "Contract.cs"])
      await copyFile(join(fixture, file), join(app, file));
    await writeFile(join(frontend, "index.html"), `<!doctype html><html><body><span id="value"></span><script type="module" src="/main.js"></script></body></html>`);
    await writeFile(join(frontend, "main.js"), `import { revision } from "./value.js";
sessionStorage.setItem("loads", String(Number(sessionStorage.getItem("loads") ?? 0) + 1));
window.__loads = Number(sessionStorage.getItem("loads"));
window.__hmrUpdates = 0;
document.querySelector("#value").textContent = revision;
import.meta.hot?.accept("./value.js", module => {
  window.__hmrUpdates += 1;
  document.querySelector("#value").textContent = module.revision;
});\n`);
    await writeFile(valueModule, `export const revision = "one";\n`);

    await execFile("dotnet", ["build", project, "--configuration", "Debug"], { cwd: app, timeout: 30_000 });
    const initial = await fingerprint(ready);
    watch = spawn("dotnet", [
      "watch", "--project", project, "--configuration", "Debug", "--no-restore",
      "--property:DebugType=portable", "--property:DebugSymbols=true",
      "--property:Optimize=false",
      "--property:RunicApplicationFrontendCompilerDevelopmentHotReload=true",
      "--non-interactive", "run", "--no-launch-profile",
    ], {
      cwd: app,
      detached: true,
      stdio: ["ignore", "pipe", "pipe"],
      env: {
        ...process.env,
        DOTNET_WATCH_RESTART_ON_RUDE_EDIT: "1",
        RUNIC_VIEW_BRIDGE_HOST_READY: hostReady,
        RUNIC_PROBE_STARTS_PATH: starts,
        RUNIC_PROBE_HOST_READY_GATE_PATH: gate,
      },
    });
    watch.stdout.on("data", chunk => { watchOutput += chunk.toString(); });
    watch.stderr.on("data", chunk => { watchOutput += chunk.toString(); });
    await waitFor(async () => (await hostStarts(starts)).length === 1 &&
      (await readOptional(hostReady))?.trim() === initial, "initial host acknowledgment");

    server = await createServer({
      root: frontend,
      configFile: false,
      logLevel: "silent",
      plugins: [viewBridgeHandoffTestPlugin({ readyManifest: ready, hostReady })],
      server: { host: "127.0.0.1", port: 0 },
    });
    const send = server.ws.send.bind(server.ws);
    server.ws.send = message => { messages.push(message); return send(message); };
    await server.listen();
    browser = await chromium.launch({
      executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH,
      headless: true,
    });
    const page = await browser.newPage();
    const pageErrors = [];
    page.on("pageerror", error => pageErrors.push(String(error)));
    await page.goto(`http://127.0.0.1:${server.httpServer.address().port}/`);
    await page.waitForFunction(() => window.__loads === 1 && document.querySelector("#value")?.textContent === "one");

    await writeFile(valueModule, `export const revision = "two";\n`);
    await page.waitForFunction(() => window.__hmrUpdates === 1 && document.querySelector("#value")?.textContent === "two");
    assert.equal(await page.evaluate(() => window.__loads), 1);
    assert.equal((await hostStarts(starts)).length, 1);
    assert.equal(fullReloads(messages), 0);

    await writeFile(contract, (await readFile(contract, "utf8")).replace("ContractBeforeEdit", "ContractAfterEdit"));
    await waitFor(async () => (await fingerprint(ready)) !== initial &&
      (await hostStarts(starts)).length === 2, "managed rebuild and replacement host start");
    await new Promise(resolve => setTimeout(resolve, 250));
    assert.equal(fullReloads(messages), 0, "Vite reloaded before the replacement host acknowledged its compiled fingerprint.");
    assert.equal(await page.evaluate(() => window.__loads), 1);
    assert.equal((await readOptional(hostReady))?.trim(), initial);

    await writeFile(gate, "ready\n");
    const changed = await fingerprint(ready);
    await waitFor(async () => (await readOptional(hostReady))?.trim() === changed &&
      fullReloads(messages) === 1, "one guarded Vite full reload");
    await page.waitForFunction(() => window.__loads === 2 && document.querySelector("#value")?.textContent === "two");
    await new Promise(resolve => setTimeout(resolve, 500));
    assert.equal(fullReloads(messages), 1);
    assert.equal((await hostStarts(starts)).length, 2);
    assert.deepEqual(pageErrors, []);
    assert.match(watchOutput, /Restart is needed to apply the changes/);
    console.log("DEV_VITE_VIEW_BRIDGE_WATCH_OK|frontend-hmr|one-host-restart|waited-for-loaded-fingerprint|one-browser-reload");
  } catch (error) {
    error.message += `\n--- dotnet watch ---\n${watchOutput.slice(-5000)}\n--- Vite messages ---\n${JSON.stringify(messages.map(message => message.type))}`;
    throw error;
  } finally {
    await browser?.close();
    await server?.close();
    if (watch?.pid) {
      const stopped = watch.exitCode === null
        ? new Promise(resolve => watch.once("exit", resolve))
        : Promise.resolve();
      try { process.kill(-watch.pid, "SIGTERM"); } catch { /* Already stopped. */ }
      await Promise.race([
        stopped,
        new Promise(resolve => setTimeout(resolve, 5_000)),
      ]);
    }
    await rm(root, { recursive: true, force: true });
  }
});

async function waitFor(predicate, label) {
  for (let attempt = 0; attempt < 400; attempt += 1) {
    if (await predicate()) return;
    await new Promise(resolve => setTimeout(resolve, 75));
  }
  assert.fail(`Timed out waiting for ${label}.`);
}

async function readOptional(path) {
  try { return await readFile(path, "utf8"); }
  catch (error) { if (error.code === "ENOENT") return undefined; throw error; }
}

async function fingerprint(path) {
  return JSON.parse(await readFile(path, "utf8")).fingerprint;
}

async function hostStarts(path) {
  return (await readOptional(path))?.trim().split("\n") ?? [];
}

function fullReloads(messages) {
  return messages.filter(message => message.type === "full-reload").length;
}
