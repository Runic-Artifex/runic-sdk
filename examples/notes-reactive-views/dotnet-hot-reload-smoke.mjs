import { spawn } from "node:child_process";
import { readFile, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, waitFor } from "../shared/smoke.mjs";

const project = fileURLToPath(new URL("./NotesReactiveViews.csproj", import.meta.url));
const source = fileURLToPath(new URL("./ViewModels.cs", import.meta.url));
const getter = process.env.RUNIC_HOT_RELOAD_SCENARIO === "getter";
const before = getter ? 'public string Greeting => "Reactive Notes";' : 'SavedMessage = $"Saved {Title}";';
const after = getter ? 'public string Greeting => "Updated Reactive Notes";' : 'SavedMessage = $"Hot {Title}";';
let browser;
// The outer catch appends the dotnet watch output.
const detail = async () => browser ? await browser.diagnostics() : "browser: not started";
const retry = (condition, timeout = 30_000) => waitFor(condition, { timeout, interval: 100, label: ".NET Hot Reload", detail });
async function availablePort() {
  const server = createServer();
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
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
let original, completed = false, cleanupError;
try {
  await retry(() => {
    if (watch.exitCode !== null) throw new Error(`dotnet watch exited:\n${output}\n${errors}`);
    return output.includes(`:${port}`) && output.includes("Main:");
  }, 90_000);
  browser = await launchChromium(url, { profilePrefix: "runic-dotnet-hot-reload-", timeout: 30_000 });
  const { evaluate } = browser;
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
  completed = true;
} catch (cause) {
  throw new Error(`${cause}\nwatch output:\n${output.slice(-5000)}\nwatch errors:\n${errors.slice(-2500)}`);
} finally {
  await browser?.close().catch(error => { cleanupError = error; });
  try { process.kill(-watch.pid, "SIGTERM"); } catch { /* Already stopped. */ }
  await pause(350);
  if (watch.exitCode === null && watch.signalCode === null) {
    try { process.kill(-watch.pid, "SIGKILL"); } catch { /* Already stopped. */ }
  }
  if (original !== undefined) await writeFile(source, original);
  // Report a Chromium cleanup failure without hiding the journey's own failure.
  if (cleanupError) { if (completed) throw cleanupError; console.error(cleanupError); }
}
