import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function retry(action, timeout = 15_000) {
  const deadline = Date.now() + timeout;
  let lastError;
  while (Date.now() < deadline) {
    try { if (await action()) return; }
    catch (cause) { lastError = cause; }
    await pause(50);
  }
  throw new Error(`Timed out waiting for concurrent Reactive Notes clients: ${lastError ?? "no detail"}`);
}

async function removeProfile(profile) {
  for (let attempt = 0; ; attempt++) {
    try { await rm(profile, { recursive: true, force: true }); return; }
    catch (cause) {
      if (cause?.code !== "ENOTEMPTY" || attempt === 9) throw cause;
      await pause(100);
    }
  }
}

async function openClient(url) {
  const profile = await mkdtemp(join(tmpdir(), "runic-reactive-client-"));
  const chrome = spawn(process.env.WEBUI_BROWSER_PATH ?? "chromium", [
    "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
    "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
    `--user-data-dir=${profile}`, url,
  ], { stdio: "ignore" });
  let socket;
  async function stopChrome() {
    if (chrome.exitCode === null && chrome.signalCode === null)
      await new Promise(resolve => { chrome.once("exit", resolve); chrome.kill("SIGTERM"); });
  }
  try {
    let port;
    await retry(async () => {
      if (chrome.exitCode !== null) throw new Error(`Chromium exited: ${chrome.exitCode}`);
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
    return {
      evaluate,
      snapshot: route => evaluate(`(async () => JSON.parse(await window.__runicBridge.call(${JSON.stringify(route + "Snapshot")})))()`),
      call: route => evaluate(`window.__runicBridge.call(${JSON.stringify(route)})`),
      kill: () => chrome.kill("SIGKILL"),
      async dispose() {
        socket?.close();
        await stopChrome();
        await removeProfile(profile);
      },
    };
  } catch (cause) {
    socket?.close();
    await stopChrome();
    await removeProfile(profile);
    throw cause;
  }
}

const dll = fileURLToPath(new URL("./bin/Release/net10.0/NotesReactiveViews.dll", import.meta.url));
const webRoot = process.env.RUNIC_WEB_ROOT;
const host = spawn("dotnet", [dll, "--serve-only", "--verify-multi-client", "--multi-client",
  ...(webRoot ? ["--web-root", webRoot] : [])], {
  stdio: ["pipe", "pipe", "pipe"],
});
let output = "", errors = "";
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { errors += chunk.toString(); });
let first, second;
try {
  let url;
  await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    url = output.match(/https?:\/\/[^\s]+/)?.[0];
    return url;
  });
  first = await openClient(url);
  await retry(async () => await first.evaluate('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
  second = await openClient(url);
  await retry(async () => await second.evaluate('document.querySelector("#main h1")?.textContent') === "Reactive Notes");

  await first.evaluate('document.querySelector("[data-go=document]").click()');
  for (const client of [first, second]) {
    await retry(async () => await client.evaluate('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
    await retry(async () => await client.evaluate('document.querySelector("#compact-pane h2")?.textContent') === "Compact View");
  }
  const documentId = (await first.snapshot("shell")).state.main.id;
  const editorId = (await first.snapshot(`content${documentId}`)).state.currentPane.id;
  const editorRoute = `content${editorId}`;
  await retry(async () => (await first.snapshot(editorRoute)).state?.activationCount === 1);

  // Both clients command the same routers. Settle on Editor after overlapping
  // calls and require both component trees to recover from transient routes.
  await Promise.all([first.call("shellOpenHome"), second.call("shellOpenDocument")]);
  await second.call("shellOpenDocument");
  for (const client of [first, second])
    await retry(async () => await client.evaluate('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
  await Promise.all([
    first.call(`content${documentId}ShowPreview`),
    second.call(`content${documentId}ShowEditor`),
  ]);
  await second.call(`content${documentId}ShowEditor`);
  for (const client of [first, second])
    await retry(async () => await client.evaluate('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
  const settled = (await second.snapshot(editorRoute)).state;
  if (settled.activationCount - settled.deactivationCount !== 1)
    throw new Error(`Overlapping routes left an unbalanced activation: ${JSON.stringify(settled)}`);

  first.kill();
  await pause(350);
  host.stdin.write("\n");
  await retry(() => output.includes("CLIENT_STILL_ACTIVE"));
  const afterFirstExit = (await second.snapshot(editorRoute)).state;
  if (afterFirstExit.activationCount - afterFirstExit.deactivationCount !== 1)
    throw new Error(`The surviving browser lost its activation: ${JSON.stringify(afterFirstExit)}`);
  await second.evaluate('(() => { const field = document.querySelector("#document-pane [data-title]"); field.value = "Surviving client"; field.dispatchEvent(new Event("change", { bubbles: true })); return true; })()');
  await retry(async () => await second.evaluate('document.querySelector("#compact-pane [data-title]")?.textContent') === "Surviving client");
  second.kill();
  host.stdin.end("\n");
  await retry(() => output.includes("CLIENTS_RELEASED"));
  await retry(() => host.exitCode !== null);
  if (host.exitCode !== 0)
    throw new Error(`The final browser exit did not release the View: ${output}\n${errors}`);
  console.log("REACTIVE_NOTES_CONCURRENCY_OK|two-clients|overlapping-routes|survivor|final-disconnect");
} finally {
  await first?.dispose();
  await second?.dispose();
  if (!host.stdin.writableEnded) host.stdin.end("\n");
  await pause(300);
  if (host.exitCode === null) host.kill("SIGTERM");
}
