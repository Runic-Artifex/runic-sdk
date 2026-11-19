// IDE debugging on Linux (#1029, D066.39): a generated React runic-app project under `dotnet runic dev` on Xvfb, and VS
// Code's JavaScript debugger (vscode-js-debug's DAP server, pinned below) attached through dev's CDP adapter on 9222 to the
// WebKitGTK Window (tests/webkit-cdp/dap-scenarios.mjs). Also checks the session file's debugEndpoints entry, the
// `[dev] Debugging:` hint, the endpoint's refusal of other origins and host names, and dev's line when another client
// took WebKit's inspector from the adapter.
//
//   node check-webkit-cdp.mjs --project <generated project directory> [--js-debug <unpacked js-debug directory>]
//     [--only <scenario,…>] [--modules <count>] [--per-script-sourcemaps auto|yes|no] [--trace <js-debug log file>]
//
// With --browser (#1031), the project's "Window in Chrome" launch configuration instead (it is generated with --editor
// vscode): `dotnet runic dev --browser` without a display, the entry address in its output and session file, its code file
// and one-time code (#1187), its refusal of other clients and web pages, and js-debug launching Chrome at the configuration's
// file with the same scenarios on Chromium, a Window restart among them: the page reloads itself onto the new Window.
// PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH selects the browser; otherwise js-debug finds the installed Chrome.
//
// RUNIC_DEV_TOOL_DLL=<path to dotnet-runic.dll> runs a locally built dev instead of the project's dotnet-runic tool.
// The project's dotnet-runic must be restored. Needs Xvfb, dbus-run-session and the WebKitGTK of the project's GTK.
import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { createReadStream, existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { createInterface } from "node:readline";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { scaled } from "../support/timeouts.mjs";

// The js-debug release the check runs (D038: older than a day when pinned), and its archive's SHA-256.
const jsDebugVersion = "1.140.1";
const jsDebugSha256 = "c72fab31bcbfdce6ad1f42adfd28eff8d4681dd3b03b5e2e6f9e72e6f9927faf";

const { values } = parseArgs({ options: {
  project: { type: "string" }, "js-debug": { type: "string" },
  // Passed to dap-scenarios.mjs: the scenarios to run, the late-module scenario's module count, js-debug's
  // perScriptSourcemaps and its log.
  // --browser: the "Window in Chrome" lane instead (#1031).
  browser: { type: "boolean" },
  only: { type: "string" }, modules: { type: "string" }, "per-script-sourcemaps": { type: "string" }, trace: { type: "string" },
} });
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
    "--frontend", join(project, "Frontend"), "--restart-file", csharp, "--inspector", session.window.environment.WEBKIT_INSPECTOR_SERVER,
    ...["only", "modules", "per-script-sourcemaps", "trace"].flatMap(name => values[name] ? [`--${name}`, values[name]] : [])], { encoding: "utf8", timeout: scaled(900_000) });
  process.stdout.write(run.stdout);
  process.stderr.write(run.stderr);
  if (run.status !== 0) {
    // The adapter's traffic while each failed scenario ran, without the page's console noise.
    for (const [, name, from, to] of run.stdout.matchAll(/^SCENARIO_WINDOW\|([^|]+)\|([\d:.]+)\|([\d:.]+)$/gm)) {
      await printTrace(name, from, to);
    }
    throw new Error(`js-debug scenarios failed (exit ${run.status ?? run.signal})`);
  }
  if (!values.only || values.only.split(",").includes("replaced-client")) {
    // dev's output arrives once the event loop runs again after the synchronous scenario run.
    for (let waited = 0; !/^\[dev\] Debugging: another client took over the Window's WebKit inspector at /m.test(output); waited += 250) {
      if (waited > 10_000) throw new Error("dotnet runic dev printed no [dev] line when another client took the Window's WebKit inspector");
      await new Promise(resolve => setTimeout(resolve, 250));
    }
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
    // The per-session origin (D077.15): http://<label>.localhost:<port>, a label of lowercase letters and digits.
    const line = /^\[dev\] Browser: Window serves at (http:\/\/([a-z0-9]{26})\.localhost:(\d+)\/)\?code=([\w-]+)$/m.exec(output);
    if (!line) throw new Error("dotnet runic dev --browser printed no [dev] Browser: line for the Window");
    const [, url, sessionLabel, port, printedCode] = line;
    const publicUrl = `http://*.localhost:${port}/`;
    // The launch configuration opens the code file, which only this user can read and which opens the address with the code.
    const codeFile = configuration.file?.replace("${workspaceFolder}", project);
    const session = JSON.parse(readFileSync(join(project, "obj/runic/dev-session.json"), "utf8"));
    // The session file has * for the label: like the code, the label is a secret, and the file is not owner-only.
    if (session.browser?.url !== publicUrl || session.browser.windows[0]?.url !== publicUrl) throw new Error(`The session file lists ${JSON.stringify(session.browser)}`);
    if (readFileSync(join(project, "obj/runic/dev-session.json"), "utf8").includes(sessionLabel)) throw new Error("The session file carries the session label");
    if (session.browser.codeFile !== codeFile) throw new Error(`The session file names ${session.browser.codeFile}, the launch configuration opens ${codeFile}`);
    if (JSON.stringify(session).includes(printedCode)) throw new Error("The session file carries the one-time code");
    if (session.debugEndpoints.length !== 0) throw new Error(`--browser listed debugging endpoints: ${JSON.stringify(session.debugEndpoints)}`);
    if ((statSync(codeFile).mode & 0o777) !== 0o600) throw new Error(`${codeFile} has mode ${(statSync(codeFile).mode & 0o777).toString(8)}, not 600`);
    const codeOf = () => /\?code=([\w-]+)/.exec(readFileSync(codeFile, "utf8"))?.[1];
    if (codeOf() !== printedCode) throw new Error("The code file does not open the address with the printed code");
    // The surface's own address carries its credential: dev does not print it.
    if (/RUNIC_APPLICATION_(URL|WINDOW)=/.test(output)) throw new Error("dotnet runic dev --browser printed the Window's surface address");
    console.log(`DEV_BROWSER_ENTRY|${url}|${codeFile}`);

    // Only the code admits a browser, also from the code file's page (file:, so cross-site), with dev's cookie; then only
    // that browser's own requests reach the Window: not another client, a web page, a foreign Origin or a DNS-rebound name.
    // curl reaches <label>.localhost on the loopback interface (--resolve), as a browser resolves it itself.
    const request = (headers, target = url) => {
      const run = spawnSync("curl", ["-s", "--resolve", `${sessionLabel}.localhost:${port}:127.0.0.1`, "-D", "-", "-w", "\n%{http_code}",
        ...headers.flatMap(header => ["-H", header]), target], { encoding: "utf8" });
      return { status: run.stdout.slice(-3), cookie: /^set-cookie: ([^\r\n]*)/im.exec(run.stdout)?.[1], text: run.stdout };
    };
    const check = (label, headers, expected, target) => {
      const actual = request(headers, target).status;
      if (actual !== expected) throw new Error(`${label} ${JSON.stringify(headers)}: HTTP ${actual}, expected ${expected}`);
    };
    check("no code", ["Sec-Fetch-Site: none"], "403");
    // Another name, with the code too, is refused without the label: 127.0.0.1, plain localhost, another label.
    for (const host of [`127.0.0.1:${port}`, `localhost:${port}`, `other.localhost:${port}`]) {
      for (const target of [url, `${url}?code=${printedCode}`]) {
        const answer = request([`Host: ${host}`, "Sec-Fetch-Site: none"], target);
        if (answer.status !== "403" || answer.text.includes(sessionLabel) || answer.cookie || /^location:/im.test(answer.text))
          throw new Error(`Host ${host}: HTTP ${answer.status}${answer.text.includes(sessionLabel) ? ", the answer names the label" : ""}`);
      }
    }
    check("a wrong code", ["Sec-Fetch-Site: none"], "403", `${url}?code=guess`);
    const admitted = request(["Sec-Fetch-Site: cross-site"], `${url}?code=${printedCode}`);
    if (admitted.status !== "200" || !/; HttpOnly; SameSite=Strict$/.test(admitted.cookie ?? "")) throw new Error(`The code's admission: ${JSON.stringify(admitted)}`);
    const cookie = `Cookie: ${admitted.cookie.split(";")[0]}`;
    if (codeOf() === printedCode) throw new Error("The admission did not replace the code in the code file");
    check("the used code", ["Sec-Fetch-Site: none"], "403", `${url}?code=${printedCode}`);
    check("the admitted browser", [cookie, "Sec-Fetch-Site: none"], "302");
    check("a cross-site request", [cookie, "Sec-Fetch-Site: cross-site"], "403");
    check("a foreign Origin", [cookie, "Origin: https://example.com"], "403");
    check("a foreign Host", [cookie, `Host: example.com:${port}`], "403");
    check("127.0.0.1 with the cookie", [cookie, `Host: 127.0.0.1:${port}`], "403");
    if (/;\s*domain=/i.test(admitted.cookie)) throw new Error(`dev's cookie is not host-only: ${admitted.cookie}`);
    console.log("DEV_BROWSER_SECURITY_OK|no code 403|other names 403 without the label|code admits 200 host-only HttpOnly SameSite=Strict|used code 403|admitted 302|cross-site 403|foreign Origin 403|foreign Host 403");

    // The page in Chrome under js-debug, as VS Code runs the configuration: its file. The Window restarts under the same
    // session, and the page reloads itself onto the new Window, where the breakpoint stops again.
    // Run without blocking this process, which keeps reading dev's output: a full pipe would stop dev and dotnet watch.
    const browser = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
    const run = await new Promise(resolve => {
      const child = spawn(process.execPath, [scenarios, "--jsdebug", jsDebugDirectory, "--launch-file", codeFile,
        "--frontend", webRoot, "--restart-file", csharp, ...(browser ? ["--browser", browser] : []),
        "--only", values.only ?? "step-in-out,conditional,logpoint,exception,uncaught-output,console-levels,set-variable,reload,window-restart,hmr",
        ...(values.trace ? ["--trace", values.trace] : [])], { stdio: ["ignore", "pipe", "pipe"] });
      let stdout = "";
      const timer = setTimeout(() => child.kill("SIGKILL"), scaled(900_000));
      child.stdout.on("data", chunk => { stdout += chunk; process.stdout.write(chunk); });
      child.stderr.on("data", chunk => process.stderr.write(chunk));
      child.on("exit", (status, signal) => { clearTimeout(timer); resolve({ status, signal, stdout }); });
    });
    if (run.status !== 0) throw new Error(`js-debug scenarios in Chrome failed (exit ${run.status ?? run.signal})`);
    if (!values.only && !/^\[[\d.]+s\] PASS window-restart: after the Window restarted: stopped at /m.test(run.stdout))
      throw new Error("The window-restart scenario did not pass");
    // Chrome's admission replaced the code: dev printed the next one.
    if (!/^\[dev\] Browser: a browser used the code\. The next one is http:\/\/[a-z0-9]+\.localhost:\d+\/\?code=[\w-]+; /m.test(output))
      throw new Error("dotnet runic dev --browser printed no line for the browser it admitted");
    // One bound browser: Chrome's admission ended curl's.
    check("the earlier browser", [cookie, "Sec-Fetch-Site: none"], "403");
    console.log(`DEV_BROWSER_OK|${/^\[[\d.]+s\] (\d+\/\d+) passed$/m.exec(run.stdout)?.[1] ?? "?"}`);
  } catch (error) {
    console.error(tail(output));
    throw error;
  } finally {
    stop();
  }
  process.exit(0);
}
