import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function retry(action, milliseconds = 15_000) {
  const deadline = Date.now() + milliseconds;
  while (Date.now() < deadline) {
    try { const value = await action(); if (value) return value; } catch { /* Server/browser startup. */ }
    await delay(40);
  }
  throw new Error("Timed out waiting for the Window Bridge browser fixture.");
}

const dll = fileURLToPath(new URL("./bin/Release/net10.0/Runic.Application.CsWebUi.Tests.dll", import.meta.url));
const host = spawn("dotnet", [dll, "--window-bridge-browser-host"], { stdio: ["pipe", "pipe", "pipe"] });
let output = "";
let errors = "";
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { errors += chunk.toString(); });
let chrome, socket, profile;
let browserSucceeded = false;
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Fixture exited: ${errors}\n${output}`);
    return output.match(/WINDOW_BRIDGE_BROWSER_URL=(https?:\/\/\S+)/)?.[1];
  });
  profile = await mkdtemp(join(tmpdir(), "runic-sdk-window-bridge-"));
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
  function evaluator(targetSocket) {
    let nextId = 0;
    const pending = new Map();
    targetSocket.addEventListener("message", ({ data }) => {
      const message = JSON.parse(data);
      const finish = pending.get(message.id);
      if (finish) { pending.delete(message.id); finish(message); }
    });
    const command = async (method, params = {}) => {
      const id = ++nextId;
      const result = new Promise(resolve => pending.set(id, resolve));
      targetSocket.send(JSON.stringify({ id, method, params }));
      return result;
    };
    const evaluate = async expression => {
      const response = await command("Runtime.evaluate", {
        expression, awaitPromise: true, returnByValue: true,
      });
      if (response.error || response.result?.exceptionDetails) throw new Error(JSON.stringify(response));
      return response.result.result.value;
    };
    return { command, evaluate };
  }
  const { command, evaluate } = evaluator(socket);
  await retry(() => evaluate("globalThis.webui?.isConnected() === true"));
  async function callWith(evaluateOnPage, route, payload) {
    const expression = `(async () => { const endpoint = globalThis.runicCsWebUi.endpoints[${JSON.stringify(route)}]; if (!endpoint) throw new Error("No endpoint for " + ${JSON.stringify(route)}); return JSON.parse(await globalThis.webui.call("__runicBridgeDispatch", globalThis.runicCsWebUi.credential, JSON.stringify({ v: 1, endpoint: endpoint.endpoint, generation: endpoint.generation, payload: ${JSON.stringify(payload)} }))); })()`;
    return evaluateOnPage(expression);
  }
  let documentEpoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
  async function callDescriptor(descriptor, payload) {
    return evaluate(`(async () => JSON.parse(await globalThis.webui.call("__runicBridgeDispatch", globalThis.runicCsWebUi.credential, JSON.stringify({ v: 1, endpoint: ${JSON.stringify(descriptor.endpoint)}, generation: ${JSON.stringify(descriptor.generation)}, payload: ${JSON.stringify({ ...payload, documentEpoch })} }))))()`);
  }
  const call = (route, payload) => callWith(evaluate, route, { ...payload, documentEpoch });
  async function beginDocument() {
    const reply = await call("__runicBridgeDocumentBegin", {});
    if (reply.ok) await evaluate(`globalThis.__runicBridgeEndpointHandoff(${JSON.stringify({ v: 1, ...reply.manifest })})`);
    return reply;
  }

  const began = await beginDocument();
  if (!began.ok) throw new Error(`The browser document epoch was not admitted: ${JSON.stringify(began)}`);
  const safeEndpointMap = await evaluate("Object.getPrototypeOf(globalThis.runicCsWebUi.endpoints) === null && Object.hasOwn(globalThis.runicCsWebUi.endpoints, '__proto__') && globalThis.runicCsWebUi.endpoints.toString === undefined");
  const specialRoute = await call("__proto__", {});
  if (!safeEndpointMap || !specialRoute.ok || specialRoute.kind !== "special-route")
    throw new Error(`A prototype-like route was lost or inherited endpoints leaked: ${JSON.stringify(specialRoute)}`);

  const firstMount = await call("notes.editor.mount", { presentationId: "editor" });
  if (!firstMount.ok) throw new Error(`First browser did not mount Editor: ${JSON.stringify(firstMount)}`);

  const initial = await call("notes.title.get", { presentationId: "editor" });
  if (!initial.ok || initial.snapshot.value !== "Draft" || initial.snapshot.version !== 0)
    throw new Error(`Initial title snapshot was not typed: ${JSON.stringify(initial)}`);
  const direct = await call("notes.title.set", { presentationId: "editor", requestId: "browser-direct", value: "From browser" });
  if (!direct.ok || direct.kind !== "applied" || direct.current.value !== "From browser" || direct.current.version !== 1)
    throw new Error(`Direct title setter was not acknowledged: ${JSON.stringify(direct)}`);
  const conflict = await call("notes.title.writeChecked", {
    presentationId: "editor", requestId: "browser-stale", value: "Must not apply", expected: initial.snapshot,
  });
  if (conflict.kind !== "conflict" || conflict.current.value !== "From browser" || conflict.current.version !== 1)
    throw new Error(`Stale checked title did not conflict: ${JSON.stringify(conflict)}`);
  const applied = await call("notes.title.writeChecked", {
    presentationId: "editor", requestId: "browser-checked", value: "Reconciled", expected: conflict.current,
  });
  if (!applied.ok || applied.kind !== "applied" || applied.current.value !== "Reconciled" || applied.current.version !== 2)
    throw new Error(`Reconciled checked title did not apply: ${JSON.stringify(applied)}`);
  const duplicate = await call("notes.title.writeChecked", {
    presentationId: "editor", requestId: "browser-checked", value: "Reconciled", expected: conflict.current,
  });
  if (JSON.stringify(duplicate) !== JSON.stringify(applied))
    throw new Error(`Duplicate request did not replay its retained receipt: ${JSON.stringify(duplicate)}`);
  const final = await call("notes.title.get", { presentationId: "editor" });
  if (final.snapshot.value !== "Reconciled" || final.snapshot.version !== 2)
    throw new Error(`Final title snapshot disagreed with receipts: ${JSON.stringify(final)}`);

  const nativeBeforeDialog = await call("notes.debug.native-bindings", {});
  if (nativeBeforeDialog.registrations !== 1)
    throw new Error(`The fixture did not start with one fixed native dispatcher: ${JSON.stringify(nativeBeforeDialog)}`);
  const dialogOpened = await call("notes.dialog.open", {});
  if (!dialogOpened.ok || dialogOpened.existing || !dialogOpened.reference?.Id)
    throw new Error(`The post-load dialog was not created: ${JSON.stringify(dialogOpened)}`);
  const firstDialogRoute = `content${dialogOpened.reference.Id}`;
  const firstDialog = await retry(async () => {
    const descriptor = await evaluate(`globalThis.runicCsWebUi.endpoints[${JSON.stringify(firstDialogRoute)}]`);
    return descriptor ?? null;
  });
  const firstDialogReply = await call(firstDialogRoute, {});
  if (!firstDialogReply.ok || firstDialogReply.kind !== "dialog")
    throw new Error(`The post-load dialog descriptor was not dispatchable: ${JSON.stringify(firstDialogReply)}`);
  const nativeDuringDialog = await call("notes.debug.native-bindings", {});
  if (nativeDuringDialog.registrations !== 1)
    throw new Error(`Opening a dynamic dialog added a native binding: ${JSON.stringify(nativeDuringDialog)}`);

  const dialogClosed = await call("notes.dialog.close", {});
  if (!dialogClosed.ok) throw new Error(`The dynamic dialog did not close: ${JSON.stringify(dialogClosed)}`);
  await retry(async () => (await evaluate(`globalThis.runicCsWebUi.endpoints[${JSON.stringify(firstDialogRoute)}] === undefined`)) || null);
  const staleDialogReply = await callDescriptor(firstDialog, {});
  if (staleDialogReply.error?.kind !== "disconnected")
    throw new Error(`A retired dialog descriptor reached a live endpoint: ${JSON.stringify(staleDialogReply)}`);

  const dialogReopened = await call("notes.dialog.open", {});
  if (!dialogReopened.ok || dialogReopened.existing || !dialogReopened.reference?.Id)
    throw new Error(`The dialog was not recreated after retirement: ${JSON.stringify(dialogReopened)}`);
  const replacementDialogRoute = `content${dialogReopened.reference.Id}`;
  const replacementDialog = await retry(async () => {
    const descriptor = await evaluate(`globalThis.runicCsWebUi.endpoints[${JSON.stringify(replacementDialogRoute)}]`);
    return descriptor ?? null;
  });
  if (JSON.stringify(replacementDialog) === JSON.stringify(firstDialog))
    throw new Error("The recreated dialog received the retired endpoint descriptor.");
  const replacementReply = await call(replacementDialogRoute, {});
  if (!replacementReply.ok || replacementReply.kind !== "dialog")
    throw new Error(`The replacement dialog descriptor was not dispatchable: ${JSON.stringify(replacementReply)}`);
  const revisionBeforeStaleHandoff = await evaluate("globalThis.runicCsWebUi.endpointRevision");
  const staleIgnored = await evaluate(`(() => { globalThis.__runicBridgeEndpointHandoff({ v: 1, revision: ${JSON.stringify(revisionBeforeStaleHandoff - 1)}, endpoints: { stale: ${JSON.stringify(firstDialog)} } }); return globalThis.runicCsWebUi.endpoints.stale === undefined; })()`);
  if (!staleIgnored)
    throw new Error("A stale endpoint handoff revision changed the current browser map.");
  const documentBeforeReload = documentEpoch;
  await command("Page.reload", { ignoreCache: true });
  documentEpoch = await retry(async () => {
    const epoch = await evaluate("globalThis.runicCsWebUi?.documentEpoch");
    return epoch && epoch !== documentBeforeReload ? epoch : null;
  });
  await retry(() => evaluate("globalThis.webui?.isConnected() === true"));
  const reloadDescriptor = await retry(async () => {
    const descriptor = await evaluate(`globalThis.runicCsWebUi.endpoints[${JSON.stringify(replacementDialogRoute)}]`);
    return descriptor ?? null;
  });
  if (JSON.stringify(reloadDescriptor) !== JSON.stringify(replacementDialog))
    throw new Error(`Reload did not bootstrap the latest dynamic endpoint map: ${JSON.stringify(reloadDescriptor)}`);
  const beganAfterReload = await beginDocument();
  if (!beganAfterReload.ok)
    throw new Error(`The replacement browser document epoch was not admitted: ${JSON.stringify(beganAfterReload)}`);
  const remountAfterReload = await call("notes.editor.mount", { presentationId: "editor" });
  if (!remountAfterReload.ok)
    throw new Error(`The fresh browser document did not remount Editor: ${JSON.stringify(remountAfterReload)}`);
  const dialogRemounted = await call("notes.dialog.open", {});
  if (!dialogRemounted.ok || !dialogRemounted.existing || dialogRemounted.reference.Id !== dialogReopened.reference.Id)
    throw new Error(`The live dialog was not remounted in the replacement document: ${JSON.stringify(dialogRemounted)}`);
  const reloadedDialogReply = await call(replacementDialogRoute, {});
  if (!reloadedDialogReply.ok || reloadedDialogReply.kind !== "dialog")
    throw new Error(`The reloaded dialog descriptor was not dispatchable: ${JSON.stringify(reloadedDialogReply)}`);
  const nativeAfterDialog = await call("notes.debug.native-bindings", {});
  if (nativeAfterDialog.registrations !== 1)
    throw new Error(`Retiring and recreating a dynamic dialog changed native binding count: ${JSON.stringify(nativeAfterDialog)}`);

  const save = await call("notes.save.start", { presentationId: "editor", requestId: "browser-save" });
  if (!save.accepted || save.kind !== "running" || save.requestId !== "browser-save")
    throw new Error(`Save was not admitted before Editor detachment: ${JSON.stringify(save)}`);
  const secondSave = await call("notes.save.start", { presentationId: "editor", requestId: "browser-save-2" });
  if (!secondSave.accepted || secondSave.kind !== "running" || secondSave.requestId !== "browser-save-2")
    throw new Error(`A second Save was not admitted from the same document: ${JSON.stringify(secondSave)}`);
  const detached = await call("notes.navigate.preview", {});
  if (!detached.ok) throw new Error(`Editor did not detach: ${JSON.stringify(detached)}`);
  const whileHeld = await call("notes.save.status", { requestId: "browser-save" });
  const secondWhileHeld = await call("notes.save.status", { requestId: "browser-save-2" });
  if (whileHeld.kind !== "running" || whileHeld.state !== null || secondWhileHeld.kind !== "running" || secondWhileHeld.state !== null)
    throw new Error(`Two detached Saves did not remain independently observable while held: ${JSON.stringify({ whileHeld, secondWhileHeld })}`);
  await call("notes.save.release", {});
  const terminal = await call("notes.save.wait", { requestId: "browser-save" });
  const secondTerminal = await call("notes.save.wait", { requestId: "browser-save-2" });
  if (terminal.kind !== "succeeded" || terminal.state !== null || terminal.error !== null
      || secondTerminal.kind !== "succeeded" || secondTerminal.state !== null || secondTerminal.error !== null)
    throw new Error(`Two detached Saves lost document-owned terminal results: ${JSON.stringify({ terminal, secondTerminal })}`);

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
  console.log("SDK_WINDOW_BRIDGE_BROWSER_OK|fixed-dispatch|bootstrap|fresh-document-reload|dynamic-dialog-after-load|retired-descriptor-disconnected|revisioned-handoff|reload-latest-map|safe-endpoint-keys|one-native-bind|typed-snapshot|direct-set|checked-conflict|explicit-rebase|duplicate-receipt|two-accepted-saves|editor-detach|document-owned-terminal-waits|terminal-state-null|scope-drained");
