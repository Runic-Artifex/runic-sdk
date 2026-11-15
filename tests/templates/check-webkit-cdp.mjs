// IDE debugging on Linux (#1029, D066.39): a generated React runic-app project under `dotnet runic dev` on Xvfb, and VS
// Code's JavaScript debugger (vscode-js-debug's DAP server, pinned below) attached through dev's CDP adapter on 9222 to the
// WebKitGTK Window (tests/webkit-cdp/dap-scenarios.mjs). Also checks the session file's debugEndpoints entry, the
// `[dev] Debugging:` hint and the endpoint's refusal of other origins and host names.
//
//   node check-webkit-cdp.mjs --project <generated project directory> [--js-debug <unpacked js-debug directory>]
//
// RUNIC_DEV_TOOL_DLL=<path to dotnet-runic.dll> runs a locally built dev instead of the project's dotnet-runic tool.
// The project's dotnet-runic must be restored. Needs Xvfb, dbus-run-session and the WebKitGTK of the project's GTK.
import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { scaled } from "../support/timeouts.mjs";

// The js-debug release the check runs (D038: older than a day when pinned), and its archive's SHA-256.
const jsDebugVersion = "1.140.1";
const jsDebugSha256 = "c72fab31bcbfdce6ad1f42adfd28eff8d4681dd3b03b5e2e6f9e72e6f9927faf";

const { values } = parseArgs({ options: { project: { type: "string" }, "js-debug": { type: "string" } } });
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
    "--frontend", join(project, "Frontend"), "--restart-file", csharp], { encoding: "utf8", timeout: scaled(600_000) });
  process.stdout.write(run.stdout);
  process.stderr.write(run.stderr);
  if (run.status !== 0) throw new Error(`js-debug scenarios failed (exit ${run.status ?? run.signal})`);
  console.log(`WEBKIT_CDP_OK|${/^\[[\d.]+s\] (\d+\/\d+) passed$/m.exec(run.stdout)?.[1] ?? "?"}`);
} catch (error) {
  console.error(tail(output));
  if (existsSync(trace)) console.error(`The adapter's traffic: ${trace}`);
  throw error;
} finally {
  stop();
}
