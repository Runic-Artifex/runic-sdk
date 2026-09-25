import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function retry(action, milliseconds = 15_000) {
  const deadline = Date.now() + milliseconds;
  while (Date.now() < deadline) {
    try { const value = await action(); if (value) return value; } catch { /* Fixture/browser startup. */ }
    await delay(40);
  }
  throw new Error("Timed out waiting for the Window Bridge batch browser fixture.");
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
  profile = await mkdtemp(join(tmpdir(), "runic-sdk-window-bridge-batch-"));
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
  const command = async (method, params = {}) => {
    const id = ++nextId;
    const result = new Promise(resolve => pending.set(id, resolve));
    socket.send(JSON.stringify({ id, method, params }));
    return result;
  };
  const evaluate = async expression => {
    const response = await command("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
    if (response.error || response.result?.exceptionDetails) throw new Error(JSON.stringify(response));
    return response.result.result.value;
  };
  await retry(() => evaluate("globalThis.webui?.isConnected() === true"));

  async function callWith(route, payload) {
    const expression = `(async () => { const endpoint = globalThis.runicCsWebUi.endpoints[${JSON.stringify(route)}]; if (!endpoint) throw new Error("No endpoint for " + ${JSON.stringify(route)}); return JSON.parse(await globalThis.webui.call("__runicBridgeDispatch", globalThis.runicCsWebUi.credential, JSON.stringify({ v: 1, endpoint: endpoint.endpoint, generation: endpoint.generation, payload: ${JSON.stringify(payload)} }))); })()`;
    return evaluate(expression);
  }
  let documentEpoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
  const call = (route, payload = {}) => callWith(route, { ...payload, documentEpoch });
  const callDescriptor = (descriptor, payload = {}) => evaluate(`(async () => JSON.parse(await globalThis.webui.call("__runicBridgeDispatch", globalThis.runicCsWebUi.credential, JSON.stringify({ v: 1, endpoint: ${JSON.stringify(descriptor.endpoint)}, generation: ${JSON.stringify(descriptor.generation)}, payload: ${JSON.stringify({ ...payload, documentEpoch })} }))))()`);
  async function beginDocument() {
    const reply = await call("__runicBridgeDocumentBegin");
    if (reply.ok) await evaluate(`globalThis.__runicBridgeEndpointHandoff(${JSON.stringify({ v: 1, ...reply.manifest })})`);
    return reply;
  }

  const began = await beginDocument();
  if (!began.ok) throw new Error(`The browser document epoch was not admitted: ${JSON.stringify(began)}`);
  const mounted = await call("notes.editor.mount", { presentationId: "batch-probe-editor" });
  if (!mounted.ok) throw new Error(`The batch probe did not mount its editor: ${JSON.stringify(mounted)}`);

  const initial = await evaluate(`(() => ({ revision: globalThis.runicCsWebUi.endpointRevision, count: Object.keys(globalThis.runicCsWebUi.endpoints).length }))()`);
  await evaluate(`(() => {
    const original = globalThis.__runicBridgeEndpointHandoff;
    const events = [];
    globalThis.__runicBridgeBatchAudit = events;
    globalThis.__runicBridgeEndpointHandoff = handoff => {
      events.push({ revision: handoff?.revision, count: Object.keys(handoff?.endpoints ?? {}).length });
      return original(handoff);
    };
  })()`);

  const attachStarted = performance.now();
  const opened = await call("notes.debug.batch.open");
  const attachElapsedMs = performance.now() - attachStarted;
  if (!opened.ok || opened.existing || opened.routeCount !== 500 || !opened.reference?.Id)
    throw new Error(`The 500-route attachment was not created: ${JSON.stringify(opened)}`);
  const prefix = `content${opened.reference.Id}.route.`;
  const attached = await retry(async () => {
    const map = await evaluate(`(() => ({ revision: globalThis.runicCsWebUi.endpointRevision, count: Object.keys(globalThis.runicCsWebUi.endpoints).length, batchCount: Object.keys(globalThis.runicCsWebUi.endpoints).filter(key => key.startsWith(${JSON.stringify(prefix)})).length, audit: globalThis.__runicBridgeBatchAudit }))()`);
    return map.batchCount === 500 ? map : null;
  });
  if (attached.revision !== initial.revision + 1 || attached.count !== initial.count + 500 || attached.audit.length !== 1
      || attached.audit[0].revision !== attached.revision || attached.audit[0].count !== attached.count)
    throw new Error(`The attachment was not one complete browser handoff: ${JSON.stringify({ initial, attached })}`);

  const sampleRoute = `${prefix}250`;
  const sampleDescriptor = await evaluate(`globalThis.runicCsWebUi.endpoints[${JSON.stringify(sampleRoute)}]`);
  const live = await call(sampleRoute);
  if (!live.ok || live.kind !== "batch" || live.index !== 250)
    throw new Error(`A descriptor from the committed batch did not dispatch: ${JSON.stringify(live)}`);
  const nativeWhileAttached = await call("notes.debug.native-bindings");
  if (nativeWhileAttached.registrations !== 1)
    throw new Error(`The batch added a native binding: ${JSON.stringify(nativeWhileAttached)}`);

  const retirementStarted = performance.now();
  const closed = await call("notes.debug.batch.close");
  const retirementElapsedMs = performance.now() - retirementStarted;
  if (!closed.ok) throw new Error(`The 500-route attachment was not retired: ${JSON.stringify(closed)}`);
  const retired = await retry(async () => {
    const map = await evaluate(`(() => ({ revision: globalThis.runicCsWebUi.endpointRevision, count: Object.keys(globalThis.runicCsWebUi.endpoints).length, batchCount: Object.keys(globalThis.runicCsWebUi.endpoints).filter(key => key.startsWith(${JSON.stringify(prefix)})).length, audit: globalThis.__runicBridgeBatchAudit }))()`);
    return map.batchCount === 0 ? map : null;
  });
  if (retired.revision !== attached.revision + 1 || retired.count !== initial.count || retired.audit.length !== 2
      || retired.audit[1].revision !== retired.revision || retired.audit[1].count !== retired.count)
    throw new Error(`The retirement was not one authoritative browser handoff: ${JSON.stringify({ initial, attached, retired })}`);
  const stale = await callDescriptor(sampleDescriptor);
  if (stale.error?.kind !== "disconnected")
    throw new Error(`A stale batch descriptor reached a retired route: ${JSON.stringify(stale)}`);
  const nativeAfterRetirement = await call("notes.debug.native-bindings");
  if (nativeAfterRetirement.registrations !== 1)
    throw new Error(`Retiring the batch changed native bindings: ${JSON.stringify(nativeAfterRetirement)}`);

  browserSucceeded = true;
  console.log(`SDK_WINDOW_BRIDGE_BATCH_BROWSER_OK|routes=500|attach-handoffs=1|retire-handoffs=1|initial-revision=${initial.revision}|attach-revision=${attached.revision}|retire-revision=${retired.revision}|attach-ms=${attachElapsedMs.toFixed(1)}|retire-ms=${retirementElapsedMs.toFixed(1)}|stale-rejected|one-native-bind`);
} finally {
  socket?.close();
  chrome?.kill("SIGTERM");
  host.stdin.end("\n");
  if (browserSucceeded) await retry(() => output.includes("WINDOW_BRIDGE_BROWSER_STOPPED"), 5_000);
  await delay(100);
  if (host.exitCode === null) host.kill("SIGTERM");
  if (profile) await rm(profile, { recursive: true, force: true });
}
