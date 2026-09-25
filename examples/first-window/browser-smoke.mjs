import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
function waitForExit(child, timeoutMs) {
  if (child.exitCode !== null || child.signalCode !== null) return Promise.resolve(true);
  return new Promise(resolve => {
    const onClose = () => { clearTimeout(timer); resolve(true); };
    const timer = setTimeout(() => { child.off("close", onClose); resolve(false); }, timeoutMs);
    child.once("close", onClose);
  });
}

async function stopChrome(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return true;
  const gracefulExit = waitForExit(child, 3_000);
  child.kill("SIGTERM");
  if (await gracefulExit) return true;
  const forcedExit = waitForExit(child, 3_000);
  child.kill("SIGKILL");
  return await forcedExit;
}

async function retry(action, label) {
  const deadline = Date.now() + 20_000;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const result = await action();
      if (result) return result;
    } catch (error) { lastError = error; }
    await pause(50);
  }
  throw new Error(`Timed out waiting for ${label}: ${lastError ?? "no detail"}`);
}

const dll = process.env.RUNIC_FIRST_WINDOW_DLL
  ?? fileURLToPath(new URL("./bin/Release/net10.0/FirstWindow.dll", import.meta.url));
const native = process.env.RUNIC_FIRST_WINDOW_EXECUTABLE;
const host = spawn(native ?? "dotnet", native ? ["--serve-only"] : [dll, "--serve-only"],
  { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "", chrome, socket, profile;
host.stdout.on("data", chunk => { output += chunk; });
host.stderr.on("data", chunk => { errors += chunk; });
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    return output.match(/https?:\/\/[^\s]+/)?.[0];
  }, "host URL");
  profile = await mkdtemp(join(tmpdir(), "runic-first-window-"));
  chrome = spawn(process.env.WEBUI_BROWSER_PATH ?? "chromium", [
    "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
    "--no-first-run", "--remote-debugging-port=0", `--user-data-dir=${profile}`, url
  ], { stdio: "ignore" });
  const port = await retry(async () => Number((await readFile(join(profile, "DevToolsActivePort"), "utf8")).split("\n")[0]), "DevTools port");
  const target = await retry(async () => (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json())
    .find(entry => entry.type === "page" && entry.url.startsWith(url)), "browser page");
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  let nextId = 0;
  const pending = new Map();
  socket.addEventListener("message", ({ data }) => {
    const message = JSON.parse(data);
    if (pending.has(message.id)) {
      pending.get(message.id)(message);
      pending.delete(message.id);
    }
  });
  async function evaluate(expression) {
    const id = ++nextId;
    const response = new Promise(resolve => pending.set(id, resolve));
    socket.send(JSON.stringify({ id, method: "Runtime.evaluate", params: { expression, awaitPromise: true, returnByValue: true } }));
    const result = await response;
    if (result.error || result.result?.exceptionDetails) throw new Error(JSON.stringify(result));
    return result.result.result.value;
  }
  const state = () => evaluate(`({count: document.querySelector("#count")?.textContent,
    step: document.querySelector("#step")?.value,
    status: document.querySelector("#status")?.textContent})`);
  await retry(async () => (await state()).status === "Connected to the .NET ViewModel.", "initial connection");
  if ((await state()).count !== "0") throw new Error("Initial count was not zero.");
  await evaluate('document.querySelector("#increment").click()');
  await retry(async () => (await state()).count === "1", "first command");
  await evaluate('document.querySelector("#step").focus(); document.querySelector("#step").value = "3"; document.querySelector("#step").blur()');
  try {
    await retry(async () => (await state()).status === "Step updated.", "writable property");
  } catch (error) {
    throw new Error(`${error}; final state: ${JSON.stringify(await state())}; host: ${errors}`);
  }
  await evaluate('document.querySelector("#increment").click()');
  await retry(async () => (await state()).count === "4", "updated command");
  await evaluate("location.reload()");
  await retry(async () => (await state()).count === "4" && (await state()).step === "3", "reload");
  console.log("FIRST_WINDOW_OK|snapshot|command|property|reload");
} finally {
  socket?.close();
  const chromeStopped = await stopChrome(chrome);
  host.stdin.end("\n");
  await pause(250);
  if (host.exitCode === null) host.kill("SIGTERM");
  if (!chromeStopped) throw new Error("Chromium did not exit after SIGKILL; its profile was left for runner cleanup.");
  if (profile) await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
}
