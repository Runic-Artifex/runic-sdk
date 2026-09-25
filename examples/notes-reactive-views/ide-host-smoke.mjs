import { spawn } from "node:child_process";
import { createConnection } from "node:net";
import { fileURLToPath } from "node:url";

const assembly = fileURLToPath(new URL("./bin/Debug/net10.0/NotesReactiveViews.dll", import.meta.url));
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

async function retry(action, timeout = 30_000) {
  const deadline = Date.now() + timeout;
  let last;
  while (Date.now() < deadline) {
    try { if (await action()) return; }
    catch (error) { last = error; }
    await pause(100);
  }
  throw new Error(`Timed out waiting for IDE host: ${last ?? "no detail"}`);
}

async function responds(url) {
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(700) });
    return { ok: response.ok, status: response.status };
  }
  catch (error) { return { ok: false, error: String(error) }; }
}

function acceptsConnections(url) {
  return new Promise(resolve => {
    const endpoint = new URL(url);
    const socket = createConnection(Number(endpoint.port), endpoint.hostname === "localhost" ? "127.0.0.1" : endpoint.hostname);
    socket.setTimeout(700);
    socket.once("connect", () => { socket.destroy(); resolve(true); });
    socket.once("error", () => resolve(false));
    socket.once("timeout", () => { socket.destroy(); resolve(false); });
  });
}

async function verify(framework, stopWithSignal = false) {
  const host = spawn("dotnet", [assembly, "--serve-only"], {
    env: { ...process.env, RUNIC_DEV_FRONTEND: framework },
    detached: true,
    stdio: ["pipe", "pipe", "pipe"],
  });
  let output = "";
  host.stdout.on("data", chunk => { output += chunk.toString(); });
  host.stderr.on("data", chunk => { output += chunk.toString(); });
  try {
    let frontend, backend, angularOrigin;
    await retry(() => {
      if (host.exitCode !== null) throw new Error(`Host exited:\n${output}`);
      const ready = output.match(/RUNIC_IDE_READY\|(http:\/\/127\.0\.0\.1:\d+\/)\|backend=(http:\/\/[^|\r\n]+)\|framework=/);
      frontend = ready?.[1];
      backend = ready?.[2];
      const angularPort = output.match(/RUNIC_IDE_ANGULAR_MAP_PROXY\|visible=\d+\|angular=(\d+)/)?.[1];
      if (angularPort) angularOrigin = `http://127.0.0.1:${angularPort}/`;
      return frontend && backend;
    }, 90_000);
    if (!(await responds(frontend)).ok || !(await responds(new URL("/runic-cswebui.js", frontend))).ok)
      throw new Error(`Frontend did not become ready: ${output}`);
    // This serve-only probe may take WebUI's single client slot once; the Debug host never does.
    const bridge = await responds(new URL("/webui.js", frontend));
    if (!bridge.ok) throw new Error(`The frontend Bridge proxy failed on first request: ${JSON.stringify(bridge)}`);
    if (framework === "angular") {
      if (!angularOrigin) throw new Error(`Angular map proxy did not report its internal port: ${output}`);
      const bundle = await (await fetch(new URL("/main.js", frontend))).text();
      if (/^\/\/# debugId=/m.test(bundle) || !bundle.includes("sourceMappingURL=main.js.map"))
        throw new Error("Angular Debug response lost its source map or retained the Debug ID directive.");
      if (!(await responds(new URL("/main.js.map", frontend))).ok)
        throw new Error("Angular source map was unavailable through the Debug proxy.");
    }

    if (stopWithSignal) host.kill("SIGTERM");
    else host.stdin.end("\n");
    await retry(() => host.exitCode !== null || host.signalCode !== null, 10_000);
    await retry(async () => !(await responds(frontend)).ok && !await acceptsConnections(backend) &&
      (!angularOrigin || !await acceptsConnections(angularOrigin)), 10_000);
    console.log(`REACTIVE_NOTES_IDE_HOST_OK|${framework}|${stopWithSignal ? "signal" : "normal"}|ports-closed`);
  } finally {
    if (host.exitCode === null && host.signalCode === null) {
      try { process.kill(-host.pid, "SIGTERM"); } catch { /* Already stopped. */ }
      await pause(300);
      try { process.kill(-host.pid, "SIGKILL"); } catch { /* Already stopped. */ }
    }
  }
}

await verify("angular");
await verify("angular", true);
await verify("svelte");
await verify("svelte", true);
