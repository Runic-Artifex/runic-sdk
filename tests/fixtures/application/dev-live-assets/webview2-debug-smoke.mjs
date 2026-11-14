// D066.25 3: the runic-app template's WebView2 script-debugging launch profile, against a real Runic Desktop Window in
// WebView2. On every OS it first checks that the template's profile and VS Code configurations agree
// (webview2-debugging.mjs); off Windows it then skips. On Windows:
//   1. `dotnet run --launch-profile` with the template's profile (its WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS), while the
//      application gives the Window browser arguments of its own (DesktopWindowOptions.BrowserArguments,
//      --js-flags=--expose-gc): /json/version and /json/list answer on the profile's port, the page target that
//      js-debug's attach selects is the Window's page, Runtime.evaluate and Debugger.enable work in it over a flattened
//      Target.attachToTarget session as js-debug's are, and the application's own argument is still in effect.
//   2. `dotnet runic dev -e WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=…`: the session file lists the endpoint under
//      debugEndpoints (#959), and the endpoint is the Window's page.
// Run from the SDK root after a Release build of tools/dotnet-runic:
//   node tests/fixtures/application/dev-live-assets/webview2-debug-smoke.mjs
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { killTree, scaled, tail, waitFor, waitForExit, within } from "../../../../examples/shared/smoke.mjs";
import { attachSelects, parseTemplateJson, variable, webView2Debugging } from "./webview2-debugging.mjs";

const fixture = fileURLToPath(new URL(".", import.meta.url));
const root = join(fixture, "../../../..");
const template = join(root, "tools/Runic.Application.Templates/content/runic-app");
const launchSettings = await readFile(join(template, "Properties/launchSettings.json"), "utf8");
const debugging = webView2Debugging(parseTemplateJson(launchSettings),
  parseTemplateJson(await readFile(join(template, ".vscode/launch.json"), "utf8")));
const { endpoint, port, attach } = debugging;
console.log(`WEBVIEW2_DEBUG_TEMPLATE_OK|${debugging.profileName}|${debugging.windowConfiguration}|${attach.name}|${endpoint}`);
if (process.platform !== "win32") {
  console.log("WEBVIEW2_DEBUG_SKIPPED|WebView2 runs on Windows only");
  process.exit(0);
}

const project = join(fixture, "DevLiveAssets.csproj");
const tool = process.env.RUNIC_DEV_TOOL_DLL ?? join(root, "tools/dotnet-runic/bin/Release/net10.0/dotnet-runic.dll");
if (!existsSync(tool)) throw new Error(`Build tools/dotnet-runic in Release first; ${tool} does not exist.`);
// The template's profiles, named for this project; dotnet run reads them from Properties/launchSettings.json.
const properties = join(fixture, "Properties");
const profileName = debugging.profileName.replace("RunicWindowApp", "DevLiveAssets");
// The application's own browser argument (Program.cs): V8's gc() exists in the page only while WebView2 applies it.
const ownArguments = "--js-flags=--expose-gc";
const environment = { ...process.env, DEV_LIVE_ASSETS_BROWSER_ARGUMENTS: ownArguments };
// Only the profile, or dev's -e, sets WebView2's variable.
for (const name of Object.keys(environment)) if (name.toUpperCase() === variable) delete environment[name];

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: root, env: environment, stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    child.stdout.on("data", chunk => { output += chunk; });
    child.stderr.on("data", chunk => { output += chunk; });
    child.on("error", reject);
    child.on("close", code => code === 0 ? resolve(output)
      : reject(new Error(`${command} ${args.join(" ")} exited ${code}:\n${tail(output)}`)));
  });
}

// A started process with its output retained for failure detail.
function start(label, args) {
  const child = spawn("dotnet", args, { cwd: root, env: environment, stdio: ["ignore", "pipe", "pipe"] });
  const started = { label, child, output: "" };
  child.stdout.on("data", chunk => { started.output += chunk; });
  child.stderr.on("data", chunk => { started.output += chunk; });
  return started;
}
const detailOf = started => () => `${started.label} ${started.child.exitCode === null ? "is running" : `exited ${started.child.exitCode}`}:\n` +
  tail(started.output);
async function stop(started) {
  if (!started) return;
  killTree(started.child);
  await waitForExit(started.child, scaled(10_000));
}

