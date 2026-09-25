import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const frontend = process.env.RUNIC_HMR_FRONTEND;
if (frontend !== "angular" && frontend !== "svelte")
  throw new Error("Set RUNIC_HMR_FRONTEND to angular or svelte.");
const angular = frontend === "angular";
const viaRunicDev = process.env.RUNIC_HMR_VIA_RUNIC_DEV === "1";
const viaIde = process.env.RUNIC_HMR_VIA_IDE === "1";
if (viaIde && (!angular || viaRunicDev))
  throw new Error("RUNIC_HMR_VIA_IDE requires Angular without RUNIC_HMR_VIA_RUNIC_DEV.");
const frontendDir = fileURLToPath(new URL(`./${angular ? "Angular" : "Svelte"}/`, import.meta.url));
const shellFile = join(frontendDir, angular ? "src/app/shell.ts" : "src/Shell.svelte");
const hostDll = fileURLToPath(new URL(`./bin/${viaIde ? "Debug" : "Release"}/net10.0/NotesReactiveViews.dll`, import.meta.url));
const project = fileURLToPath(new URL("./NotesReactiveViews.csproj", import.meta.url));
const devTool = fileURLToPath(new URL("../../tools/Runic.Application.Views.Dev/runic-dev.mjs", import.meta.url));

const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function retry(action, timeout = 15_000) {
  const deadline = Date.now() + timeout;
  let lastError;
  while (Date.now() < deadline) {
    try { if (await action()) return; }
    catch (cause) { lastError = cause; }
    await pause(50);
  }
  throw new Error(`Timed out waiting for ${frontend} Reactive Notes HMR: ${lastError ?? "no detail"}`);
}
async function availablePort() {
  const server = createServer();
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
}

const host = viaRunicDev
  ? spawn("node", [devTool, "--project", project, "--frontend", frontendDir, "--framework", frontend],
    { stdio: ["pipe", "pipe", "pipe"] })
  : spawn("dotnet", [hostDll, "--serve-only"], {
    env: viaIde ? { ...process.env, RUNIC_DEV_FRONTEND: frontend } : process.env,
    stdio: ["pipe", "pipe", "pipe"],
  });
