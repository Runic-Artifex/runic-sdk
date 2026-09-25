import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function retry(action, timeoutMilliseconds = 12_000) {
  const deadline = Date.now() + timeoutMilliseconds;
  while (true) {
    try { if (await action()) return; } catch { /* Host or browser is starting. */ }
    if (Date.now() >= deadline) throw new Error("Timed out waiting for the composed Notes journey.");
    await pause(50);
  }
}

const dll = fileURLToPath(new URL("./bin/Release/net10.0/NotesViewFirst.dll", import.meta.url));
const host = spawn("dotnet", [dll, "--serve-only", ...(process.env.RUNIC_WEB_ROOT ? ["--web-root", process.env.RUNIC_WEB_ROOT] : []), ...(process.env.RUNIC_VIEW_LOCATOR === "splat" ? ["--splat"] : []), ...(process.env.RUNIC_VERIFY_WEB_MOUNT === "1" ? ["--verify-web-mount"] : [])], { stdio: ["pipe", "pipe", "pipe"] });
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
  profile = await mkdtemp(join(tmpdir(), "runic-notes-composed-"));
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
  const change = (selector, value) => evaluate(`(() => { const field = document.querySelector(${JSON.stringify(selector)}); field.value = ${JSON.stringify(value)}; field.dispatchEvent(new Event("input", { bubbles: true })); field.dispatchEvent(new Event("change", { bubbles: true })); return true; })()`);
  const snapshot = route => evaluate(`(async () => JSON.parse(await window.__runicBridge.call(${JSON.stringify(route + "Snapshot")})))()`);

  await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Welcome to composed Notes");
  const firstShell = await snapshot("shell");
  const sidebarId = firstShell.state.sidebar.id;
  if (!sidebarId || firstShell.state.dialog !== null) throw new Error("Wrong initial shell state.");

  await click("[data-go=notes]");
  await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Editor");
  const documentId = (await snapshot("shell")).state.main.id;
  const editorId = (await snapshot(`content${documentId}`)).state.currentPane.id;
  await change("#document-pane input", "Draft");
  await change("#document-pane textarea", "Line one");
  await retry(async () => (await snapshot(`content${editorId}`)).state?.title === "Draft");

  await click("[data-pane=preview]");
  await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Draft");
  if (await query('document.querySelector("#document-pane p")?.textContent') !== "Line one")
    throw new Error("Preview did not observe Editor state.");
  const detachedEditor = await snapshot(`content${editorId}`);
  if (detachedEditor.error?.kind !== "disconnected") throw new Error("Nested Editor endpoint stayed active after its outlet changed.");
  if (!(await snapshot(`content${sidebarId}`)).ok) throw new Error("Independent sidebar disconnected with the Editor.");

  await click("[data-pane=editor]");
  await retry(async () => await query('document.querySelector("#document-pane input")?.value') === "Draft");
  await change("#document-pane textarea", "Line two");
  await click("[data-save]");
  await retry(async () => (await snapshot(`content${editorId}`)).state?.canSave === false);
  await click("[data-pane=preview]");
  await retry(async () => await query('document.querySelector("#document-pane p")?.textContent') === "Line two");
  await pause(350);
  await click("[data-pane=editor]");
  await retry(async () => (await snapshot(`content${editorId}`)).state?.savedMessage === "Saved Draft");
  await retry(async () => await query('document.querySelector("[data-message]")?.textContent') === "Saved Draft");

  await change("#document-pane input", "Throwaway");
  await retry(async () => (await snapshot(`content${editorId}`)).state?.isDirty === true);
  await evaluate('document.querySelector("[data-go=home]").focus()');
  await click("[data-go=home]");
  await retry(async () => await query('document.querySelector("#modal [role=dialog]") !== null'));
  const dialogId = (await snapshot("shell")).state.dialog.id;
  await retry(async () => await query('document.querySelector("#modal [data-cancel]")?.disabled === false'));
  await evaluate('document.activeElement.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }))');
  await retry(async () => await query('document.querySelector("#modal [role=dialog]") === null'));
  if (await query('document.querySelector("#main h1")?.textContent') !== "Document")
    throw new Error("Cancel unexpectedly left the document.");
  if (await query('document.activeElement?.getAttribute("data-go")') !== "home")
    throw new Error("Dialog did not return focus to the sidebar.");
  if ((await snapshot(`content${dialogId}`)).error?.kind !== "disconnected")
    throw new Error("Closed dialog endpoint stayed active.");

  await click("[data-go=home]");
  await retry(async () => await query('document.querySelector("#modal [data-confirm]")?.disabled === false'));
  await click("[data-confirm]");
  await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Welcome to composed Notes");
  await click("[data-go=notes]");
  await retry(async () => await query('document.querySelector("#document-pane input")?.value') === "Draft");
  await evaluate("location.reload()");
  await retry(async () => await query('document.querySelector("#document-pane input")?.value') === "Draft");
  try {
    await retry(async () => await query('document.querySelector("#status")?.textContent') === "Connected.");
  } catch (error) {
    throw new Error(`Reconnect did not settle: ${await query('document.querySelector("#status")?.textContent')}; ${error}`);
  }
  if (process.env.RUNIC_VERIFY_WEB_MOUNT === "1") {
    host.stdin.end("\n");
    await retry(() => host.exitCode !== null);
    if (host.exitCode !== 0 || !output.includes("WEB_MOUNT_BROWSER_OK"))
      throw new Error(`Web mount verification failed: ${output}\n${errors}`);
  }
  console.log("NOTES_COMPOSED_OK|sidebar|nested-pane|modal|discard|async-detach|reload" + (process.env.RUNIC_VERIFY_WEB_MOUNT === "1" ? "|web-mount" : ""));
} finally {
  socket?.close();
  chrome?.kill("SIGTERM");
  if (!host.stdin.writableEnded) host.stdin.end("\n");
  await pause(300);
  if (host.exitCode === null) host.kill("SIGTERM");
  if (profile) await rm(profile, { recursive: true, force: true });
}
