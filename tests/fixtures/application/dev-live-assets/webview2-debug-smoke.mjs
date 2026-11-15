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
// WebView2 ignores its WEBVIEW2_* variables and HKCU overrides in an elevated process (Runtime 150 and later; "Develop
// secure WebView2 apps"), and GitHub's Windows runners run every step elevated. An elevated script therefore starts both
// runs as the standard user RUNIC_TEST_UNELEVATED_USER (HostedWindows.Smoke --run-unelevated), as a developer's IDE
// starts them.
// Run from the SDK root after Release builds of tools/dotnet-runic and, when elevated, HostedWindows.Smoke:
//   node tests/fixtures/application/dev-live-assets/webview2-debug-smoke.mjs
import { spawn, spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { copyFile, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { killTree, scaled, tail, waitFor, waitForExit, within } from "../../../../examples/shared/smoke.mjs";
import { attachSelects, cdpEndpoint, parseTemplateJson, variable, webView2Debugging } from "./webview2-debugging.mjs";

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
// The fixture writes what it received and its Desktop diagnostics to standard error (Program.cs).
environment.DEV_LIVE_ASSETS_TRACE = "1";

// Failure detail: the processes below `root` (a process ID, or every process of that file name with what runs below
// it) with their command lines, the ports they listen on, each WebView2 browser process's user data folder and
// DevToolsActivePort, and WebView2 browser processes outside the tree.
async function describeTree(root) {
  const script = "Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CommandLine | ConvertTo-Json -Compress; " +
    "'---'; Get-NetTCPConnection -State Listen | Select-Object LocalAddress,LocalPort,OwningProcess | ConvertTo-Json -Compress";
  const result = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script],
    { encoding: "utf8", timeout: 60_000, maxBuffer: 64 << 20 });
  try {
    const [processText, listenText] = result.stdout.split(/^---\r?$/m);
    const processes = [JSON.parse(processText)].flat(), listening = [JSON.parse(listenText?.trim() || "[]")].flat();
    const children = new Map();
    for (const entry of processes) children.set(entry.ParentProcessId, [...children.get(entry.ParentProcessId) ?? [], entry]);
    const named = typeof root === "string" ? processes.filter(entry => entry.Name?.toLowerCase() === root.toLowerCase()) : [];
    const tree = [...named], queue = typeof root === "number" ? [...children.get(root) ?? []] : named.flatMap(entry => children.get(entry.ProcessId) ?? []);
    while (queue.length) { const entry = queue.shift(); tree.push(entry); queue.push(...children.get(entry.ProcessId) ?? []); }
    const pids = new Set([...(typeof root === "number" ? [root] : []), ...tree.map(entry => entry.ProcessId)]);
    const browserProcesses = processes.filter(entry => /^msedgewebview2\.exe$/i.test(entry.Name) && !/\s--type=/.test(entry.CommandLine ?? ""));
    const folders = [];
    for (const browser of browserProcesses.filter(entry => pids.has(entry.ProcessId))) {
      const folder = /--user-data-dir=(?:"([^"]+)"|(\S+))/.exec(browser.CommandLine ?? "");
      const path = folder?.[1] ?? folder?.[2];
      const active = path ? await readFile(join(path, "DevToolsActivePort"), "utf8").catch(error => `(none: ${error.code})`) : "(no --user-data-dir)";
      folders.push(`  browser ${browser.ProcessId}: user data ${path}; DevToolsActivePort: ${JSON.stringify(active)}`);
    }
    return [`process tree of ${root}:`,
      ...tree.filter(entry => !/\s--type=(renderer|gpu-process|utility|crashpad-handler)/.test(entry.CommandLine ?? ""))
        .map(entry => `  ${entry.ProcessId} (parent ${entry.ParentProcessId}) ${entry.Name}: ${(entry.CommandLine ?? "").slice(0, 1200)}`),
      `listening in the tree: ${JSON.stringify(listening.filter(entry => pids.has(entry.OwningProcess)))}`,
      `listening on ${port}: ${JSON.stringify(listening.filter(entry => entry.LocalPort === port))}`,
      ...folders,
      `WebView2 browser processes outside the tree: ${JSON.stringify(browserProcesses.filter(entry => !pids.has(entry.ProcessId))
        .map(entry => `${entry.ProcessId}: ${(entry.CommandLine ?? "").slice(0, 600)}`))}`].join("\n");
  } catch (error) {
    return `process snapshot failed: ${error.message}\n${tail(result.stdout ?? "", 2000)}\n${tail(result.stderr ?? "", 2000)}`;
  }
}

