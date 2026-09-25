import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function retry(action, milliseconds = 15_000) {
  const deadline = Date.now() + milliseconds;
  while (Date.now() < deadline) {
    try { const value = await action(); if (value) return value; } catch { /* Server or new page context is starting. */ }
    await delay(40);
  }
  throw new Error("Timed out waiting for the Window Bridge reconnect fixture.");
}

const dll = fileURLToPath(new URL("./bin/Release/net10.0/Runic.Application.CsWebUi.Tests.dll", import.meta.url));
const host = spawn("dotnet", [dll, "--window-bridge-browser-host"], { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "";
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { errors += chunk.toString(); });
let chrome, socket, profile;
let browserSucceeded = false;
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Fixture exited: ${errors}\n${output}`);
    return output.match(/WINDOW_BRIDGE_BROWSER_URL=(https?:\/\/\S+)/)?.[1];
  });
  profile = await mkdtemp(join(tmpdir(), "runic-sdk-window-bridge-reconnect-"));
  chrome = spawn(process.env.WEBUI_BROWSER_PATH ?? "chromium", [
    "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
    "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
    `--user-data-dir=${profile}`, url,
  ], { stdio: "ignore" });
  const port = await retry(async () => Number((await readFile(join(profile, "DevToolsActivePort"), "utf8")).split("\n")[0]));
  const target = await retry(async () => {
    const entries = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    return entries.find(entry => entry.type === "page" && entry.url.startsWith(url));
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
    const finish = pending.get(message.id);
    if (finish) { pending.delete(message.id); finish(message); }
  });
  async function command(method, params = {}) {
    const id = ++nextId;
    const result = new Promise(resolve => pending.set(id, resolve));
    socket.send(JSON.stringify({ id, method, params }));
    return result;
  }
  async function evaluate(expression) {
    const response = await command("Runtime.evaluate", {
      expression, awaitPromise: true, returnByValue: true,
    });
    if (response.error || response.result?.exceptionDetails) throw new Error(JSON.stringify(response));
    return response.result.result.value;
  }
  async function call(route, payload) {
    const descriptor = await evaluate(`globalThis.runicCsWebUi?.endpoints?.[${JSON.stringify(route)}]`);
    if (!descriptor) throw new Error(`No endpoint for ${route}.`);
    return evaluate(`(async () => JSON.parse(await globalThis.webui.call("__runicBridgeDispatch", globalThis.runicCsWebUi.credential, JSON.stringify({
      v: 1, endpoint: ${JSON.stringify(descriptor.endpoint)}, generation: ${JSON.stringify(descriptor.generation)}, payload: ${JSON.stringify({ ...payload, documentEpoch })},
    }))))()`);
  }
  async function beginDocument() {
    const reply = await call("__runicBridgeDocumentBegin", {});
    if (reply.ok) await evaluate(`globalThis.__runicBridgeEndpointHandoff(${JSON.stringify({ v: 1, ...reply.manifest })})`);
    return reply;
  }

  await retry(() => evaluate("globalThis.webui?.isConnected() === true && !!globalThis.runicCsWebUi?.endpoints?.['notes.editor.mount']"));
  const documentEpoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
  const firstEpoch = documentEpoch;
  const firstBegin = await beginDocument();
  if (!firstBegin.ok) throw new Error(`The initial document epoch was not admitted: ${JSON.stringify(firstBegin)}`);
  const firstConnection = await call("notes.debug.connection", {});
  const firstMount = await call("notes.editor.mount", { presentationId: "editor" });
  if (!firstMount.ok) throw new Error(`The initial presentation mount failed: ${JSON.stringify(firstMount)}`);
  if (!(await call("notes.debug.connection", {})).mounted)
    throw new Error("The initial connection did not own an Editor presentation.");

  // CDP can reach the WebUI bridge's current private WebSocket without
  // reloading the page or changing the application script. Closing just that
  // socket exercises WebUI's own reconnect loop in the retained document.
  const bridgeObject = await command("Runtime.evaluate", { expression: "globalThis.webui" });
  const properties = await command("Runtime.getProperties", { objectId: bridgeObject.result.result.objectId, ownProperties: true });
  const privateSocket = properties.result?.privateProperties?.find(property => property.name === "#ws")?.value?.objectId;
  if (!privateSocket) throw new Error("This WebUI version did not expose its current socket to CDP.");
  const closed = await command("Runtime.callFunctionOn", {
    objectId: privateSocket,
    functionDeclaration: "function() { this.close(1000, 'Window Bridge reconnect smoke'); }",
  });
  if (closed.error || closed.result?.exceptionDetails) throw new Error(`Could not close the WebUI socket: ${JSON.stringify(closed)}`);
  await retry(() => evaluate("globalThis.webui?.isConnected() === false"), 5_000);
  await retry(() => evaluate("globalThis.webui?.isConnected() === true"));
  const retainedEpoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
  if (retainedEpoch !== firstEpoch) throw new Error(`Reconnection reloaded the document: ${retainedEpoch}`);
  const reconnectBegin = await beginDocument();
  const replacementConnection = await call("notes.debug.connection", {});
  if (firstConnection.connectionId !== replacementConnection.connectionId
      || firstConnection.clientId !== replacementConnection.clientId)
    throw new Error(`This CS-WebUI host stopped reusing its callback identity: ${JSON.stringify({ firstConnection, replacementConnection })}`);
  if (replacementConnection.current || replacementConnection.mounted)
    throw new Error(`Disconnect did not retire the mounted presentation: ${JSON.stringify(replacementConnection)}`);
  if (reconnectBegin.ok || reconnectBegin.kind !== "stale-document"
      || reconnectBegin.error !== "The native connection is no longer active.")
    throw new Error(`The retained document ticket had an unexpected admission result: ${JSON.stringify(reconnectBegin)}`);
  const remount = await call("notes.editor.mount", { presentationId: "editor" });
  if (remount.ok || remount.kind !== "rejected")
    throw new Error(`An unadmitted reconnect remounted Editor: ${JSON.stringify(remount)}`);
  browserSucceeded = true;
  console.log("SDK_WINDOW_BRIDGE_RECONNECT_LIMIT_OBSERVED|same-document-ticket|native-callback-identity-reused|old-presentation-drained|same-ticket-begin-rejected|editor-remount-rejected");
} finally {
  socket?.close();
  chrome?.kill("SIGTERM");
  host.stdin.end("\n");
  if (browserSucceeded) await retry(() => output.includes("WINDOW_BRIDGE_BROWSER_STOPPED"), 5_000);
  await delay(100);
  if (host.exitCode === null) host.kill("SIGTERM");
  if (profile) await rm(profile, { recursive: true, force: true });
}
