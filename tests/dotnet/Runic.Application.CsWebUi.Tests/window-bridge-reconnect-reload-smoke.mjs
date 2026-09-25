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
  throw new Error("Timed out waiting for the Window Bridge reconnect reload fixture.");
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
  profile = await mkdtemp(join(tmpdir(), "runic-sdk-window-bridge-reconnect-reload-"));
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
  async function callDescriptor(descriptor, payload) {
    return evaluate(`(async () => JSON.parse(await globalThis.webui.call("__runicBridgeDispatch", globalThis.runicCsWebUi.credential, JSON.stringify({
      v: 1, endpoint: ${JSON.stringify(descriptor.endpoint)}, generation: ${JSON.stringify(descriptor.generation)}, payload: ${JSON.stringify(payload)},
    }))))()`);
  }
  async function call(route, payload) {
    const descriptor = await evaluate(`globalThis.runicCsWebUi?.endpoints?.[${JSON.stringify(route)}]`);
    if (!descriptor) throw new Error(`No endpoint for ${route}.`);
    return callDescriptor(descriptor, { ...payload, documentEpoch });
  }
  async function beginDocument() {
    const reply = await call("__runicBridgeDocumentBegin", {});
    if (reply.ok) await evaluate(`globalThis.__runicBridgeEndpointHandoff(${JSON.stringify({ v: 1, ...reply.manifest })})`);
    return reply;
  }

  await retry(() => evaluate("globalThis.webui?.isConnected() === true && !!globalThis.runicCsWebUi?.endpoints?.['notes.editor.mount']"));
  let documentEpoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
  const firstEpoch = documentEpoch;
  const firstBegin = await beginDocument();
  if (!firstBegin.ok) throw new Error(`The initial document epoch was not admitted: ${JSON.stringify(firstBegin)}`);
  const firstConnection = await call("notes.debug.connection", {});
  const firstMount = await call("notes.editor.mount", { presentationId: "editor" });
  if (!firstMount.ok) throw new Error(`The initial presentation mount failed: ${JSON.stringify(firstMount)}`);
  if (!(await call("notes.debug.connection", {})).mounted)
    throw new Error("The initial connection did not own an Editor presentation.");
  const initialTitle = await call("notes.title.set", { presentationId: "editor", requestId: "before-reconnect", value: "Before reconnect" });
  if (!initialTitle.ok || initialTitle.current?.value !== "Before reconnect")
    throw new Error(`The initial title update failed: ${JSON.stringify(initialTitle)}`);
  const save = await call("notes.save.start", { presentationId: "editor", requestId: "accepted-before-reconnect" });
  if (!save.accepted || save.kind !== "running")
    throw new Error(`Window-owned Save was not accepted: ${JSON.stringify(save)}`);
  const oldTitleDescriptor = await evaluate("globalThis.runicCsWebUi.endpoints['notes.title.get']");

  // CDP can reach the WebUI bridge's current private WebSocket without
  // reloading the page or changing the application script. Closing just that
  // socket exercises WebUI's own reconnect loop in the retained document.
  const bridgeObject = await command("Runtime.evaluate", { expression: "globalThis.webui" });
  const properties = await command("Runtime.getProperties", { objectId: bridgeObject.result.result.objectId, ownProperties: true });
  const privateSocket = properties.result?.privateProperties?.find(property => property.name === "#ws")?.value?.objectId;
  if (!privateSocket) throw new Error("This WebUI version did not expose its current socket to CDP.");
  const closed = await command("Runtime.callFunctionOn", {
    objectId: privateSocket,
    functionDeclaration: "function() { this.close(1000, 'Window Bridge reconnect reload smoke'); }",
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
  const oldWindowObserver = await call("notes.debug.accepted-save.status", {});
  if (oldWindowObserver.kind !== "rejected")
    throw new Error(`The disconnected document reached the fixture window observer: ${JSON.stringify(oldWindowObserver)}`);

  // The fallback is a full navigation after WebUI's same-document reconnect.
  // Waiting for a different server-issued ticket proves that Page.reload
  // completed before we admit or remount the replacement document.
  await command("Page.reload", { ignoreCache: true });
  documentEpoch = await retry(async () => {
    const epoch = await evaluate("globalThis.runicCsWebUi?.documentEpoch");
    return epoch && epoch !== firstEpoch ? epoch : null;
  });
  await retry(() => evaluate("globalThis.webui?.isConnected() === true"));
  const replacementBegin = await beginDocument();
  if (!replacementBegin.ok)
    throw new Error(`The fallback document ticket was not admitted: ${JSON.stringify(replacementBegin)}`);
  const fallbackConnection = await call("notes.debug.connection", {});
  if (!fallbackConnection.current || fallbackConnection.mounted)
    throw new Error(`Fallback admission had an unexpected presentation state: ${JSON.stringify(fallbackConnection)}`);
  const oldDocumentTitle = await callDescriptor(oldTitleDescriptor, { documentEpoch: firstEpoch, presentationId: "editor" });
  if (oldDocumentTitle.ok || oldDocumentTitle.kind !== "rejected")
    throw new Error(`The old document regained Editor access: ${JSON.stringify(oldDocumentTitle)}`);
  const replacementMount = await call("notes.editor.mount", { presentationId: "editor" });
  if (!replacementMount.ok)
    throw new Error(`The fallback document did not remount Editor: ${JSON.stringify(replacementMount)}`);
  const replacementTitle = await call("notes.title.get", { presentationId: "editor" });
  if (!replacementTitle.ok || replacementTitle.snapshot?.value !== "Before reconnect")
    throw new Error(`The fallback document could not read the window model: ${JSON.stringify(replacementTitle)}`);

  // The standard status route remains bound to the accepting document. The
  // fixture-only window observer checks that accepted work itself survived
  // the disconnect and can finish after the new document is mounted.
  const newDocumentStatus = await call("notes.save.status", { requestId: "accepted-before-reconnect" });
  if (newDocumentStatus.kind !== "rejected")
    throw new Error(`The replacement document inherited the old status authority: ${JSON.stringify(newDocumentStatus)}`);
  const held = await call("notes.debug.accepted-save.status", {});
  if (held.kind !== "running" || held.requestId !== "accepted-before-reconnect")
    throw new Error(`Accepted window work did not survive reconnect: ${JSON.stringify(held)}`);
  const released = await call("notes.debug.accepted-save.release", {});
  if (!released.ok) throw new Error(`The fallback document could not release accepted work: ${JSON.stringify(released)}`);
  const completed = await retry(async () => {
    const status = await call("notes.debug.accepted-save.status", {});
    return status.kind === "succeeded" ? status : null;
  });
  if (completed.error !== null || JSON.parse(completed.state)?.savedTitle !== "Before reconnect")
    throw new Error(`Accepted work did not finish with its captured model state: ${JSON.stringify(completed)}`);
  browserSucceeded = true;
} finally {
  socket?.close();
  chrome?.kill("SIGTERM");
  host.stdin.end("\n");
  if (browserSucceeded) await retry(() => output.includes("WINDOW_BRIDGE_BROWSER_STOPPED"), 5_000);
  await delay(100);
  if (host.exitCode === null) host.kill("SIGTERM");
  if (profile) await rm(profile, { recursive: true, force: true });
}
if (browserSucceeded)
  console.log("SDK_WINDOW_BRIDGE_RECONNECT_RELOAD_OK|same-document-limit-observed|fresh-host-ticket|fallback-navigation-completed|replacement-admitted|editor-remounted|title-restored|old-document-rejected|accepted-window-work-survived|scope-drained");
