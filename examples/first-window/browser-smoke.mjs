import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

const dll = process.env.RUNIC_FIRST_WINDOW_DLL
  ?? fileURLToPath(new URL("./bin/Release/net10.0/FirstWindow.dll", import.meta.url));
const native = process.env.RUNIC_FIRST_WINDOW_EXECUTABLE;
// RUNIC_APPLICATION_SERVE_ONLY makes the unchanged example start its server
// without launching a browser and print RUNIC_APPLICATION_URL=<url>.
const host = spawn(native ?? "dotnet", native ? [] : [dll],
  { stdio: ["pipe", "pipe", "pipe"], env: { ...process.env, RUNIC_APPLICATION_SERVE_ONLY: "1" } });
let output = "", errors = "", browser;
host.stdout.on("data", chunk => { output += chunk; });
host.stderr.on("data", chunk => { errors += chunk; });
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`;
const retry = (condition, label) => waitFor(condition, { timeout: 20_000, label, detail });
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    return output.match(/https?:\/\/[^\s]+/)?.[0];
  }, "host URL");
  browser = await launchChromium(url, { profilePrefix: "runic-first-window-", timeout: 20_000 });
  const { evaluate } = browser;
  const state = () => evaluate(`({count: document.querySelector("#count")?.textContent,
    step: document.querySelector("#step")?.value,
    status: document.querySelector("#status")?.textContent})`);
  await retry(async () => (await state()).status === "Connected to the .NET ViewModel.", "initial connection");
  if ((await state()).count !== "0") throw new Error("Initial count was not zero.");
  await evaluate('document.querySelector("#increment").click()');
  await retry(async () => {
    const current = await state();
    return current.count === "1" && current.status === "Incremented.";
  }, "first command");
  await evaluate('document.querySelector("#step").focus(); document.querySelector("#step").value = "3"; document.querySelector("#step").blur()');
  await retry(async () => (await state()).status === "Step updated.", "writable property");
  await evaluate('document.querySelector("#increment").click()');
  await retry(async () => (await state()).count === "4", "updated command");
  await evaluate("location.reload()");
  await retry(async () => (await state()).count === "4" && (await state()).step === "3", "reload");
  console.log("FIRST_WINDOW_OK|snapshot|command|property|reload");
} finally {
  try { await browser?.close(); }
  finally {
    host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
