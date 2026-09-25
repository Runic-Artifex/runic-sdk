#!/usr/bin/env node
import { spawn } from "node:child_process";
import { existsSync, readdirSync, watch } from "node:fs";
import { createServer } from "node:http";
import { createServer as createTcpServer } from "node:net";
import { basename, dirname, isAbsolute, join, resolve } from "node:path";

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
    cwd, env: { ...process.env, ...environment }, stdio: ["pipe", "pipe", "pipe"]
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

async function stop(child) {
  if (!child || child.exitCode !== null) return;
  const done = exited(child);
  child.stdin?.end();
  child.kill("SIGTERM");
  await Promise.race([done, new Promise(accept => setTimeout(accept, 3000))]);
  if (child.exitCode === null) child.kill("SIGKILL");
}

const options = optionsFrom(process.argv.slice(2));
const [backendPort, frontendPort, coordinatorPort] = await Promise.all([unusedPort(), unusedPort(), unusedPort()]);
let generation = 0;
let backend;
let frontend;
let build;
let rebuilding = false;
let pending = false;
let closing = false;
let timer;
const clients = new Set();
const root = dirname(options.project);
const assembly = join(root, "bin", "Release", "net10.0", `${basename(options.project, ".csproj")}.dll`);

const coordinator = createServer((request, response) => {
  if (request.url === "/__runic_dev/status") {
    response.setHeader("Content-Type", "application/json");
    response.end(JSON.stringify({ generation, backendPort, frontendPort }));
  } else if (request.url === "/__runic_dev/events") {
    response.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache", Connection: "keep-alive" });
    response.write(`data: ${generation}\n\n`);
    clients.add(response);
    request.on("close", () => clients.delete(response));
  } else {
    response.writeHead(404).end();
  }
});
await new Promise(accept => coordinator.listen(coordinatorPort, "127.0.0.1", accept));

async function buildBackend() {
  build = run("dotnet", ["build", options.project, "-c", "Release", "--nologo"], root);
  const code = await exited(build);
  build = undefined;
  return code === 0;
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
      if (response.ok) return;
    } catch { /* wait for server */ }
    await new Promise(accept => setTimeout(accept, 250));
  }
  throw new Error("Frontend dev server did not become ready.");
}

async function rebuild() {
  if (rebuilding || closing) { pending = true; return; }
  rebuilding = true;
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
  rebuilding = false;
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
  build?.kill("SIGTERM");
  await Promise.all([stop(backend), stop(frontend)]);
  for (const client of clients) client.end();
  coordinator.close();
}

let watcher;
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
