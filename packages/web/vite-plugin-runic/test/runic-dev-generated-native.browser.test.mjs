import assert from "node:assert/strict";
import { spawn, execFile as execFileCallback } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import test from "node:test";

const execFile = promisify(execFileCallback);
const repository = resolve(fileURLToPath(new URL("../../../../", import.meta.url)));
const fixture = join(repository, "tests", "fixtures", "application", "GeneratedDevNativeProbe");
const tool = join(repository, "tools", "dotnet-runic", "Runic.Application.Tool.csproj");
const vite = join(repository, "packages", "web", "vite-plugin-runic", "node_modules", "vite", "bin", "vite.js");
const plugin = join(repository, "packages", "web", "vite-plugin-runic", "dist", "index.js");

test("generated native fixture replaces one CS-WebUI document after its compiled contract changes", {
  timeout: 180_000,
  skip: process.platform !== "linux" || !process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH,
}, async () => {
  const root = await mkdtemp(join(tmpdir(), "runic-dev-generated-native-"));
  const app = join(root, "app");
  const project = join(app, "GeneratedDevNativeProbe.csproj");
  const hostReady = join(app, "obj", "runic", "view-bridge-host.fingerprint");
  const startupPath = join(root, "host-startup.json");
  const originPath = join(root, "vite-origin.txt");
  const handoffAckPath = join(root, "handoff-ack.json");
  const hostReadyGate = join(root, "hold-host-ready");
  const hostReadyGateReached = join(root, "host-ready-gate-reached");
  const valueModule = join(app, "Frontend", "src", "value.js");
  let dev, chrome, socket, profile;
  let output = "";
  try {
    await cp(fixture, app, { recursive: true });
    await mkdir(join(app, "obj", "runic"), { recursive: true });
    assert.equal(await exists(vite), true, "The Vite binary must be installed before this browser fixture runs.");
    assert.equal(await exists(plugin), true, "The built fixture Vite plugin must be available.");
    dev = spawn("dotnet", ["run", "--project", tool, "--", "dev", "--project", project], {
      cwd: app,
      detached: true,
      stdio: ["ignore", "pipe", "pipe"],
      env: {
        ...process.env,
        RUNIC_SDK_ROOT: repository,
        RUNIC_PROBE_STARTUP_PATH: startupPath,
        RUNIC_PROBE_HANDOFF_ACK_PATH: handoffAckPath,
        RUNIC_PROBE_HOST_READY_GATE_PATH: hostReadyGate,
        RUNIC_PROBE_HOST_READY_GATE_REACHED_PATH: hostReadyGateReached,
        RUNIC_PROBE_VITE_BIN: vite,
        RUNIC_PROBE_VITE_PLUGIN: plugin,
        RUNIC_PROBE_VITE_ORIGIN_PATH: originPath,
      },
    });
    dev.stdout.on("data", chunk => { output += chunk.toString(); });
    dev.stderr.on("data", chunk => { output += chunk.toString(); });

    const initial = await waitForValue(async () => {
      const startup = await readJsonOptional(startupPath);
      if (!startup) return;
      const ready = startup.readyManifest;
      const fingerprint = await readFingerprint(ready);
      const acknowledged = await readOptional(hostReady);
      const origin = await readOptional(originPath);
      return fingerprint && acknowledged?.trim() === fingerprint && origin ? { startup, ready, fingerprint, origin: origin.trim() } : undefined;
    }, "the host to acknowledge its compiled fingerprint");
    assert.equal(initial.startup.fingerprint, initial.fingerprint);
    assert.match(initial.startup.nativeUrl, /^http:\/\/127\.0\.0\.1:\d+\/$/);

    profile = await mkdtemp(join(tmpdir(), "runic-dev-generated-native-chrome-"));
    chrome = spawn(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, [
      "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--no-first-run",
      "--no-default-browser-check", "--remote-debugging-port=0", `--user-data-dir=${profile}`, initial.startup.nativeUrl,
    ], { stdio: "ignore" });
    const port = await waitForValue(async () => Number((await readFile(join(profile, "DevToolsActivePort"), "utf8")).split("\n")[0]), "Chromium's debugging port");
    const target = await waitForValue(async () => {
      const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      return pages.find(page => page.type === "page" && page.url.startsWith(initial.startup.nativeUrl));
    }, "the private CS-WebUI document");
    socket = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => {
      socket.addEventListener("open", resolve, { once: true });
      socket.addEventListener("error", reject, { once: true });
    });
    const evaluate = createEvaluator(socket);
    const navigations = [];
    socket.addEventListener("message", ({ data }) => {
      const message = JSON.parse(data);
      if (message.method === "Page.frameNavigated" && !message.params.frame.parentId) navigations.push(message.params.frame.url);
    });
    await sendCommand(socket, "Page.enable");
    await waitFor(() => evaluate("globalThis.__loads === 1 && document.querySelector('#value')?.textContent === 'one'"), "the initial Vite document");
    await waitFor(() => evaluate("globalThis.webui?.isConnected() === true"), "the initial CS-WebUI connection");
    let epoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
    const call = (route, payload = {}) => evaluate(`(async () => {
      const descriptor = globalThis.runicCsWebUi.endpoints[${JSON.stringify(route)}];
      if (!descriptor) throw new Error("No endpoint for ${route}");
      return JSON.parse(await globalThis.webui.call("__runicBridgeDispatch", globalThis.runicCsWebUi.credential,
        JSON.stringify({ v: 1, endpoint: descriptor.endpoint, generation: descriptor.generation,
          payload: ${JSON.stringify({ ...payload, documentEpoch: epoch })} })));
    })()`);
    await bootstrapAndExerciseTitle(call, evaluate, "native-title", initial.fingerprint);
    await writeFile(valueModule, "export const revision = \"two\";\n");
    await waitFor(() => evaluate("globalThis.__hmrUpdates === 1 && document.querySelector('#value')?.textContent === 'two'"), "initial Vite HMR");
    assert.equal(await evaluate("globalThis.__loads"), 1, "The initial frontend edit must not replace its document.");
    await waitFor(() => evaluate('document.readyState === "complete"'), "the first document to settle");
    await new Promise(resolve => setTimeout(resolve, 150));
    navigations.length = 0;

    const innerProject = join(app, "PostMvvmDiscovery", "PostMvvmDiscovery.csproj");
    const source = await readFile(innerProject, "utf8");
    await writeFile(hostReadyGate, "hold\n");
    await writeFile(innerProject, source.replace("    <_RunicPostMvvmDiscovery>true</_RunicPostMvvmDiscovery>",
      "    <DefineConstants>$(DefineConstants);POST_MVVM_NATIVE_RESTART</DefineConstants>\n    <_RunicPostMvvmDiscovery>true</_RunicPostMvvmDiscovery>"));
    const changedFingerprint = await waitForValue(async () => {
      const next = await readFingerprint(initial.startup.readyManifest);
      return next && next !== initial.fingerprint ? next : undefined;
    }, "the changed generated manifest before host acknowledgement");
    assert.notEqual((await readOptional(hostReady))?.trim(), changedFingerprint,
      "The replacement host must not acknowledge its fingerprint while the deterministic fixture gate is held.");
    await waitForValue(async () => (await readOptional(hostReadyGateReached))?.trim() === changedFingerprint ? true : undefined,
      "the replacement host to reach the held host-ready gate");
    await assertNoReplacementWhileGated(evaluate, navigations, initial.startup.nativeUrl);
    await rm(hostReadyGate, { force: true });

    const replacement = await waitForValue(async () => {
      const startup = await readJsonOptional(startupPath);
      if (!startup || startup.processId === initial.startup.processId) return;
      const ready = startup.readyManifest;
      const fingerprint = await readFingerprint(ready);
      return fingerprint && fingerprint !== initial.fingerprint && startup.fingerprint === fingerprint &&
        (await readOptional(hostReady))?.trim() === fingerprint ? { startup, fingerprint } : undefined;
    }, "one replacement host acknowledgement");
    const acknowledgement = await waitForValue(() => readJsonOptional(handoffAckPath), "the replacement handoff acknowledgement");
    assert.equal(acknowledgement.instanceId, replacement.startup.instanceId);
    assert.ok(acknowledgement.attempts >= 1, "The document must acknowledge the verified replacement descriptor.");
    assert.equal(acknowledgement.eventId, 1, "Exactly one private Vite event must acknowledge the replacement descriptor.");
    const replacementUrl = new URL("index.html", replacement.startup.nativeUrl).href;
    await waitFor(() => navigations.includes(replacementUrl), "one main-frame replacement navigation");
    assert.deepEqual(navigations, [replacementUrl], "The managed restart must replace exactly one native document.");
    await waitFor(() => evaluate(`location.href === ${JSON.stringify(replacementUrl)} && globalThis.__loads === 1`), "the fresh replacement document");
    epoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
    await waitFor(() => evaluate("globalThis.webui?.isConnected() === true"), "the replacement CS-WebUI connection");
    await bootstrapAndExerciseTitle(call, evaluate, "replacement-title", replacement.fingerprint);
    await writeFile(valueModule, "export const revision = \"three\";\n");
    await waitFor(() => evaluate("globalThis.__hmrUpdates === 1 && document.querySelector('#value')?.textContent === 'three'"), "Vite HMR after replacement");
    assert.equal(await evaluate("globalThis.__loads"), 1, "HMR after replacement must not cause another navigation.");
    await assert.rejects(fetch(initial.startup.nativeUrl), "The old private host URL must retire.");
    await new Promise(resolve => setTimeout(resolve, 400));
    assert.deepEqual(navigations, [replacementUrl], "No delayed native-document replacement may follow the first navigation.");
    assert.equal((output.match(/^\[host\] GENERATED_DEV_NATIVE_HOST_READY [^\r\n]+$/gm) ?? []).length, 2,
      "dotnet watch owns exactly one managed restart.");
    await stopProcessGroup(dev);
    dev = undefined;
    const evaluatedOwnerRoot = await readEvaluatedOwnerRoot(project, initial.startup.readyManifest);
    assert.equal(await exists(evaluatedOwnerRoot), false,
      "The dev session must remove its exact evaluated owner root after host, Vite, and watch shutdown.");
    console.log("DOTNET_RUNIC_GENERATED_NATIVE_OK|compiled-fingerprint-ack|native-vite-hmr|generated-title-read-write|one-managed-restart|host-ready-gated-no-premature-navigation|verified-vite-handoff-event|one-native-document-replacement|fresh-document-identity|replacement-title-read-write|replacement-hmr-reconnected|owner-root-cleaned");
  } catch (error) {
    error.message += `\n--- dotnet runic dev ---\n${output.slice(-6000)}`;
    throw error;
  } finally {
    socket?.close();
    await stopBrowser(chrome);
    await stopProcessGroup(dev);
    if (profile) await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
    await rm(root, { recursive: true, force: true });
  }
});

