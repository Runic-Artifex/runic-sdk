import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const project = fileURLToPath(new URL("./NotesReactiveViews.csproj", import.meta.url));
const source = fileURLToPath(new URL("./ViewModels.cs", import.meta.url));
const getter = process.env.RUNIC_HOT_RELOAD_SCENARIO === "getter";
const before = getter ? 'public string Greeting => "Reactive Notes";' : 'SavedMessage = $"Saved {Title}";';
const after = getter ? 'public string Greeting => "Updated Reactive Notes";' : 'SavedMessage = $"Hot {Title}";';
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function retry(action, timeout = 30_000) {
  const deadline = Date.now() + timeout;
  let lastError;
  while (Date.now() < deadline) {
    try { if (await action()) return; }
    catch (cause) { lastError = cause; }
    await pause(100);
  }
  throw new Error(`Timed out waiting for .NET Hot Reload: ${lastError ?? "no detail"}`);
}
async function availablePort() {
  const server = createServer();
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
}
async function removeProfile(path) {
  for (let attempt = 0; ; attempt++) {
    try { await rm(path, { recursive: true, force: true }); return; }
    catch (cause) {
      if (cause?.code !== "ENOTEMPTY" || attempt === 9) throw cause;
      await pause(100);
    }
  }
}

const port = await availablePort();
const url = `http://127.0.0.1:${port}/`;
const watch = spawn("dotnet", ["watch", "--non-interactive", "--project", project,
  "run", "-c", "Debug", "--", "--serve-only", "--port", String(port)], {
  detached: true,
  stdio: ["pipe", "pipe", "pipe"],
  env: { ...process.env, DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH: "1",
    DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER: "1", DOTNET_WATCH_RESTART_ON_RUDE_EDIT: "1" },
});
let output = "", errors = "";
watch.stdout.on("data", chunk => { output += chunk.toString(); });
watch.stderr.on("data", chunk => { errors += chunk.toString(); });
let chrome, socket, profile, original;
try {
  await retry(() => {
    if (watch.exitCode !== null) throw new Error(`dotnet watch exited:\n${output}\n${errors}`);
    return output.includes(`:${port}`) && output.includes("Main:");
  }, 90_000);
  profile = await mkdtemp(join(tmpdir(), "runic-dotnet-hot-reload-"));
  chrome = spawn(process.env.WEBUI_BROWSER_PATH ?? "chromium", [
    "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
    "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
    `--user-data-dir=${profile}`, url,
  ], { stdio: "ignore" });
  let debugPort;
  await retry(async () => {
    debugPort = Number((await readFile(join(profile, "DevToolsActivePort"), "utf8")).split("\n")[0]);
    return debugPort;
  });
  let target;
  await retry(async () => {
    const targets = await (await fetch(`http://127.0.0.1:${debugPort}/json/list`)).json();
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
  const snapshot = route => evaluate(`(async () => JSON.parse(await window.__runicBridge.call(${JSON.stringify(route + "Snapshot")})))()`);
  await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
  let route, initial;
  if (getter) {
    const homeId = (await snapshot("shell")).state.main.id;
    route = `content${homeId}`;
    initial = (await snapshot(route)).state;
  } else {
    await query('document.querySelector("[data-go=document]").click()');
    await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
    const documentId = (await snapshot("shell")).state.main.id;
    const editorId = (await snapshot(`content${documentId}`)).state.currentPane.id;
    route = `content${editorId}`;
    await query('(() => { const field = document.querySelector("#document-pane [data-title]"); field.value = "Persistent draft"; field.dispatchEvent(new Event("change", { bubbles: true })); return true; })()');
    await retry(async () => (await snapshot(route)).state?.title === "Persistent draft");
    initial = (await snapshot(route)).state;
  }

  original = await readFile(source, "utf8");
  if (original.split(before).length !== 2) throw new Error("The Hot Reload edit point changed.");
  const errorsBeforeEdit = errors.length;
  await writeFile(source, original.replace(before, after));
  await retry(() => /C# and Razor changes applied|Hot Reload/i.test(errors.slice(errorsBeforeEdit)), 60_000);
  if (getter) {
    await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Updated Reactive Notes", 15_000);
    if ((await snapshot(route)).state.greeting !== "Updated Reactive Notes"
        || (await snapshot("shell")).state.main.id !== route.slice("content".length))
      throw new Error("The computed getter update lost its active View identity.");
    console.log("REACTIVE_NOTES_DOTNET_HOT_RELOAD_OK|getter|browser-push|view-retained");
  } else {
    let updated;
    await retry(async () => {
      if (watch.exitCode !== null) throw new Error(`dotnet watch exited:\n${output}\n${errors}`);
      updated = (await snapshot(route)).state;
      if (!updated || updated.title !== "Persistent draft") return false;
      await query('document.querySelector("[data-save]").click()');
      await pause(170);
      updated = (await snapshot(route)).state;
      return updated?.savedMessage === "Hot Persistent draft";
    }, 45_000);
    if (updated.activationCount !== initial.activationCount || updated.deactivationCount !== initial.deactivationCount)
      throw new Error(`Hot Reload restarted the Editor activation: ${JSON.stringify(updated)}`);
    console.log("REACTIVE_NOTES_DOTNET_HOT_RELOAD_OK|method-body|state-retained|command-updated");
  }
} catch (cause) {
  throw new Error(`${cause}\nwatch output:\n${output.slice(-5000)}\nwatch errors:\n${errors.slice(-2500)}`);
} finally {
  socket?.close();
  chrome?.kill("SIGTERM");
  try { process.kill(-watch.pid, "SIGTERM"); } catch { /* Already stopped. */ }
  await pause(350);
  if (watch.exitCode === null && watch.signalCode === null) {
    try { process.kill(-watch.pid, "SIGKILL"); } catch { /* Already stopped. */ }
  }
  if (original !== undefined) await writeFile(source, original);
  if (profile) await removeProfile(profile);
}