async function getJson(base, path, signal) {
  const response = await fetch(new URL(path, base), { signal });
  if (!response.ok) throw new Error(`${new URL(path, base)} answered ${response.status}`);
  return response.json();
}
const answers = base => getJson(base, "json/version", AbortSignal.timeout(2_000)).then(() => true, () => false);

// A DevTools Protocol connection; `send` takes a flattened session ID as js-debug sends it.
async function devtools(url) {
  const socket = new WebSocket(url);
  await within(new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", () => reject(new Error(`DevTools WebSocket ${url} failed to open`)), { once: true });
  }), scaled(10_000), "DevTools WebSocket");
  let nextId = 0;
  const pending = new Map(), events = [];
  socket.addEventListener("message", ({ data }) => {
    const message = JSON.parse(data);
    if (message.id === undefined) events.push(message);
    else { pending.get(message.id)?.(message); pending.delete(message.id); }
  });
  socket.addEventListener("close", () => {
    for (const resolve of pending.values()) resolve({ error: { message: "DevTools connection closed" } });
    pending.clear();
  });
  return {
    events,
    async send(method, params = {}, sessionId) {
      const id = ++nextId;
      const reply = new Promise(resolve => pending.set(id, resolve));
      socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
      const message = await within(reply, scaled(10_000), `DevTools ${method}`);
      if (message.error) throw new Error(`${method}: ${JSON.stringify(message.error)}`);
      return message.result;
    },
    async evaluate(expression, sessionId) {
      const { result, exceptionDetails } = await this.send("Runtime.evaluate",
        { expression, returnByValue: true, awaitPromise: true }, sessionId);
      if (exceptionDetails) throw new Error(`Runtime.evaluate(${expression}) threw: ${JSON.stringify(exceptionDetails)}`);
      return result.value;
    },
    close() { socket.close(); },
  };
}

// What the Window's page reports about itself: the fixture's marker, WebView2's host object (not a browser fallback)
// and whether the application's --js-flags=--expose-gc is in effect.
const pageState = `({ href: location.href, marker: document.querySelector("#marker")?.textContent ?? null,
  webView2: typeof globalThis.chrome?.webview?.postMessage === "function", gc: typeof globalThis.gc })`;

// Proves `base` is the Window's WebView2 DevTools Protocol endpoint, as js-debug's attach uses it.
async function proveEndpoint(base, started) {
  const detail = detailOf(started);
  const version = await waitFor(signal => getJson(base, "json/version", signal),
    { label: `${base}json/version`, timeout: 60_000, attemptTimeout: 5_000, detail });
  if (!version.webSocketDebuggerUrl) throw new Error(`${base}json/version names no browser endpoint: ${JSON.stringify(version)}`);
  // The attach configuration leaves js-debug's default address, localhost; its HTTP client retries the other loopback
  // family, as Node's fetch does.
  await getJson(`http://localhost:${port}/`, "json/version", AbortSignal.timeout(scaled(5_000))).catch(error => {
    throw new Error(`http://localhost:${port}/json/version, js-debug's default address, did not answer`, { cause: error });
  });

  // The page target js-debug would select, once the Window shows the fixture's page.
  let targets;
  const target = await waitFor(async signal => {
    targets = await getJson(base, "json/list", signal);
    const selected = targets.filter(entry => attachSelects(attach, entry));
    if (selected.length > 1) throw new Error(`js-debug's attach would choose among ${selected.length} page targets`);
    if (selected.length === 0 || !/^https?:/.test(selected[0].url)) return undefined;
    const page = await devtools(selected[0].webSocketDebuggerUrl);
    try {
      const state = await page.evaluate(pageState);
      // The listed URL is the page's own once the Window shows the fixture's page (not Runic's loading surface).
      return state.marker === "one" && state.href === selected[0].url ? { ...selected[0], state } : undefined;
    } finally { page.close(); }
  }, {
    label: `the Window's page in ${base}json/list`, timeout: 60_000, attemptTimeout: 15_000,
    detail: async () => `targets: ${JSON.stringify(targets?.map(({ type, url }) => ({ type, url })))}\n${await detail()}`,
  });
  const { state } = target;
  if (!state.webView2) throw new Error(`The page has no chrome.webview, so it is not in WebView2: ${JSON.stringify(state)}`);
  if (state.gc !== "function")
    throw new Error(`The application's own browser argument ${ownArguments} is not in effect next to ${variable}: typeof gc is ${state.gc}`);

  // js-debug: the browser connection from /json/version, target discovery, a flattened attach, then the session's
  // Runtime and Debugger domains.
  const browser = await devtools(version.webSocketDebuggerUrl);
  try {
    await browser.send("Target.setDiscoverTargets", { discover: true });
    const discovered = await waitFor(() => browser.events.find(event => event.method === "Target.targetCreated"
      && event.params.targetInfo.targetId === target.id)?.params.targetInfo, { label: "Target.targetCreated for the page", detail });
    if (!attachSelects(attach, discovered)) throw new Error(`js-debug would not select ${JSON.stringify(discovered)}`);
    const { sessionId } = await browser.send("Target.attachToTarget", { targetId: target.id, flatten: true });
    const evaluated = await browser.evaluate(`[document.querySelector("#marker").textContent, typeof gc, 6 * 7]`, sessionId);
    if (JSON.stringify(evaluated) !== JSON.stringify(["one", "function", 42]))
      throw new Error(`Runtime.evaluate over the attached session returned ${JSON.stringify(evaluated)}`);
    const { debuggerId } = await browser.send("Debugger.enable", {}, sessionId);
    if (!debuggerId) throw new Error("Debugger.enable returned no debuggerId");
    await browser.send("Debugger.disable", {}, sessionId);
    await browser.send("Target.detachFromTarget", { sessionId });
  } finally { browser.close(); }
  return target;
}