let hostOutput = "", hostErrors = "", serverOutput = "";
host.stdout.on("data", chunk => { hostOutput += chunk.toString(); });
host.stderr.on("data", chunk => { hostErrors += chunk.toString(); });
let devServer, chrome, socket, profile, originalShell, probeFile, devPorts;
try {
  let url;
  if (viaRunicDev) {
    await retry(() => {
      if (host.exitCode !== null) throw new Error(`runic dev exited: ${hostErrors}\n${hostOutput}`);
      const ready = hostOutput.match(/RUNIC_DEV_READY\|(http:\/\/127\.0\.0\.1:\d+\/)\|backend=(\d+)\|events=(\d+)/);
      url = ready?.[1];
      if (ready) devPorts = [Number(new URL(url).port), Number(ready[2]), Number(ready[3])];
      return url;
    }, 90_000);
  } else if (viaIde) {
    await retry(() => {
      if (host.exitCode !== null) throw new Error(`IDE host exited: ${hostErrors}\n${hostOutput}`);
      url = hostOutput.match(/RUNIC_IDE_READY\|(http:\/\/127\.0\.0\.1:\d+\/)\|backend=/)?.[1];
      return url;
    }, 90_000);
  } else {
    let origin;
    await retry(() => {
      if (host.exitCode !== null) throw new Error(`Host exited: ${hostErrors}`);
      origin = hostOutput.match(/https?:\/\/[^\s]+/)?.[0];
      return origin;
    });
    const devPort = await availablePort();
    const args = angular
      ? ["run", "start", "--", "--host", "127.0.0.1", "--port", String(devPort)]
      : ["run", "dev", "--", "--port", String(devPort), "--strictPort"];
    devServer = spawn("npm", args, {
      cwd: frontendDir,
      env: { ...process.env, RUNIC_WEBUI_ORIGIN: origin },
      stdio: ["ignore", "pipe", "pipe"],
    });
    devServer.stdout.on("data", chunk => { serverOutput += chunk.toString(); });
    devServer.stderr.on("data", chunk => { serverOutput += chunk.toString(); });
    url = `http://127.0.0.1:${devPort}/`;
    await retry(async () => {
      if (devServer.exitCode !== null) throw new Error(`Dev server exited: ${serverOutput}`);
      try { return (await fetch(url)).ok; } catch { return false; }
    }, 30_000);
  }

  profile = await mkdtemp(join(tmpdir(), `runic-${frontend}-reactive-hmr-`));
  chrome = spawn(process.env.WEBUI_BROWSER_PATH ?? "chromium", [
    "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
    "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
    `--user-data-dir=${profile}`, url,
  ], { stdio: "ignore" });
  let port;
  await retry(async () => {
    port = Number((await readFile(join(profile, "DevToolsActivePort"), "utf8")).split("\n")[0]);
    return port;
  });
  let target;
  await retry(async () => {
    const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    target = targets.find(entry => entry.type === "page" && entry.url.startsWith(url));
    return target;
  });
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  let nextId = 0;
  const pending = new Map();
  socket.addEventListener("message", ({ data }) => {
    const message = JSON.parse(data);
    const resolve = pending.get(message.id);
    if (resolve) { pending.delete(message.id); resolve(message); }
  });
  async function evaluate(expression) {
    const id = ++nextId;
    const result = new Promise(resolve => pending.set(id, resolve));
    socket.send(JSON.stringify({ id, method: "Runtime.evaluate", params: {
      expression, awaitPromise: true, returnByValue: true,
    } }));
    const response = await result;
    if (response.error || response.result?.exceptionDetails) throw new Error(JSON.stringify(response));
    return response.result.result.value;
  }
  const query = expression => evaluate(expression);
  const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
  const snapshot = route => evaluate(`(async () => JSON.parse(await window.__runicBridge.call(${JSON.stringify(route + "Snapshot")})))()`);

  try {
    await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
  } catch (cause) {
    const page = await query('({ html: document.body.innerHTML.slice(0, 1800), webui: typeof window.webui, bridge: typeof window.__runicBridge, connected: window.webui?.isConnected?.() })');
    throw new Error(`Dev page failed: ${JSON.stringify(page)}\n${serverOutput}\n${hostErrors}\n${cause}`);
  }
  await click("[data-go=document]");
  await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
  await retry(async () => await query('document.querySelector("#compact-pane h2")?.textContent') === "Compact View");
  const documentId = (await snapshot("shell")).state.main.id;
  const editorId = (await snapshot(`content${documentId}`)).state.currentPane.id;
  const editorRoute = `content${editorId}`;
  const compactId = (await snapshot(`content${documentId}`)).state.compactNote.id;
  const compactRoute = `content${compactId}`;
  await query('(() => { const field = document.querySelector("#document-pane [data-title]"); field.value = "Draft"; field.dispatchEvent(new Event("change", { bubbles: true })); return true; })()');
  await retry(async () => await query('document.querySelector("#compact-pane [data-title]")?.textContent') === "Draft");
  await click("[data-save]");
  await retry(async () => await query('document.querySelector("#document-pane [data-message]")?.textContent') === "Saved Draft");
  await query('window.__hmrSentinel = "same-document"');

  originalShell = await readFile(shellFile, "utf8");
  const before = angular
    ? '<p id="status" class="status" role="status">'
    : '<p id="status" role="status">';
  if (originalShell.split(before).length !== 2) throw new Error("The HMR edit point changed.");
  await writeFile(shellFile, originalShell.replace(before, before.replace(">", ' data-hmr-probe="updated">')));
  await retry(async () => await query('document.querySelector("#status")?.dataset.hmrProbe') === "updated");
  if (await query("window.__hmrSentinel") !== "same-document")
    throw new Error("HMR reloaded the browser document.");
  await retry(async () => await query('document.querySelector("#document-pane [data-title]")?.value') === "Draft");
  await retry(async () => await query('document.querySelector("#compact-pane [data-title]")?.textContent') === "Draft");
  const afterHmr = (await snapshot(editorRoute)).state;
  if (afterHmr.activationCount - afterHmr.deactivationCount !== 1)
    throw new Error(`HMR left an unbalanced Editor activation: ${JSON.stringify(afterHmr)}`);

  await query('(() => { const field = document.querySelector("#document-pane [data-title]"); field.value = "After HMR"; field.dispatchEvent(new Event("change", { bubbles: true })); return true; })()');
  await retry(async () => await query('document.querySelector("#compact-pane [data-title]")?.textContent') === "After HMR");
  await click("[data-save]");
  await retry(async () => await query('document.querySelector("#document-pane [data-message]")?.textContent') === "Saved After HMR");
  await click("[data-pane=preview]");
  await retry(async () => (await snapshot(editorRoute)).error?.kind === "disconnected");
  await retry(async () => await query(`typeof window.__${editorRoute}Changed`) === "undefined");
  if ((await snapshot(compactRoute)).state.activationCount - (await snapshot(compactRoute)).state.deactivationCount !== 1)
    throw new Error("The compact View lost its activation after HMR and nested navigation.");
  await click("[data-go=home]");
  await retry(async () => (await snapshot(compactRoute)).error?.kind === "disconnected");
  await retry(async () => await query(`typeof window.__${compactRoute}Changed`) === "undefined");
  if (viaRunicDev) {
    await retry(async () => (await query('(async () => (await fetch("/__runic_dev/status")).json())()')).generation === 1);
    await pause(300);
    probeFile = join(dirname(project), `RunicDevSmokeProbe${process.pid}.cs`);
    await writeFile(probeFile, "namespace NotesReactiveViews; internal static class RunicDevSmokeProbe { }\n");
    await retry(() => hostOutput.includes("RUNIC_DEV_RESTARTED|generation=2"), 60_000);
    await retry(async () => await query('window.__hmrSentinel === undefined && document.readyState === "complete" && document.querySelector("#main h1")?.textContent === "Reactive Notes"'), 30_000);
    host.kill("SIGTERM");
    await retry(() => host.exitCode !== null || host.signalCode !== null, 10_000);
    for (const port of devPorts) {
      await retry(async () => {
        try {
          await fetch(`http://127.0.0.1:${port}/`, { signal: AbortSignal.timeout(500) });
          return false;
        } catch { return true; }
      }, 10_000);
    }
    console.log(`${frontend.toUpperCase()}_REACTIVE_RUNIC_DEV_OK|framework-hmr|backend-rebuild|browser-reload`);
  } else {
    console.log(`${frontend.toUpperCase()}_REACTIVE_${viaIde ? "IDE_" : ""}HMR_OK|same-document|shared-state|command|route-cleanup`);
  }
} finally {
  if (originalShell !== undefined) await writeFile(shellFile, originalShell);
  socket?.close();
  chrome?.kill("SIGTERM");
  devServer?.kill("SIGTERM");
  if (viaRunicDev) host.kill("SIGTERM");
  else if (!host.stdin.writableEnded) host.stdin.end("\n");
  await pause(300);
  if (host.exitCode === null) host.kill("SIGTERM");
  if (probeFile) await rm(probeFile, { force: true });
  if (profile) await rm(profile, { recursive: true, force: true });
}
