#!/usr/bin/env node
// SDK-internal runner for the repository's multi-frontend Views examples
// (plain TypeScript, Svelte, and Angular variants of one .NET project). It is
// not shipped. Applications and generated projects use `dotnet runic dev`.
import { execFile, spawn } from "node:child_process";
import { promisify } from "node:util";
import { existsSync, readdirSync, realpathSync, watch } from "node:fs";
import { createServer } from "node:http";
import { createServer as createTcpServer } from "node:net";
import { basename, dirname, isAbsolute, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

// The coordinator listens on 127.0.0.1 only. It answers requests whose Host is a loopback name, so a page on
// another site cannot reach it through DNS rebinding, and it serves a bounded number of event streams.
export const maximumEventClients = 16;
const loopbackHosts = new Set(["127.0.0.1", "localhost", "[::1]"]);

/** Whether a Host header names this machine: 127.0.0.1, localhost or [::1], with any port (the dev proxies keep it). */
export function isLoopbackHost(host) {
  if (typeof host !== "string") return false;
  const match = /^(\[[^\]]*\]|[^:]*)(?::\d{1,5})?$/.exec(host.trim().toLowerCase());
  return match !== null && loopbackHosts.has(match[1]);
}

/**
 * The coordinator's request handler: `/__runic_dev/status` and the `/__runic_dev/events` stream of restart
 * generations. `state()` returns `{ generation, backendPort, frontendPort }`; `clients` holds the open streams.
 */
export function coordinatorHandler({ state, clients, maximumClients = maximumEventClients }) {
  return (request, response) => {
    if (!isLoopbackHost(request.headers.host)) {
      response.writeHead(403).end();
    } else if (request.url === "/__runic_dev/status") {
      response.setHeader("Content-Type", "application/json");
      response.end(JSON.stringify(state()));
    } else if (request.url === "/__runic_dev/events") {
      if (clients.size >= maximumClients) {
        response.writeHead(503, { "Retry-After": "1" }).end();
        return;
      }
      response.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache", Connection: "keep-alive" });
      response.write(`data: ${state().generation}\n\n`);
      clients.add(response);
      request.on("close", () => clients.delete(response));
    } else {
      response.writeHead(404).end();
    }
  };
}

function optionsFrom(args) {
  const options = { framework: "vanilla" };
  for (let index = 0; index < args.length; index++) {
    const key = args[index];
    if (!["--project", "--frontend", "--framework"].includes(key) || !args[index + 1])
      throw new Error(`Expected a value for ${key}.`);
    options[key.slice(2)] = args[++index];
  }
  if (!["vanilla", "angular", "svelte"].includes(options.framework))
    throw new Error("--framework must be vanilla, angular, or svelte.");
  const project = resolve(options.project ?? readdirSync(process.cwd()).find(name => name.endsWith(".csproj")) ?? "");
  if (!project.endsWith(".csproj") || !existsSync(project))
    throw new Error("Supply --project with an existing .csproj.");
  const frontend = resolve(options.frontend ?? join(dirname(project), "Frontend"));
  if (!existsSync(join(frontend, "package.json")))
    throw new Error(`No frontend package.json in ${frontend}.`);
  return { ...options, project, frontend };
}

function unusedPort() {
  return new Promise((accept, reject) => {
    const server = createTcpServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const port = server.address().port;
      server.close(() => accept(port));
    });
  });
}

function run(command, args, cwd, environment = {}) {
  const child = spawn(command, args, {
    cwd, env: { ...process.env, ...environment }, stdio: ["pipe", "pipe", "pipe"],
    detached: process.platform !== "win32"
  });
  for (const stream of [child.stdout, child.stderr])
    stream.on("data", chunk => process.stdout.write(chunk));
  return child;
}

function exited(child) {
  return new Promise((accept, reject) => {
    child.once("error", reject);
    child.once("exit", code => accept(code ?? 1));
  });
}

function signalTree(child, signal) {
  if (!child?.pid) return;
  if (process.platform === "win32") { child.kill(signal); return; }
  try { process.kill(-child.pid, signal); }
  catch (error) { if (error.code !== "ESRCH") throw error; }
}

async function stop(child) {
  if (!child) return;
  const done = child.exitCode === null && child.signalCode === null ? exited(child) : Promise.resolve();
  child.stdin?.end();
  signalTree(child, "SIGTERM");
  await Promise.race([done, new Promise(accept => setTimeout(accept, 3000))]);
  if (process.platform !== "win32") {
    // npm may exit before its dev-server child; finish the owned process group.
    await new Promise(accept => setTimeout(accept, 150));
    signalTree(child, "SIGKILL");
  } else if (child.exitCode === null && child.signalCode === null) child.kill("SIGKILL");
}

let options;
let backendPort;
let frontendPort;
let coordinatorPort;
let coordinator;
let generation = 0;
let backend;
let frontend;
let build;
let rebuilding = false;
let pending = false;
let closing = false;
let timer;
const clients = new Set();

let root;
let assembly;

async function buildBackend() {
  build = run("dotnet", ["build", options.project, "-c", "Release", "--nologo"], root);
  const code = await exited(build);
  build = undefined;
  if (code !== 0) return false;
  assembly = await targetPath();
  return true;
}