const profileFile = join(properties, "launchSettings.json");
if (existsSync(profileFile)) throw new Error(`${profileFile} exists; the check writes the template's profiles there.`);
let launched, dev, createdProperties = false;
try {
  if (await answers(endpoint)) throw new Error(`${endpoint} already answers; the check needs the profile's port ${port} free.`);
  await run("dotnet", ["build", project, "-c", "Release"]);

  // 1. The launch profile, as dotnet run, Visual Studio and Rider apply it.
  if (!existsSync(properties)) { await mkdir(properties); createdProperties = true; }
  await writeFile(profileFile,
    JSON.stringify(parseTemplateJson(launchSettings.replaceAll("RunicWindowApp", "DevLiveAssets")), null, 2));
  launched = start("dotnet run --launch-profile", ["run", "--project", project, "-c", "Release", "--no-build", "--launch-profile", profileName]);
  const page = await proveEndpoint(endpoint, launched);
  console.log(`WEBVIEW2_DEBUG_PROFILE_OK|${profileName}|${page.url}`);
  await stop(launched);
  await waitFor(async () => !await answers(endpoint), { label: `${endpoint} to close after the Window stopped`, timeout: 30_000 });

  // 2. dotnet runic dev with the profile's variable: the session file names the endpoint.
  dev = start("dotnet runic dev", [tool, "dev", "--project", project, "-c", "Release", "--no-restore", "--no-dotnet-watch",
    "-e", `${variable}=${debugging.environment[variable]}`]);
  await waitFor(() => {
    if (dev.child.exitCode !== null) throw new Error(`dotnet runic dev exited ${dev.child.exitCode}`);
    return /^\[dev\] Ready\r?$/m.test(dev.output);
  }, { label: "[dev] Ready", timeout: 180_000, detail: detailOf(dev) });
  const session = JSON.parse(await readFile(join(fixture, "obj/runic/dev-session.json"), "utf8"));
  const expected = [{ engine: "webview2", protocol: "cdp", url: endpoint }];
  if (session.state !== "ready" || JSON.stringify(session.debugEndpoints) !== JSON.stringify(expected))
    throw new Error(`dev-session.json: state ${session.state}, debugEndpoints ${JSON.stringify(session.debugEndpoints)}; expected ${JSON.stringify(expected)}`);
  const devPage = await proveEndpoint(session.debugEndpoints[0].url, dev);
  console.log(`WEBVIEW2_DEBUG_DEV_SESSION_OK|${session.debugEndpoints[0].url}|${devPage.url}`);
  console.log("WEBVIEW2_DEBUG_OK|launch-profile|json-version|json-list|page-target|runtime-evaluate|debugger|own-arguments|dev-session");
} finally {
  await stop(launched);
  await stop(dev);
  await rm(createdProperties ? properties : profileFile, { recursive: true, force: true });
}
