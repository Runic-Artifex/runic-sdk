import { spawn, spawnSync } from "node:child_process";
import { createConnection } from "node:net";
import { fileURLToPath } from "node:url";
import { pause, waitFor } from "../shared/smoke.mjs";

const assembly = fileURLToPath(new URL("./bin/Debug/net10.0/NotesReactiveViews.dll", import.meta.url));
const retry = (condition, timeout = 30_000) => waitFor(condition, { timeout, interval: 100, label: "the IDE host" });

async function responds(url, timeout = 700) {
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(timeout) });
    await response.arrayBuffer();
    return { ok: response.ok, status: response.status };
  }
  catch (error) { return { ok: false, error: String(error) }; }
}

// A cold runner can take several seconds to serve the first proxied request
// (#40). Warm each readiness URL up for 20 seconds before treating it as failed.
async function respondsWithin(url, timeout = 20_000) {
  const started = Date.now();
  let result, attempts = 0;
  await retry(async () => (attempts++, result = await responds(url, 5000)).ok, timeout).catch(() => {});
  return { ...result, attempts, milliseconds: Date.now() - started };
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
      if (host.exitCode !== null || host.signalCode !== null) throw new Error(`Host exited:\n${output}`);
      const ready = output.match(/RUNIC_IDE_READY\|(http:\/\/127\.0\.0\.1:\d+\/)\|backend=(http:\/\/[^|\r\n]+)\|framework=/);
      frontend = ready?.[1];
      backend = ready?.[2];
      const angularPort = output.match(/RUNIC_IDE_ANGULAR_MAP_PROXY\|visible=\d+\|angular=(\d+)/)?.[1];
      if (angularPort) angularOrigin = `http://127.0.0.1:${angularPort}/`;
      return frontend && backend;
    }, 90_000).catch(error => {
      throw new Error(`${error.message}\nHost output:\n${output.slice(-16_384)}`, { cause: error });
    });
    for (const url of [frontend, new URL("/runic-cswebui.js", frontend)]) {
      const ready = await respondsWithin(url);
      if (!ready.ok) throw new Error(`Frontend did not become ready at ${url}: ${JSON.stringify(ready)}\nHost output:\n${output.slice(-16_384)}`);
    }
    // This serve-only probe may take WebUI's single client slot; the Debug host never does.
    const bridge = await respondsWithin(new URL("/webui.js", frontend));
    if (!bridge.ok) throw new Error(`The frontend Bridge proxy did not respond: ${JSON.stringify(bridge)}\nHost output:\n${output.slice(-16_384)}`);
    // Record the first proxied request's latency so a slow runner shows up before it fails.
    console.log(`REACTIVE_NOTES_IDE_FIRST_PROXY|${framework}|ms=${bridge.milliseconds}|attempts=${bridge.attempts}`);
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
      if (process.platform === "win32") {
        // Negative-PID process groups are POSIX-only. Reap the owned Windows
        // process tree before launching the next frontend probe.
        spawnSync("taskkill.exe", ["/PID", String(host.pid), "/T", "/F"], { timeout: 5000 });
      } else {
        try { process.kill(-host.pid, "SIGTERM"); } catch { /* Already stopped. */ }
        await pause(300);
        try { process.kill(-host.pid, "SIGKILL"); } catch { /* Already stopped. */ }
      }
      await retry(() => host.exitCode !== null || host.signalCode !== null, 5000);
    }
  }
}

await verify("angular");
await verify("angular", true);
await verify("svelte");
await verify("svelte", true);
