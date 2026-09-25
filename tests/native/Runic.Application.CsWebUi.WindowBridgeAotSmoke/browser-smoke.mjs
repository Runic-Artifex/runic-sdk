import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

const executable = process.env.RUNIC_WINDOW_BRIDGE_AOT_HOST;
if (!executable) throw new Error("Set RUNIC_WINDOW_BRIDGE_AOT_HOST to the published native executable.");
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function retry(action, milliseconds = 15_000) {
  const deadline = Date.now() + milliseconds;
  while (Date.now() < deadline) {
    try { const value = await action(); if (value) return value; } catch { /* Startup or native callback in flight. */ }
    await delay(40);
  }
  throw new Error("Timed out waiting for the NativeAOT Window Bridge fixture.");
}

const host = spawn(executable, ["--window-bridge-aot-host"], { stdio: ["pipe", "pipe", "pipe"] });
let output = "";
let errors = "";
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { errors += chunk.toString(); });
let chrome, socket, profile;
let succeeded = false;
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Native fixture exited: ${errors}\n${output}`);
    return output.match(/WINDOW_BRIDGE_AOT_URL=(https?:\/\/\S+)/)?.[1];
  });
  profile = await mkdtemp(join(tmpdir(), "runic-sdk-aot-window-bridge-"));
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
    const complete = pending.get(message.id);
    if (complete) { pending.delete(message.id); complete(message); }
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
  const documentEpoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
  const call = (route, payload = {}) => evaluate(`(async () => {
    const descriptor = globalThis.runicCsWebUi.endpoints[${JSON.stringify(route)}];
    if (!descriptor) throw new Error("Missing endpoint: " + ${JSON.stringify(route)});
    return JSON.parse(await globalThis.webui.call("__runicBridgeDispatch", globalThis.runicCsWebUi.credential,
      JSON.stringify({ v: 1, endpoint: descriptor.endpoint, generation: descriptor.generation,
        payload: ${JSON.stringify({ ...payload, documentEpoch })} })));
  })()`);
  const callDescriptor = (descriptor, payload) => evaluate(`(async () => JSON.parse(await globalThis.webui.call(
    "__runicBridgeDispatch", globalThis.runicCsWebUi.credential,
    JSON.stringify({ v: 1, endpoint: ${JSON.stringify(descriptor.endpoint)}, generation: ${JSON.stringify(descriptor.generation)},
      payload: ${JSON.stringify(payload)} }))))()`);

  const began = await call("__runicBridgeDocumentBegin");
  if (!began.ok) throw new Error(`Document admission failed: ${JSON.stringify(began)}`);
  await evaluate(`globalThis.__runicBridgeEndpointHandoff(${JSON.stringify({ v: 1, ...began.manifest })})`);
  const fixture = await call("aot.discover");
  if (!fixture.ok || !fixture.route) throw new Error(`Fixture route missing: ${JSON.stringify(fixture)}`);
  const read = `${fixture.route}.read`;
  const write = `${fixture.route}.write`;
  const save = `${fixture.route}.save`;
  const cachedRead = await evaluate(`globalThis.runicCsWebUi.endpoints[${JSON.stringify(read)}]`);
  if (!cachedRead?.endpoint) throw new Error("Missing cached read descriptor.");
  if (!(await call("aot.mount", { presentationId: "a" })).ok
      || !(await call("aot.mount", { presentationId: "b" })).ok)
    throw new Error("Two presentations failed to mount.");
  const initialA = await call(read, { presentationId: "a" });
  const initialB = await call(read, { presentationId: "b" });
  if (initialA.title !== "Draft" || initialB.title !== "Draft")
    throw new Error(`Initial reads failed: ${JSON.stringify({ initialA, initialB })}`);
  const escaped = "Line \"one\"\\two\nnext";
  const written = await call(write, { presentationId: "b", title: escaped });
  if (!written.ok || written.title !== escaped) throw new Error(`Escaped write failed: ${JSON.stringify(written)}`);
  if (!(await call("aot.unmount", { presentationId: "a" })).ok) throw new Error("Unmount a failed.");
  const staleRead = await call(read, { presentationId: "a" });
  const staleWrite = await call(write, { presentationId: "a", title: "Must not apply" });
  const staleSave = await call(save, { presentationId: "a" });
  if (staleRead.ok !== false || staleWrite.ok !== false || staleSave.ok !== false)
    throw new Error(`Stale presentation was admitted: ${JSON.stringify({ staleRead, staleWrite, staleSave })}`);
  if (!(await call(save, { presentationId: "b" })).ok) throw new Error("Mounted save failed.");
  const model = await call("aot.model");
  if (!model.ok || model.title !== escaped || model.saves !== 1)
    throw new Error(`Model state was incorrect: ${JSON.stringify(model)}`);
  if (!(await call("aot.suspend")).ok) throw new Error("Suspend failed.");
  await retry(async () => (await evaluate(`globalThis.runicCsWebUi.endpoints[${JSON.stringify(read)}] === undefined`)) || null);
  const staleDescriptor = await callDescriptor(cachedRead, { documentEpoch, presentationId: "b" });
  if (staleDescriptor.error?.kind !== "disconnected")
    throw new Error(`Retired descriptor was admitted: ${JSON.stringify(staleDescriptor)}`);
  succeeded = true;
} finally {
  socket?.close();
  chrome?.kill("SIGTERM");
  host.stdin.end("\n");
  if (succeeded) await retry(() => output.includes("WINDOW_BRIDGE_AOT_STOPPED"), 5_000);
  await delay(100);
  if (host.exitCode === null) host.kill("SIGTERM");
  if (profile) await rm(profile, { recursive: true, force: true });
}

if (succeeded)
  console.log("WINDOW_BRIDGE_AOT_BROWSER_OK|native-host|document-admission|two-presentations|escaped-title|stale-rejected|save|retired-endpoint|scope-drained");
