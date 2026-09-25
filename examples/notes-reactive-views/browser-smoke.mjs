import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function retry(action, timeoutMilliseconds = 12_000) {
  const deadline = Date.now() + timeoutMilliseconds;
  let lastError;
  while (true) {
    try { if (await action()) return; } catch (error) { lastError = error; }
    if (Date.now() >= deadline) throw new Error(`Timed out waiting for the Reactive Notes journey: ${lastError ?? "no detail"}`);
    await pause(50);
  }
}

const dll = fileURLToPath(new URL("./bin/Release/net10.0/NotesReactiveViews.dll", import.meta.url));
const webRoot = process.env.RUNIC_WEB_ROOT;
const verifyClientDisconnect = process.env.RUNIC_VERIFY_CLIENT_DISCONNECT === "1";
const verifyPendingMount = process.env.RUNIC_VERIFY_PENDING_MOUNT === "1";
const host = spawn("dotnet", [dll, "--serve-only", ...(webRoot ? ["--web-root", webRoot] : []),
  ...(verifyClientDisconnect ? ["--verify-client-disconnect"] : [])], { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "";
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { errors += chunk.toString(); });
let chrome, socket, profile;
try {
  let url;
  await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    url = output.match(/https?:\/\/[^\s]+/)?.[0];
    return url;
  });
  profile = await mkdtemp(join(tmpdir(), "runic-notes-reactive-"));
  chrome = spawn(process.env.WEBUI_BROWSER_PATH ?? "chromium", [
    "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
    "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
    `--user-data-dir=${profile}`, url
  ], { stdio: "ignore" });
  let port;
  await retry(async () => {
    const active = await readFile(join(profile, "DevToolsActivePort"), "utf8");
    port = Number(active.split("\n")[0]);
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
      expression, awaitPromise: true, returnByValue: true
    } }));
    const response = await result;
    if (response.error || response.result?.exceptionDetails) throw new Error(JSON.stringify(response));
    return response.result.result.value;
  }
  const query = expression => evaluate(expression);
  const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
  const change = (selector, value) => evaluate(`(() => { const field = document.querySelector(${JSON.stringify(selector)}); field.value = ${JSON.stringify(value)}; field.dispatchEvent(new Event("change", { bubbles: true })); return true; })()`);
  const snapshot = route => evaluate(`(async () => JSON.parse(await window.__runicBridge.call(${JSON.stringify(route + "Snapshot")})))()`);

  await retry(async () => {
    if (await query('document.querySelector("#main h1")?.textContent') === "Reactive Notes") return true;
    const status = await query('document.querySelector("#status")?.textContent');
    if (status && status !== "Connecting…") {
      const raw = await query('(async () => await window.__runicBridge.call("shellSnapshot"))()');
      throw new Error(`Browser status: ${status}; raw: ${raw}; host: ${errors}`);
    }
    return false;
  });
  if (verifyClientDisconnect) {
    await click("[data-go=document]");
    await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
    await retry(async () => await query('document.querySelector("#compact-pane h2")?.textContent') === "Compact View");
    const documentId = (await snapshot("shell")).state.main.id;
    const editorId = (await snapshot(`content${documentId}`)).state.currentPane.id;
    await retry(async () => (await snapshot(`content${editorId}`)).state?.activationCount === 1);
    chrome.kill("SIGKILL");
    await retry(() => output.includes("CLIENT_DISCONNECT_OBSERVED"));
    host.stdin.end("\n");
    try { await retry(() => host.exitCode !== null || host.signalCode !== null); }
    catch (cause) {
      throw new Error(`The Notes host did not exit after client disconnect. exit: ${host.exitCode}; signal: ${host.signalCode}; stdout: ${output}; stderr: ${errors}; ${cause}`);
    }
    if (host.exitCode !== 0 || !output.includes("CLIENT_DISCONNECT_OK"))
      throw new Error(`Client disconnect verification failed: exit: ${host.exitCode}; signal: ${host.signalCode}; ${output}\n${errors}`);
    console.log("REACTIVE_NOTES_CLIENT_DISCONNECT_OK");
  } else if (verifyPendingMount) {
    await evaluate(`(() => {
      const bridge = window.__runicBridge;
      const original = bridge.call;
      const invoke = original.bind(bridge);
      let held = false;
      bridge.call = (name, ...args) => {
        if (!held && name.startsWith("content") && name.endsWith("Mount")) {
          held = true;
          return new Promise((resolve, reject) => {
            window.__releaseMount = () => {
              bridge.call = original;
              invoke(name, ...args).then(resolve, reject);
              return true;
            };
          });
        }
        return invoke(name, ...args);
      };
      return true;
    })()`);
    await click("[data-go=document]");
    await retry(async () => await query('typeof window.__releaseMount') === "function");
    await click("[data-go=home]");
    await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
    await evaluate("window.__releaseMount()");
    await pause(100);
    if ((await snapshot("shell")).state.main.kind !== "home")
      throw new Error("A delayed mount changed the current route.");
    await click("[data-go=document]");
    await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
    await retry(async () => await query('document.querySelector("#compact-pane h2")?.textContent') === "Compact View");
    const documentId = (await snapshot("shell")).state.main.id;
    const editorId = (await snapshot(`content${documentId}`)).state.currentPane.id;
    const editorState = (await snapshot(`content${editorId}`)).state;
    if (editorState?.activationCount !== 1 || editorState?.deactivationCount !== 0)
      throw new Error(`A delayed mount left a stale activation: ${JSON.stringify(editorState)}`);
    if (await query('document.querySelector("#status")?.textContent') !== "Connected.")
      throw new Error("A delayed mount surfaced as a shell error.");
    console.log("REACTIVE_NOTES_PENDING_MOUNT_OK");
  } else {
  const firstShell = await snapshot("shell");
  if (firstShell.state.main.kind !== "home") throw new Error("Top-level router did not start at Home.");

  await click("[data-go=document]");
  await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
  await retry(async () => await query('document.querySelector("#compact-pane h2")?.textContent') === "Compact View");
  const documentId = (await snapshot("shell")).state.main.id;
  const documentRoute = `content${documentId}`;
  const documentState = (await snapshot(documentRoute)).state;
  if (documentState.currentPane.kind !== "editor" || documentState.compactNote.kind !== "editorCompact")
    throw new Error("Nested router or compact View contract chose the wrong presentation kind.");
  const editorId = documentState.currentPane.id;
  const compactId = documentState.compactNote.id;
  if (editorId === compactId) throw new Error("Two View contracts reused one presentation identity.");
  const editorRoute = `content${editorId}`;
  const compactRoute = `content${compactId}`;
  await retry(async () => (await snapshot(editorRoute)).state?.activationCount === 1);
  if ((await snapshot(compactRoute)).state?.activationCount !== 1)
    throw new Error("Two mounted Views activated one ViewModel more than once.");

  await change("#document-pane [data-title]", "Shared note");
  await change("#document-pane [data-body]", "Both Views see this text.");
  await click("[data-save]");
  await retry(async () => (await snapshot(compactRoute)).state?.savedMessage === "Saved Shared note");
  await retry(async () => await query('document.querySelector("#compact-pane [data-title]")?.textContent') === "Shared note");
  await retry(async () => await query('document.querySelector("#compact-pane [data-body]")?.textContent') === "Both Views see this text.");

  await click("[data-pane=preview]");
  await retry(async () => await query('document.querySelector("#document-pane [data-heading]")?.textContent') === "Shared note");
  if ((await snapshot(editorRoute)).error?.kind !== "disconnected")
    throw new Error("The full editor endpoint stayed active after the nested route changed.");
  const whilePreview = (await snapshot(compactRoute)).state;
  if (whilePreview?.activationCount !== 1 || whilePreview?.deactivationCount !== 0)
    throw new Error("The remaining compact View lost the shared activation lease.");

  await click("[data-pane=editor]");
  await retry(async () => await query('document.querySelector("#document-pane [data-title]")?.value') === "Shared note");
  const remounted = (await snapshot(editorRoute)).state;
  if (remounted?.activationCount !== 1 || remounted?.deactivationCount !== 0)
    throw new Error("Remounting the full View restarted an already active ViewModel.");

  await click("[data-go=home]");
  await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
  if ((await snapshot(compactRoute)).error?.kind !== "disconnected")
    throw new Error("The compact endpoint stayed active after leaving Document.");
  await click("[data-go=document]");
  await retry(async () => await query('document.querySelector("#document-pane [data-title]")?.value') === "Shared note");
  const returned = (await snapshot(editorRoute)).state;
  if (returned?.activationCount !== 2 || returned?.deactivationCount !== 1)
    throw new Error(`WhenActivated did not stop and restart across top-level routing: ${JSON.stringify(returned)}`);
  if ((await snapshot(compactRoute)).state?.activationCount !== 2)
    throw new Error("Compact View did not reconnect to the original ViewModel.");

  await evaluate("window.__runicReloadProbe = true; location.reload()");
  try { await retry(async () => await query('window.__runicReloadProbe === undefined && document.readyState === "complete"')); }
  catch (cause) {
    const detail = await query('({ url: location.href, marker: window.__runicReloadProbe, ready: document.readyState, navigation: performance.getEntriesByType("navigation")[0]?.type, status: document.querySelector("#status")?.textContent })');
    throw new Error(`Reload did not create a new document: ${JSON.stringify(detail)}; host: ${output} ${errors}; ${cause}`);
  }
  await retry(async () => await query('document.querySelector("#document-pane [data-title]")?.value') === "Shared note");
  await retry(async () => await query('document.querySelector("#compact-pane [data-title]")?.textContent') === "Shared note");
  const afterReload = (await snapshot(editorRoute)).state;
  if (!afterReload || afterReload.activationCount - afterReload.deactivationCount !== 1)
    throw new Error(`Reload left an incorrect activation lease count: ${JSON.stringify(afterReload)}`);
  if ((await snapshot(compactRoute)).state?.activationCount !== afterReload.activationCount)
    throw new Error("The compact View did not reconnect in the new browser session.");

  await click("[data-go=home]");
  await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
  await click("[data-go=document]");
  await retry(async () => await query('document.querySelector("#document-pane [data-title]")?.value') === "Shared note");
  const afterReloadRoute = (await snapshot(editorRoute)).state;
  if (!afterReloadRoute || afterReloadRoute.activationCount !== afterReload.activationCount + 1
      || afterReloadRoute.deactivationCount !== afterReload.deactivationCount + 1)
    throw new Error(`Navigation after reload did not release and reacquire the lease: ${JSON.stringify(afterReloadRoute)}`);
  await change("#document-pane [data-title]", "Operation roundtrip");
  await retry(async () => (await snapshot(editorRoute)).state?.canSave === true);
  const requestId = "browser-operation-roundtrip";
  const admission = await evaluate(`(async () => JSON.parse(await window.__runicBridge.call(${JSON.stringify(editorRoute + "StartSave")}, ${JSON.stringify(requestId)})))()`);
  if (admission.kind !== "accepted" || admission.requestId !== requestId || typeof admission.contract !== "string")
    throw new Error(`Advanced Save admission lost its wire identity: ${JSON.stringify(admission)}`);
  const identity = JSON.stringify({ contract: admission.contract, requestId });
  const terminal = await evaluate(`(async () => JSON.parse(await window.__runicBridge.call("__runicOperationWait", ${JSON.stringify(identity)})))()`);
  if (terminal.kind !== "succeeded" || terminal.contract !== admission.contract || terminal.requestId !== requestId)
    throw new Error(`Advanced Save did not finish through its window route: ${JSON.stringify(terminal)}`);
  const cancel = await evaluate(`(async () => JSON.parse(await window.__runicBridge.call("__runicOperationCancel", ${JSON.stringify(identity)})))()`);
  if (cancel.kind !== "not-running") throw new Error(`Terminal operation accepted cancellation: ${JSON.stringify(cancel)}`);
  const invalid = await evaluate('(async () => JSON.parse(await window.__runicBridge.call("__runicOperationStatus", "invalid-json")))()');
  if (invalid.kind !== "invalid-request") throw new Error(`Malformed operation identity was accepted: ${JSON.stringify(invalid)}`);
  console.log("REACTIVE_NOTES_BROWSER_OK|nested-routing|view-contract|shared-state|command|shared-activation|route-deactivation|reload-lease|operation-wire");
  }
} finally {
  socket?.close();
  chrome?.kill("SIGTERM");
  if (!host.stdin.writableEnded) host.stdin.end("\n");
  await pause(300);
  if (host.exitCode === null) host.kill("SIGTERM");
  if (profile) await rm(profile, { recursive: true, force: true });
}
