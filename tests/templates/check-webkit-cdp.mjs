// IDE debugging on Linux (#1029, D066.39): a generated React runic-app project under `dotnet runic dev` on Xvfb, and VS
// Code's JavaScript debugger (vscode-js-debug's DAP server, pinned below) attached through dev's CDP adapter on 9222 to the
// WebKitGTK Window (tests/webkit-cdp/dap-scenarios.mjs). Also checks the session file's debugEndpoints entry, the
// `[dev] Debugging:` hint and the endpoint's refusal of other origins and host names.
//
//   node check-webkit-cdp.mjs --project <generated project directory> [--js-debug <unpacked js-debug directory>]
//
// With --browser (#1031), the project's "Window in Chrome" launch configuration instead (it is generated with --editor
// vscode): `dotnet runic dev --browser` without a display, the entry address in its output and session file and its
// refusal of web pages, and js-debug launching Chrome at the configuration's url with the same scenarios on Chromium.
// PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH selects the browser; otherwise js-debug finds the installed Chrome.
//
// RUNIC_DEV_TOOL_DLL=<path to dotnet-runic.dll> runs a locally built dev instead of the project's dotnet-runic tool.
// The project's dotnet-runic must be restored. Needs Xvfb, dbus-run-session and the WebKitGTK of the project's GTK.
import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { createReadStream, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createInterface } from "node:readline";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { scaled } from "../support/timeouts.mjs";

// The js-debug release the check runs (D038: older than a day when pinned), and its archive's SHA-256.
const jsDebugVersion = "1.140.1";
const jsDebugSha256 = "c72fab31bcbfdce6ad1f42adfd28eff8d4681dd3b03b5e2e6f9e72e6f9927faf";

const { values } = parseArgs({ options: { project: { type: "string" }, "js-debug": { type: "string" }, browser: { type: "boolean" } } });
if (!values.project) throw new Error("--project is required");
const project = values.project;
const scenarios = join(dirname(fileURLToPath(import.meta.url)), "../webkit-cdp/dap-scenarios.mjs");
const startTimeout = scaled(900_000);
const tail = text => text.split("\n").slice(-80).join("\n");

