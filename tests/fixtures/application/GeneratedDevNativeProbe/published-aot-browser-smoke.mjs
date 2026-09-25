// Fixture-only check of a published NativeAOT executable. No Vite server is
// needed: the native page can connect to CS-WebUI even if its test HMR modules
// are unavailable. This does not exercise dotnet runic dev or managed restart.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const [binaryArgument, readyArgument] = process.argv.slice(2);
assert.ok(binaryArgument && readyArgument,
  "Usage: node published-aot-browser-smoke.mjs <published-executable> <ready-manifest>");
const chromium = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
assert.ok(chromium, "Run inside the locked development shell with Chromium configured.");
const binary = resolve(binaryArgument);
const ready = resolve(readyArgument);
const root = await mkdtemp(join(tmpdir(), "runic-generated-aot-smoke-"));
const startup = join(root, "startup.json");
const hostReady = join(root, "host-ready.txt");
let output = "";
const host = spawn(binary, [], {
  stdio: ["ignore", "pipe", "pipe"],
  env: {
    ...process.env,
    RUNIC_VIEW_BRIDGE_READY_MANIFEST: ready,
    RUNIC_VIEW_BRIDGE_HOST_READY: hostReady,
    RUNIC_PROBE_STARTUP_PATH: startup,
    // The fixture's HTML references Vite scripts. This focused AOT route
    // check does not use them; port 1 is deliberately unavailable.
    RUNIC_APPLICATION_VITE_DEV_SERVER: "http://127.0.0.1:1/",
  },
});
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { output += chunk.toString(); });
let chrome, socket;
try {
  const descriptor = await waitFor(async () => JSON.parse(await readFile(startup, "utf8")), "AOT host startup");
  const readyDocument = JSON.parse(await readFile(ready, "utf8"));
  assert.equal(descriptor.processId, host.pid);
  assert.equal(descriptor.fingerprint, readyDocument.fingerprint);
  assert.equal((await readFile(hostReady, "utf8")).trim(), readyDocument.fingerprint);
  assert.match(descriptor.nativeUrl, /^http:\/\/127\.0\.0\.1:\d+\/$/);

  const profile = join(root, "chromium");
  chrome = spawn(chromium, [
    "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
    "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
    `--user-data-dir=${profile}`, descriptor.nativeUrl,
  ], { stdio: "ignore" });
  const port = await waitFor(async () => Number((await readFile(join(profile, "DevToolsActivePort"), "utf8")).split("\n")[0]), "Chromium debugging port");
  const page = await waitFor(async () => (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json())
    .find(candidate => candidate.type === "page" && candidate.url.startsWith(descriptor.nativeUrl)), "native page");
  socket = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolveOpen, rejectOpen) => {
    socket.addEventListener("open", resolveOpen, { once: true });
    socket.addEventListener("error", rejectOpen, { once: true });
  });
  const evaluate = createEvaluator(socket);
  await waitFor(() => evaluate("globalThis.webui?.isConnected() === true"), "CS-WebUI connection");
  const documentEpoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
  const call = (route, payload = {}) => evaluate(`(async () => {
    const endpoint = globalThis.runicCsWebUi.endpoints[${JSON.stringify(route)}];
    if (!endpoint) throw new Error("Missing route");
    return JSON.parse(await globalThis.webui.call("__runicBridgeDispatch", globalThis.runicCsWebUi.credential,
      JSON.stringify({ v: 1, endpoint: endpoint.endpoint, generation: endpoint.generation,
        payload: ${JSON.stringify({ ...payload, documentEpoch })} })));
  })()`);

  const begun = await call("__runicBridgeDocumentBegin");
  assert.equal(begun.ok, true);
  await evaluate(`globalThis.__runicBridgeEndpointHandoff(${JSON.stringify({ v: 1, ...begun.manifest })})`);
  const fixture = await call("postmvvm.fixture");
  assert.equal(fixture.ok, true);
  assert.equal(fixture.page.kind, "notes-editor");
  assert.equal(fixture.page.fingerprint, readyDocument.fingerprint);
  const presentationId = "aot-browser";
  const mounted = await call("postmvvm.mount", {
    reference: fixture.page.reference, fingerprint: fixture.page.fingerprint, presentationId,
  });
  assert.equal(mounted.ok, true);
  assert.equal(mounted.fingerprint, readyDocument.fingerprint);
  const read = `${mounted.referencePrefix}.title.read`;
  const write = `${mounted.referencePrefix}.title.write`;
  assert.equal((await call(read, { presentationId })).title, "Draft");
  assert.equal((await call(write, { presentationId, title: "AOT changed" })).title, "AOT changed");
  assert.equal((await call(read, { presentationId })).title, "AOT changed");
  console.log("GENERATED_NATIVE_AOT_BROWSER_OK|published-binary|fixture-json|generated-title-read-write");
} catch (error) {
  error.message += `\n--- published host ---\n${output.slice(-4000)}`;
  throw error;
} finally {
  socket?.close();
  await stop(chrome);
  await stop(host);
  await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
}

async function waitFor(action, label) {
  for (let attempt = 0; attempt < 200; attempt++) {
    try { const value = await action(); if (value) return value; } catch { /* Startup race. */ }
    await new Promise(resolveWait => setTimeout(resolveWait, 100));
  }
  throw new Error(`Timed out waiting for ${label}.`);
}

async function stop(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  const exited = new Promise(resolveExit => child.once("exit", resolveExit));
  child.kill("SIGTERM");
  await exited;
}

function createEvaluator(socket) {
  let nextId = 0;
  const pending = new Map();
  socket.addEventListener("message", ({ data }) => {
    const message = JSON.parse(data);
    const complete = pending.get(message.id);
    if (complete) { pending.delete(message.id); complete(message); }
  });
  return async expression => {
    const id = ++nextId;
    const reply = new Promise(resolveReply => pending.set(id, resolveReply));
    socket.send(JSON.stringify({ id, method: "Runtime.evaluate", params: { expression, awaitPromise: true, returnByValue: true } }));
    const response = await reply;
    if (response.error || response.result?.exceptionDetails) throw new Error(JSON.stringify(response));
    return response.result.result.value;
  };
}
