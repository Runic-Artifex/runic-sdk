import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

const dll = process.env.RUNIC_DESKTOP_VIEWS_DLL
  ?? fileURLToPath(new URL("./bin/Release/net10.0/FirstWindowDesktop.dll", import.meta.url));
// RUNIC_APPLICATION_SERVE_ONLY makes RunicDesktopHost serve the unchanged example's surface without a window
// and print RUNIC_APPLICATION_URL=<url>, as CS-WebUI does.
const host = spawn("dotnet", [dll],
  { stdio: ["pipe", "pipe", "pipe"], env: { ...process.env, RUNIC_APPLICATION_SERVE_ONLY: "1" } });
let output = "", errors = "", browser;
host.stdout.on("data", chunk => { output += chunk; });
host.stderr.on("data", chunk => { errors += chunk; });
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`;
const retry = (condition, label) => waitFor(condition, { label, detail });
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    return output.match(/RUNIC_APPLICATION_URL=(\S+)/)?.[1];
  }, "Desktop surface URL");
  browser = await launchChromium(url, { profilePrefix: "runic-desktop-views-" });
  const { evaluate } = browser;
  const count = () => evaluate('document.querySelector("#count")?.value');
  await retry(async () => (await count()) === "0", "initial generated snapshot");
  await evaluate('document.querySelector("#increment").click()');
  await retry(async () => (await count()) === "1", "ReactiveUI command");
  await evaluate("location.reload()");
  await retry(async () => (await count()) === "1", "reconnected snapshot");
  // The page learns that the host ended when its WebSocket drops, without making a call (#406).
  const states = () => evaluate("JSON.stringify(globalThis.connectionStates ?? null)");
  await evaluate("globalThis.connectionStates = []; window.__runicBridge.onConnectionChange(connected => connectionStates.push(connected)); true");
  await retry(async () => await states() === "[true]", "the connection listener");
  // A line on standard input ends serving: WaitForCloseAsync returns and the application exits by itself.
  const ended = Date.now();
  host.stdin.end("\n");
  await retry(() => host.exitCode !== null, "host exit after a standard input line");
  if (host.exitCode !== 0) throw new Error(`Host exited with ${host.exitCode}: ${errors}`);
  await retry(async () => await states() === "[true,false]", "the page seeing the ended host");
  const latency = Date.now() - ended;
  if (latency > 5_000) throw new Error(`The page saw the ended host only after ${latency} ms.`);
  console.log(`The page saw the ended host after ${latency} ms.`);
  console.log("DESKTOP_VIEWS_BROWSER_OK|snapshot|reactive-command|reload|serve-only-exit|host-ended");
} finally {
  try { await browser?.close(); }
  finally {
    if (host.exitCode === null && !host.stdin.writableEnded) host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