async function jsDebug() {
  if (values["js-debug"]) return values["js-debug"];
  const directory = join(process.env.RUNNER_TEMP ?? tmpdir(), `js-debug-dap-${jsDebugVersion}`);
  if (existsSync(join(directory, "js-debug/src/dapDebugServer.js"))) return join(directory, "js-debug");
  const url = `https://github.com/microsoft/vscode-js-debug/releases/download/v${jsDebugVersion}/js-debug-dap-v${jsDebugVersion}.tar.gz`;
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${url}: HTTP ${response.status}`);
  const archive = Buffer.from(await response.arrayBuffer());
  const digest = createHash("sha256").update(archive).digest("hex");
  if (digest !== jsDebugSha256) throw new Error(`js-debug ${jsDebugVersion} has SHA-256 ${digest}, expected ${jsDebugSha256}`);
  mkdirSync(directory, { recursive: true });
  writeFileSync(join(directory, "js-debug.tar.gz"), archive);
  const tar = spawnSync("tar", ["-xzf", "js-debug.tar.gz"], { cwd: directory, encoding: "utf8" });
  if (tar.status !== 0) throw new Error(`tar failed: ${tar.stderr}`);
  return join(directory, "js-debug");
}

// The trace lines (HH:mm:ss.fff …) from `from` to `to`, at most 800, each cut at 400 characters.
async function printTrace(name, from, to) {
  if (!existsSync(trace)) return;
  console.log(`--- adapter trace while "${name}" ran (${from} to ${to}) ---`);
  let printed = 0;
  for await (const line of createInterface({ input: createReadStream(trace) })) {
    const time = line.slice(0, 12);
    if (time < from || time > to || /Console\.messageAdded|Runtime\.consoleAPICalled/.test(line)) continue;
    if (++printed > 800) { console.log("… (more in the trace artifact)"); break; }
    console.log(line.length > 400 ? `${line.slice(0, 400)}…` : line);
  }
  console.log("--- end of trace ---");
}

// The window-restart scenario appends a comment to it: dotnet watch then restarts the Window.
const csharp = join(project, "Program.cs");
if (!existsSync(csharp)) throw new Error(`${project} has no Program.cs`);

const jsDebugDirectory = await jsDebug();
// The adapter's traffic (RUNIC_DEV_CDP_TRACE): in CI under RUNNER_TEMP/webkit-cdp, which the job keeps on failure.
const traceDirectory = process.env.RUNNER_TEMP ? join(process.env.RUNNER_TEMP, "webkit-cdp") : project;
mkdirSync(traceDirectory, { recursive: true });
const trace = join(traceDirectory, `${project.split("/").filter(Boolean).at(-1)}-cdp-trace.log`);
// Its own process group, so stopping it ends dotnet watch, the Window and the development server.
// RUNIC_DEV_TOOL_DLL runs a locally built dotnet-runic instead of the project's tool (tools/dotnet-runic/bin/...).
const runicDev = process.env.RUNIC_DEV_TOOL_DLL ? ["dotnet", process.env.RUNIC_DEV_TOOL_DLL, "dev"] : ["dotnet", "runic", "dev"];
if (values.browser) await checkBrowser();
const dev = spawn("dbus-run-session", ["--", "xvfb-run", "-a", "-s", "-screen 0 1280x900x24", ...runicDev], {
  cwd: project,
  detached: true,
  stdio: ["ignore", "pipe", "pipe"],
  env: { ...process.env, GDK_BACKEND: "x11", GSETTINGS_BACKEND: "memory", RUNIC_DEV_CDP_TRACE: trace },
});
let output = "";
dev.stdout.on("data", chunk => { output += chunk; });
dev.stderr.on("data", chunk => { output += chunk; });
let exited = null;
dev.on("exit", (code, signal) => { exited = { code, signal }; });
const stop = () => {
  try { process.kill(-dev.pid, "SIGKILL"); } catch { }
};
process.on("exit", stop);

try {
  for (let waited = 0; !/^\[dev\] Ready$/m.test(output); waited += 500) {
    if (exited) throw new Error(`dotnet runic dev exited (${JSON.stringify(exited)}) before it was ready`);
    if (waited > startTimeout) throw new Error("dotnet runic dev was not ready in time");
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  const hint = /^\[dev\] Debugging: JavaScript debuggers attach to the Window at (http:\/\/127\.0\.0\.1:(\d+)\/)/m.exec(output);
  if (!hint) throw new Error("dotnet runic dev printed no [dev] Debugging: hint for the CDP adapter");
  const [, url, port] = hint;
  const session = JSON.parse(readFileSync(join(project, "obj/runic/dev-session.json"), "utf8"));
  const endpoint = session.debugEndpoints.find(entry => entry.engine === "webkitgtk" && entry.protocol === "cdp");
  if (endpoint?.url !== url) throw new Error(`The session file lists ${JSON.stringify(session.debugEndpoints)}, not ${url}`);
  if (!session.debugEndpoints.some(entry => entry.engine === "webkitgtk" && entry.protocol === "webkit-remote-inspector"))
    throw new Error("The session file lists no webkit-remote-inspector endpoint");
  if (!session.window.environment.WEBKIT_INSPECTOR_SERVER) throw new Error("The session file's window.environment has no WEBKIT_INSPECTOR_SERVER");
  console.log(`WEBKIT_CDP_ENDPOINT|${url}`);

  // A page in a browser or a DNS-rebound name must not reach the endpoint.
  const status = headers => spawnSync("curl", ["-s", "-o", "/dev/null", "-w", "%{http_code}",
    ...headers.flatMap(header => ["-H", header]), `${url}json/version`], { encoding: "utf8" }).stdout;
  for (const [headers, expected] of [[[], "200"], [["Origin: https://example.com"], "403"], [[`Host: example.com:${port}`], "403"]]) {
    const actual = status(headers);
    if (actual !== expected) throw new Error(`${JSON.stringify(headers)}: HTTP ${actual}, expected ${expected}`);
  }
  console.log("WEBKIT_CDP_SECURITY_OK|loopback without Origin 200|foreign Origin 403|foreign Host 403");

  const run = spawnSync(process.execPath, [scenarios, "--jsdebug", jsDebugDirectory, "--port", port,
    "--frontend", join(project, "Frontend"), "--restart-file", csharp], { encoding: "utf8", timeout: scaled(900_000) });
  process.stdout.write(run.stdout);
  process.stderr.write(run.stderr);
  if (run.status !== 0) {
    // The adapter's traffic while each failed scenario ran, without the page's console noise.
    for (const [, name, from, to] of run.stdout.matchAll(/^SCENARIO_WINDOW\|([^|]+)\|([\d:.]+)\|([\d:.]+)$/gm)) {
      await printTrace(name, from, to);
    }
    throw new Error(`js-debug scenarios failed (exit ${run.status ?? run.signal})`);
  }
  console.log(`WEBKIT_CDP_OK|${/^\[[\d.]+s\] (\d+\/\d+) passed$/m.exec(run.stdout)?.[1] ?? "?"}`);
} catch (error) {
  console.error(tail(output));
  console.error(`The adapter's traffic: ${trace}`);
  throw error;
} finally {
  stop();
}

