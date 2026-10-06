import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

const dll = process.env.RUNIC_DESKTOP_VIEWS_DLL
  ?? fileURLToPath(new URL("./bin/Release/net10.0/FirstWindowDesktop.dll", import.meta.url));
const host = spawn("dotnet", [dll, "--serve-only"], { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "", browser;
host.stdout.on("data", chunk => { output += chunk; });
host.stderr.on("data", chunk => { errors += chunk; });
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`;
const retry = (condition, label) => waitFor(condition, { label, detail });
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    return output.match(/https?:\/\/[^\s]+/)?.[0];
  }, "Desktop surface URL");
  browser = await launchChromium(url, { profilePrefix: "runic-desktop-views-" });
  const { evaluate } = browser;
  const count = () => evaluate('document.querySelector("#count")?.value');
  await retry(async () => (await count()) === "0", "initial generated snapshot");
  await evaluate('document.querySelector("#increment").click()');
  await retry(async () => (await count()) === "1", "ReactiveUI command");
  await evaluate("location.reload()");
  await retry(async () => (await count()) === "1", "reconnected snapshot");
  console.log("DESKTOP_VIEWS_BROWSER_OK|snapshot|reactive-command|reload");
} finally {
  try { await browser?.close(); }
  finally {
    host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