// Elevated (net session succeeds only then): WebView2 would ignore the profile's variable, so the Window runs as the
// standard user that the CI step creates, which may write the fixture's obj/ and Frontend/dist (dev --no-build builds
// nothing). Its processes do not run below this one (CreateProcessWithLogonW).
const elevated = spawnSync("net.exe", ["session"], { stdio: "ignore" }).status === 0;
const standardUser = process.env.RUNIC_TEST_UNELEVATED_USER;
const unelevated = join(root, "tests/native/Runic.Application.HostedWindows.Smoke/bin/Release/net10.0/Runic.Application.HostedWindows.Smoke.dll");
if (elevated) {
  if (!standardUser || !process.env.RUNIC_TEST_UNELEVATED_PASSWORD || !existsSync(unelevated))
    throw new Error("This process is elevated, and WebView2 ignores WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS in an elevated Window. " +
      "Run the check unelevated, or set RUNIC_TEST_UNELEVATED_USER and RUNIC_TEST_UNELEVATED_PASSWORD to a standard user and " +
      "build tests/native/Runic.Application.HostedWindows.Smoke in Release.");
  const granted = spawnSync("icacls.exe", [fixture, "/grant", `${standardUser}:(OI)(CI)M`, "/T", "/C", "/Q"], { encoding: "utf8" });
  if (granted.status !== 0) throw new Error(`icacls ${fixture} failed: ${granted.stdout}${granted.stderr}`);
  // dev's frontend watcher runs Bun, which setup-bun installs in the runner user's profile, where the standard user may
  // not read: a copy in the fixture, first on the standard user's PATH.
  const bun = spawnSync("where.exe", ["bun"], { encoding: "utf8" }).stdout?.split(/\r?\n/).find(line => line.trim());
  if (!bun) throw new Error("bun is not on PATH; dotnet runic dev's frontend watcher needs it.");
  const tools = join(fixture, "obj", "standard-user-tools");
  await mkdir(tools, { recursive: true });
  await copyFile(bun.trim(), join(tools, "bun.exe"));
  const pathKey = Object.keys(environment).find(name => name.toUpperCase() === "PATH") ?? "PATH";
  environment[pathKey] = `${tools};${environment[pathKey] ?? ""}`;
  // What run 2 needs as the standard user: Bun, and writing obj/runic (dev's session file and pages) and Frontend/dist.
  await mkdir(join(fixture, "obj/runic"), { recursive: true });
  for (const [label, command] of [
    ["bun --version", ["bun", "--version"]],
    ["writing obj/runic and Frontend", ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
      `Set-Content -LiteralPath '${join(fixture, "obj/runic/standard-user-probe")}' -Value ok; ` +
      `Remove-Item -LiteralPath '${join(fixture, "obj/runic/standard-user-probe")}'; ` +
      `Set-Content -LiteralPath '${join(fixture, "Frontend/standard-user-probe")}' -Value ok; ` +
      `Remove-Item -LiteralPath '${join(fixture, "Frontend/standard-user-probe")}'`]],
  ]) {
    const result = spawnSync("dotnet", [unelevated, "--run-unelevated", ...command], { cwd: root, env: environment, encoding: "utf8", timeout: 120_000 });
    if (result.status !== 0)
      throw new Error(`The standard user ${standardUser} cannot run ${label}, which dotnet runic dev needs (exit ${result.status}):\n` +
        tail(`${result.stdout ?? ""}${result.stderr ?? ""}`, 2000));
  }
}
// Stops what a run started, including the standard user's processes, which do not run below this one: the process trees
// of the fixture's Window and of every process whose command line names the fixture's project (dotnet run, dotnet runic
// dev and the launcher), then whatever else still runs as the standard user (only this step runs as that user). Returns
// what survived.
function stopFixtureProcesses() {
  const user = standardUser.replaceAll("'", "''");
  const result = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
    "$ErrorActionPreference = 'SilentlyContinue'; " +
    "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'DevLiveAssets.exe' -or $_.CommandLine -like '*DevLiveAssets.csproj*' } | " +
    "ForEach-Object { taskkill.exe /PID $_.ProcessId /T /F | Out-Null }; " +
    `$mine = { Get-Process -IncludeUserName | Where-Object { $_.UserName -like '*\\${user}' } }; ` +
    "& $mine | Stop-Process -Force; " +
    "foreach ($attempt in 1..20) { if (-not (& $mine)) { break }; Start-Sleep -Milliseconds 250 }; " +
    "(& $mine | ForEach-Object { \"$($_.Id) $($_.ProcessName)\" }) -join ', '"], { encoding: "utf8", timeout: 60_000 });
  const survivors = (result.stdout ?? "").trim();
  if (survivors) console.log(`Still running as ${standardUser} after the teardown: ${survivors}`);
  return survivors;
}

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
  const command = elevated ? [unelevated, "--run-unelevated", "dotnet", ...args] : args;
  const child = spawn("dotnet", command, { cwd: root, env: environment, stdio: ["ignore", "pipe", "pipe"] });
  const started = { label, child, output: "" };
  child.stdout.on("data", chunk => { started.output += chunk; });
  child.stderr.on("data", chunk => { started.output += chunk; });
  return started;
}
const detailOf = started => () => `${started.label} ${started.child.exitCode === null ? "is running" : `exited ${started.child.exitCode}`}:\n` +
  tail(started.output);