// `dotnet runic dev --browser` and the template's "Window in Chrome" launch configuration (#1031); exits when done.
async function checkBrowser() {
  // The configuration as VS Code reads it: JSONC whose comments are whole lines, ${workspaceFolder} the project.
  const launch = JSON.parse(readFileSync(join(project, ".vscode/launch.json"), "utf8").split("\n").filter(line => !/^\s*\/\//.test(line)).join("\n"));
  const configuration = launch.configurations.find(entry => entry.name === "Window in Chrome");
  if (configuration?.type !== "chrome" || configuration.request !== "launch") throw new Error("The project has no \"Window in Chrome\" launch configuration");
  const webRoot = configuration.webRoot.replace("${workspaceFolder}", project);
  // No display: serve-only needs none.
  const environment = { ...process.env };
  for (const name of ["DISPLAY", "WAYLAND_DISPLAY", "RUNIC_APPLICATION_SERVE_ONLY"]) delete environment[name];
  const dev = spawn(runicDev[0], [...runicDev.slice(1), "--browser"], { cwd: project, detached: true, stdio: ["ignore", "pipe", "pipe"], env: environment });
  let output = "", exited = null;
  dev.stdout.on("data", chunk => { output += chunk; });
  dev.stderr.on("data", chunk => { output += chunk; });
  dev.on("exit", (code, signal) => { exited = { code, signal }; });
  const stop = () => { try { process.kill(-dev.pid, "SIGKILL"); } catch { } };
  process.on("exit", stop);
  try {
    for (let waited = 0; !/^\[dev\] Ready$/m.test(output); waited += 500) {
      if (exited) throw new Error(`dotnet runic dev --browser exited (${JSON.stringify(exited)}) before it was ready`);
      if (waited > startTimeout) throw new Error("dotnet runic dev --browser was not ready in time");
      await new Promise(resolve => setTimeout(resolve, 500));
    }
    const url = /^\[dev\] Browser: Window serves at (http:\/\/127\.0\.0\.1:\d+\/)$/m.exec(output)?.[1];
    if (!url) throw new Error("dotnet runic dev --browser printed no [dev] Browser: line for the Window");
    if (url !== configuration.url) throw new Error(`dev serves the Window at ${url}, the launch configuration opens ${configuration.url}`);
    const session = JSON.parse(readFileSync(join(project, "obj/runic/dev-session.json"), "utf8"));
    if (session.browser?.url !== url || session.browser.windows[0]?.url !== url) throw new Error(`The session file lists ${JSON.stringify(session.browser)}`);
    if (session.debugEndpoints.length !== 0) throw new Error(`--browser listed debugging endpoints: ${JSON.stringify(session.debugEndpoints)}`);
    // The surface's own address carries its credential: dev does not print it.
    if (/RUNIC_APPLICATION_(URL|WINDOW)=/.test(output)) throw new Error("dotnet runic dev --browser printed the Window's surface address");
    console.log(`DEV_BROWSER_ENTRY|${url}`);

    // The browser's own navigation is sent to the Window; a web page's, a foreign Origin or a DNS-rebound name is not.
    const status = headers => spawnSync("curl", ["-s", "-o", "/dev/null", "-w", "%{http_code}",
      ...headers.flatMap(header => ["-H", header]), url], { encoding: "utf8" }).stdout;
    for (const [headers, expected] of [[["Sec-Fetch-Site: none"], "302"], [["Sec-Fetch-Site: cross-site"], "403"],
      [["Origin: https://example.com"], "403"], [[`Host: example.com:${new URL(url).port}`], "403"]]) {
      const actual = status(headers);
      if (actual !== expected) throw new Error(`${JSON.stringify(headers)}: HTTP ${actual}, expected ${expected}`);
    }
    console.log("DEV_BROWSER_SECURITY_OK|browser navigation 302|cross-site 403|foreign Origin 403|foreign Host 403");

    // The page in Chrome under js-debug, as VS Code runs the configuration. A restart needs the address opened again, so
    // the Window restart scenario is left out.
    const browser = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
    const run = spawnSync(process.execPath, [scenarios, "--jsdebug", jsDebugDirectory, "--launch", configuration.url,
      "--frontend", webRoot, ...(browser ? ["--browser", browser] : []),
      "--only", "step-in-out,conditional,logpoint,exception,uncaught-output,console-levels,set-variable,reload,hmr"],
      { encoding: "utf8", timeout: scaled(900_000) });
    process.stdout.write(run.stdout);
    process.stderr.write(run.stderr);
    if (run.status !== 0) throw new Error(`js-debug scenarios in Chrome failed (exit ${run.status ?? run.signal})`);
    console.log(`DEV_BROWSER_OK|${/^\[[\d.]+s\] (\d+\/\d+) passed$/m.exec(run.stdout)?.[1] ?? "?"}`);
  } catch (error) {
    console.error(tail(output));
    throw error;
  } finally {
    stop();
  }
  process.exit(0);
}