// Ask MSBuild for the built assembly instead of guessing bin/Release/<tfm>:
// OutputPath, AssemblyName and the target framework are project settings.
async function msbuildProperty(name, properties = []) {
  const { stdout } = await promisify(execFile)("dotnet",
    ["msbuild", options.project, `-getProperty:${name}`, "-p:Configuration=Release", ...properties], { cwd: root });
  return stdout.trim();
}

async function targetPath() {
  let path = await msbuildProperty("TargetPath");
  if (!path) {
    // A multi-targeted project has no TargetPath until a framework is chosen.
    const framework = (await msbuildProperty("TargetFrameworks")).split(";").find(Boolean);
    if (framework) path = await msbuildProperty("TargetPath", [`-p:TargetFramework=${framework}`]);
  }
  if (!path || !existsSync(path)) throw new Error(`Could not locate the built assembly for ${basename(options.project)}.`);
  return path;
}

function awaitOutput(child, pattern, timeout = 30000) {
  return new Promise((accept, reject) => {
    let output = "";
    const deadline = setTimeout(() => reject(new Error(`Timed out waiting for ${pattern}.`)), timeout);
    function read(chunk) {
      output += chunk.toString();
      if (pattern.test(output)) finish();
    }
    function fail() { finish(new Error("Process exited before it was ready.")); }
    function finish(error) {
      clearTimeout(deadline);
      child.stdout.off("data", read);
      child.off("exit", fail);
      error ? reject(error) : accept();
    }
    child.stdout.on("data", read);
    child.once("exit", fail);
  });
}

async function startBackend() {
  backend = run("dotnet", [assembly, "--serve-only", "--port", String(backendPort)], root);
  await awaitOutput(backend, /http:\/\//);
}

async function startFrontend() {
  const args = options.framework === "angular"
    ? ["run", "start", "--", "--configuration", "runic", "--host", "127.0.0.1", "--port", String(frontendPort)]
    : ["run", "dev", "--", "--port", String(frontendPort), "--strictPort"];
  frontend = run("npm", args, options.frontend, {
    RUNIC_WEBUI_ORIGIN: `http://127.0.0.1:${backendPort}`,
    RUNIC_DEV_ORIGIN: `http://127.0.0.1:${coordinatorPort}`
  });
  const deadline = Date.now() + 60000;
  while (Date.now() < deadline) {
    if (frontend.exitCode !== null) throw new Error("Frontend dev server exited.");
    try {
      const response = await fetch(`http://127.0.0.1:${frontendPort}/`);
      const ready = response.ok;
      await response.arrayBuffer();
      if (ready) return;
    } catch { /* wait for server */ }
    await new Promise(accept => setTimeout(accept, 250));
  }
  throw new Error("Frontend dev server did not become ready.");
}

async function rebuild() {
  if (rebuilding || closing) { pending = true; return; }
  rebuilding = true;
  try {
    do {
      pending = false;
      if (await buildBackend()) {
        await stop(backend);
        await startBackend();
        generation++;
        for (const client of clients) client.write(`data: ${generation}\n\n`);
        console.log(`RUNIC_DEV_RESTARTED|generation=${generation}`);
      } else {
        console.error("RUNIC_DEV_BUILD_FAILED|backend remains available");
      }
    } while (pending && !closing);
  } finally {
    // A failed restart must not leave every later source change ignored.
    rebuilding = false;
  }
}

function sourceChanged(name) {
  if (!name || name.startsWith("bin/") || name.startsWith("obj/") || name.startsWith("Frontend/")) return;
  if (!/\.(cs|csproj|props|targets)$/.test(name)) return;
  clearTimeout(timer);
  timer = setTimeout(() => { rebuild().catch(error => console.error(error)); }, 400);
}

async function close() {
  if (closing) return;
  closing = true;
  clearTimeout(timer);
  watcher?.close();
  await Promise.all([stop(build), stop(backend), stop(frontend)]);
  for (const client of clients) client.end();
  coordinator?.close();
}

let watcher;

async function main() {
  options = optionsFrom(process.argv.slice(2));
  root = dirname(options.project);
  [backendPort, frontendPort, coordinatorPort] = await Promise.all([unusedPort(), unusedPort(), unusedPort()]);
  coordinator = createServer(coordinatorHandler({
    state: () => ({ generation, backendPort, frontendPort }),
    clients,
  }));
  await new Promise(accept => coordinator.listen(coordinatorPort, "127.0.0.1", accept));
  try {
    if (!await buildBackend()) throw new Error("Initial backend build failed.");
    await startBackend();
    await startFrontend();
    generation = 1;
    watcher = watch(root, { recursive: true }, (_, filename) => sourceChanged(filename?.replaceAll("\\", "/")));
    console.log(`RUNIC_DEV_READY|http://127.0.0.1:${frontendPort}/|backend=${backendPort}|events=${coordinatorPort}`);
    process.on("SIGINT", () => { close().then(() => process.exit(0)); });
    process.on("SIGTERM", () => { close().then(() => process.exit(0)); });
  } catch (error) {
    console.error(`runic views dev: ${error.message}`);
    await close();
    process.exitCode = 1;
  }
}

// Run only as a program; tests import the coordinator handler.
if (process.argv[1] && existsSync(process.argv[1]) && import.meta.url === pathToFileURL(realpathSync(process.argv[1])).href) await main();