async function stop(started) {
  if (!started) return;
  if (elevated) stopFixtureProcesses();
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
    { label: `${base}json/version`, timeout: 30_000, attemptTimeout: 5_000, detail });
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
    label: `the Window's page in ${base}json/list`, timeout: 30_000, attemptTimeout: 15_000,
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
  const page = await proveEndpoint(endpoint, launched).catch(async error => {
    console.log(`${await describeTree("DevLiveAssets.exe")}\ndotnet run output:\n${tail(launched.output, 4000)}`);
    throw error;
  });
  console.log(`WEBVIEW2_DEBUG_PROFILE_OK|${profileName}|${page.url}`);
  await stop(launched);
  await waitFor(async () => !await answers(endpoint), {
    label: `${endpoint} to close after the Window stopped`, timeout: 30_000, detail: () => describeTree("DevLiveAssets.exe"),
  });

  // 2. dotnet runic dev with the profile's variable: the session file names the endpoint.
  dev = start("dotnet runic dev", [tool, "dev", "--project", project, "-c", "Release", "--no-build", "--no-restore", "--no-dotnet-watch",
    "-e", `${variable}=${debugging.environment[variable]}`]);
  const sessionFile = join(fixture, "obj/runic/dev-session.json");
  const devDetail = async () => `${await detailOf(dev)()}\n${await describeTree("DevLiveAssets.exe")}\n` +
    `dev-session.json: ${await readFile(sessionFile, "utf8").catch(error => `(unreadable: ${error.code})`)}`;
  await waitFor(() => {
    if (dev.child.exitCode !== null) throw new Error(`dotnet runic dev exited ${dev.child.exitCode}`);
    return /^\[dev\] Ready\r?$/m.test(dev.output);
  }, { label: "[dev] Ready from dotnet runic dev as the standard user", timeout: 60_000, detail: devDetail });
  const session = JSON.parse(await readFile(sessionFile, "utf8"));
  const expected = [{ engine: "webview2", protocol: "cdp", url: endpoint }];
  if (session.state !== "ready" || JSON.stringify(session.debugEndpoints) !== JSON.stringify(expected))
    throw new Error(`dev-session.json: state ${session.state}, debugEndpoints ${JSON.stringify(session.debugEndpoints)}; expected ${JSON.stringify(expected)}`);
  const devPage = await proveEndpoint(session.debugEndpoints[0].url, dev).catch(async error => {
    console.log(await devDetail());
    throw error;
  });
  console.log(`WEBVIEW2_DEBUG_DEV_SESSION_OK|${session.debugEndpoints[0].url}|${devPage.url}`);
  console.log("WEBVIEW2_DEBUG_OK|launch-profile|json-version|json-list|page-target|runtime-evaluate|debugger|own-arguments|dev-session");
} finally {
  await stop(launched);
  await stop(dev);
  await rm(createdProperties ? properties : profileFile, { recursive: true, force: true });
}
