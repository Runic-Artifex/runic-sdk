import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function retry(action, label) {
  const deadline = Date.now() + 12_000;
  let lastError;
  while (Date.now() < deadline) {
    try { const value = await action(); if (value) return value; }
    catch (error) { lastError = error; }
    await pause(50);
  }
  throw new Error(`Timed out waiting for ${label}: ${lastError ?? "no detail"}`);
}

const dll = process.env.RUNIC_DESKTOP_VIEWS_DLL
  ?? fileURLToPath(new URL("./bin/Release/net10.0/FirstWindowDesktop.dll", import.meta.url));
const host = spawn("dotnet", [dll, "--serve-only"], { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "", chrome, socket, profile;
host.stdout.on("data", chunk => { output += chunk; });
host.stderr.on("data", chunk => { errors += chunk; });
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    return output.match(/https?:\/\/[^\s]+/)?.[0];
  }, "Desktop surface URL");
  profile = await mkdtemp(join(tmpdir(), "runic-desktop-views-"));
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
  const count = () => evaluate('document.querySelector("#count")?.value');
  await retry(async () => (await count()) === "0", `initial generated snapshot; host: ${errors}`);
  await evaluate('document.querySelector("#increment").click()');
  await retry(async () => (await count()) === "1", `ReactiveUI command; host: ${errors}`);
  await evaluate("location.reload()");
  await retry(async () => (await count()) === "1", `reconnected snapshot; host: ${errors}`);
  console.log("DESKTOP_VIEWS_BROWSER_OK|snapshot|reactive-command|reload");
} finally {
  socket?.close();
  chrome?.kill("SIGTERM");
  host.stdin.end("\n");
  await pause(250);
  if (host.exitCode === null) host.kill("SIGTERM");
  if (profile) await rm(profile, { recursive: true, force: true });
}
