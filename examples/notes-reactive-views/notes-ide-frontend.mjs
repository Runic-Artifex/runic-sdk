import { spawn, spawnSync } from "node:child_process";
import { appendFileSync, existsSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { basename, dirname, join, resolve, toNamespacedPath } from "node:path";
import { pickFreeLoopbackPort, startAngularSourceMapProxy } from "./angular-source-map-proxy.mjs";

const [parentText, frontend, framework, backendOrigin, portText, profilePath] = process.argv.slice(2);
const parent = Number(parentText);
const port = Number(portText);
if (!Number.isSafeInteger(parent) || parent < 1 || !Number.isSafeInteger(port) || port < 1 || profilePath === undefined)
  throw new Error("Expected a parent PID, frontend port, and browser profile path.");

const environment = { ...process.env, RUNIC_WEBUI_ORIGIN: backendOrigin };
delete environment.RUNIC_DEV_ORIGIN;
let child;
let closing = false;
let closeAngularProxy;
function closeOwnedBrowser() {
  if (!profilePath) return;
  const appArgument = `--app=http://127.0.0.1:${port}/`;
  const profileArgument = `--user-data-dir=${profilePath}`;
  if (process.platform === "win32") {
    const query = "$app=$env:RUNIC_IDE_APP_ARGUMENT; $profile=$env:RUNIC_IDE_PROFILE_PATH; " +
      "Get-CimInstance Win32_Process -Filter \"Name='chrome.exe'\" | " +
      "Where-Object { $_.CommandLine -and $_.CommandLine.Contains($app) -and $_.CommandLine.Contains($profile) } | " +
      "Select-Object -ExpandProperty ProcessId | ConvertTo-Json -Compress";
    const result = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", query], {
      env: { ...process.env, RUNIC_IDE_APP_ARGUMENT: appArgument, RUNIC_IDE_PROFILE_PATH: profilePath },
      encoding: "utf8", timeout: 5000
    });
    if (result.status !== 0) {
      console.error(`RUNIC_IDE_BROWSER_CLEANUP_FAILED|${result.error ?? result.stderr}`);
      return;
    }
    const output = result.stdout.trim();
    for (const pid of output ? [JSON.parse(output)].flat() : [])
      if (Number.isSafeInteger(pid) && pid > 0)
        spawnSync("taskkill.exe", ["/PID", String(pid), "/T", "/F"], { timeout: 5000 });

    const absoluteProfile = resolve(profilePath);
    const root = dirname(absoluteProfile);
    if (basename(root) === "runic-ide-browser" && basename(dirname(root)) === "obj" &&
        new RegExp(`^${parent}-[0-9a-f]{32}$`).test(basename(absoluteProfile))) {
      const session = join(dirname(dirname(root)), "obj", "runic-ide-session.json");
      try {
        const current = JSON.parse(readFileSync(session, "utf8"));
        if (current.pid === parent && current.url === `http://127.0.0.1:${port}/` &&
            resolve(current.profilePath) === absoluteProfile)
          rmSync(session, { force: true });
      } catch { /* No session record, or a new session replaced it. */ }
      let lastError;
      for (let attempt = 0; attempt < 20 && existsSync(absoluteProfile); attempt++) {
        try { rmSync(toNamespacedPath(absoluteProfile), { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }); }
        catch (error) { lastError = error; }
        if (existsSync(absoluteProfile)) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 250);
      }
      if (existsSync(absoluteProfile)) {
        const message = `RUNIC_IDE_PROFILE_CLEANUP_FAILED|${lastError}\n`;
        console.error(message.trim());
        appendFileSync(join(dirname(root), "runic-ide-cleanup.log"), message);
      }
    }
    return;
  }
  if (process.platform !== "linux") return;
  for (const entry of readdirSync("/proc")) {
    if (!/^[1-9][0-9]*$/.test(entry)) continue;
    try {
      const argumentsToProcess = readFileSync(`/proc/${entry}/cmdline`, "utf8").split("\0");
      if (argumentsToProcess.some(arg => arg.includes(appArgument)) &&
          argumentsToProcess.some(arg => arg.includes(profileArgument)))
        process.kill(Number(entry), "SIGKILL");
    } catch { /* Another process or already closed. */ }
  }
}
function stop() {
  if (closing) return;
  closing = true;
  closeOwnedBrowser();
  closeAngularProxy?.();
  child?.kill("SIGTERM");
  setTimeout(() => {
    if (child && child.exitCode === null) child.kill("SIGKILL");
    process.exit(0);
  }, 2000).unref();
}
process.on("SIGTERM", stop);
process.on("SIGINT", stop);
setInterval(() => {
  try { process.kill(parent, 0); }
  catch { stop(); }
}, 500).unref();

function launch(args) {
  child = spawn(process.execPath, args, { cwd: frontend, env: environment, stdio: "inherit" });
  return child;
}
function completed(processHandle) {
  return new Promise((resolve, reject) => {
    processHandle.once("error", reject);
    processHandle.once("exit", code => resolve(code ?? 1));
  });
}
function installPackages(directory) {
  if (framework !== "angular") {
    child = spawn("bun", ["install", "--frozen-lockfile"],
      { cwd: directory, env: environment, stdio: "inherit" });
    return completed(child);
  }
  const windows = process.platform === "win32";
  child = spawn(windows ? "cmd.exe" : "npm", windows ? ["/d", "/s", "/c", "npm.cmd", "ci"] : ["ci"],
    { cwd: directory, env: environment, stdio: "inherit" });
  return completed(child);
}

const cli = framework === "angular"
  ? join(frontend, "node_modules/@angular/cli/bin/ng.js")
  : join(frontend, "node_modules/vite/bin/vite.js");
if (!existsSync(cli)) {
  console.log("RUNIC_IDE_INSTALLING_FRONTEND");
  if (await installPackages(frontend) !== 0) throw new Error("Frontend npm ci failed.");
}

let argumentsToRun;
if (framework === "angular") {
  if (await completed(launch(["sync-host-script.mjs"])) !== 0)
    throw new Error("Angular host script sync failed.");
  const angularPort = await pickFreeLoopbackPort();
  closeAngularProxy = await startAngularSourceMapProxy(port, angularPort);
  console.log(`RUNIC_IDE_ANGULAR_MAP_PROXY|visible=${port}|angular=${angularPort}`);
  argumentsToRun = [join(frontend, "node_modules/@angular/cli/bin/ng.js"), "serve",
    "--configuration", "development", "--host", "127.0.0.1", "--port", String(angularPort),
    ...(process.platform === "win32" ? ["--poll", "500"] : [])];
} else if (framework === "svelte" || framework === "typescript") {
  argumentsToRun = [join(frontend, "node_modules/vite/bin/vite.js"), "--host", "127.0.0.1",
    "--port", String(port), "--strictPort"];
} else {
  throw new Error(`Unknown frontend framework: ${framework}`);
}
launch(argumentsToRun);
const frontendExit = await completed(child);
if (frontendExit !== 0)
  console.error(`RUNIC_IDE_FRONTEND_EXIT|framework=${framework}|code=${frontendExit}`);
process.exitCode = frontendExit;
closeAngularProxy?.();