async function bootstrapAndExerciseTitle(call, evaluate, presentationId, expectedFingerprint) {
  const begun = await call("__runicBridgeDocumentBegin");
  assert.equal(begun.ok, true);
  await evaluate(`globalThis.__runicBridgeEndpointHandoff(${JSON.stringify({ v: 1, ...begun.manifest })})`);
  const fixture = await call("postmvvm.fixture");
  assert.equal(fixture.ok, true);
  assert.equal(fixture.page.fingerprint, expectedFingerprint,
    "The live adapter must match the owner-scoped ready manifest.");
  const mounted = await call("postmvvm.mount", { reference: fixture.page.reference, fingerprint: fixture.page.fingerprint, presentationId });
  assert.equal(mounted.ok, true);
  assert.equal(mounted.fingerprint, expectedFingerprint,
    "The generated route attachment must use the acknowledged adapter.");
  const read = `${mounted.referencePrefix}.title.read`;
  const write = `${mounted.referencePrefix}.title.write`;
  assert.equal((await call(read, { presentationId })).title, "Draft");
  const title = `${presentationId} changed`;
  assert.equal((await call(write, { presentationId, title })).title, title);
  assert.equal((await call(read, { presentationId })).title, title);
}

async function exists(path) { return readFile(path).then(() => true, () => false); }
async function readOptional(path) { try { return await readFile(path, "utf8"); } catch (error) { if (error.code === "ENOENT") return undefined; throw error; } }
async function readJsonOptional(path) { const text = await readOptional(path); return text === undefined ? undefined : JSON.parse(text); }
async function readFingerprint(path) { const text = await readOptional(path); return text === undefined ? undefined : JSON.parse(text).fingerprint; }
async function assertNoReplacementWhileGated(evaluate, navigations, nativeUrl) {
  const oldDocumentUrl = new URL("index.html", nativeUrl).href;
  for (let attempt = 0; attempt < 6; attempt += 1) {
    assert.deepEqual(navigations, [], "Vite must not replace the document while the replacement host is held before its acknowledgement.");
    assert.equal(await evaluate("location.href"), oldDocumentUrl,
      "The old document must remain current while the replacement host is held before its acknowledgement.");
    await new Promise(resolve => setTimeout(resolve, 75));
  }
}
async function readEvaluatedOwnerRoot(project, readyManifest) {
  const owner = resolve(readyManifest).split("/").at(-5);
  assert.match(owner ?? "", /^[a-f0-9]{32}$/,
    "The ready manifest must be under the dev session's owner root.");
  const { stdout } = await execFile("dotnet", [
    "msbuild", project, "-nologo",
    `-p:RunicPostMvvmDiscoveryBuildOwner=${owner}`,
    "-p:RunicPostMvvmDiscoveryOwnerDriver=true",
    "-getProperty:MSBuildProjectFullPath,_RunicPostMvvmDiscoveryOwnerRoot",
  ], { cwd: resolve(project, ".."), env: { ...process.env, RUNIC_SDK_ROOT: repository } });
  // MSBuild can print a project-import diagnostic before its requested JSON
  // properties. The final JSON object remains the authoritative evaluation.
  const json = stdout.slice(stdout.lastIndexOf("\n{") + 1);
  const root = JSON.parse(json).Properties._RunicPostMvvmDiscoveryOwnerRoot;
  assert.equal(typeof root, "string");
  assert.notEqual(root, "");
  return root;
}
async function stopProcessGroup(child) {
  if (!child?.pid) return;
  // A signal code means Node has already reaped this child. Its process group
  // ID may now belong to an unrelated process, so do not signal or wait on it.
  if (child.exitCode !== null || child.signalCode !== null) return;
  const stopped = new Promise(resolve => child.once("exit", resolve));
  // SIGINT gives `dotnet runic dev` its ConsoleCancel path, which first drains
  // host/Vite/watch and then removes the evaluated owner root. SIGTERM is only
  // the bounded fallback for a wedged process group owned by this test.
  try { process.kill(-child.pid, "SIGINT"); } catch { /* Already stopped. */ }
  const stoppedGracefully = await Promise.race([stopped.then(() => true), new Promise(resolve => setTimeout(() => resolve(false), 5_000))]);
  if (!stoppedGracefully) {
    try { process.kill(-child.pid, "SIGTERM"); } catch { /* Already stopped. */ }
    const stoppedAfterTerm = await Promise.race([stopped.then(() => true), new Promise(resolve => setTimeout(() => resolve(false), 5_000))]);
    if (!stoppedAfterTerm) {
      try { process.kill(-child.pid, "SIGKILL"); } catch { /* Already stopped. */ }
      await Promise.race([stopped, new Promise(resolve => setTimeout(resolve, 5_000))]);
    }
  }
}
async function stopBrowser(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  const stopped = new Promise(resolve => child.once("exit", resolve));
  child.kill("SIGTERM");
  const stoppedGracefully = await Promise.race([stopped.then(() => true), new Promise(resolve => setTimeout(() => resolve(false), 5_000))]);
  if (!stoppedGracefully) {
    child.kill("SIGKILL");
    await stopped;
  }
}
async function waitFor(predicate, label) { await waitForValue(async () => (await predicate()) ? true : undefined, label); }
async function waitForValue(action, label) {
  for (let attempt = 0; attempt < 500; attempt += 1) {
    try { const value = await action(); if (value) return value; } catch { /* Expected startup races. */ }
    await new Promise(resolve => setTimeout(resolve, 75));
  }
  assert.fail(`Timed out waiting for ${label}.`);
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
    const reply = new Promise(resolve => pending.set(id, resolve));
    socket.send(JSON.stringify({ id, method: "Runtime.evaluate", params: { expression, awaitPromise: true, returnByValue: true } }));
    const response = await reply;
    if (response.error || response.result?.exceptionDetails) throw new Error(JSON.stringify(response));
    return response.result.result.value;
  };
}
function sendCommand(socket, method, params = {}) {
  const id = Math.floor(Math.random() * 1_000_000_000);
  return new Promise((resolve, reject) => {
    const onMessage = ({ data }) => {
      const message = JSON.parse(data);
      if (message.id !== id) return;
      socket.removeEventListener("message", onMessage);
      message.error ? reject(new Error(JSON.stringify(message.error))) : resolve(message.result);
    };
    socket.addEventListener("message", onMessage);
    socket.send(JSON.stringify({ id, method, params }));
  });
}
