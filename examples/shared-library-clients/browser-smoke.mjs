// The shared library clients example in Chromium: the application's page renders its own state and the shared-ui
// package's about panel, whose client SharedUi's build generated, and both library ViewModels answer commands and a
// property write through the application's Bridges. The build output shows that the application generated no copy of
// the library's clients and copied no frontend of the library.
import { spawn } from "node:child_process";
import { existsSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

const example = fileURLToPath(new URL(".", import.meta.url));
const output = `${example}App/bin/Release/net10.0`;
const generated = readdirSync(`${example}App/Frontend/src/generated`);
for (const file of ["about.ts", "support.ts", "types.ts"])
  if (generated.includes(file)) throw new Error(`The application generated ${file}, which SharedUi owns.`);
if (!generated.includes("runic-client-modules.ts")) throw new Error("The application has no runic-client-modules.ts.");
const webRoot = readdirSync(`${output}/www`, { recursive: true }).map(String);
if (webRoot.some(file => /runic-clients|about-panel|shared-ui/.test(file)) || existsSync(`${output}/www/src`))
  throw new Error(`The application copied the library's frontend: ${webRoot.join(", ")}`);

const host = spawn("dotnet", [`${output}/SharedLibraryApp.dll`],
  { stdio: ["pipe", "pipe", "pipe"], env: { ...process.env, RUNIC_APPLICATION_SERVE_ONLY: "1" } });
let stdout = "", stderr = "", browser;
host.stdout.on("data", chunk => { stdout += chunk; });
host.stderr.on("data", chunk => { stderr += chunk; });
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `host stdout:\n${tail(stdout)}\nhost stderr:\n${tail(stderr)}`;
const retry = (condition, label) => waitFor(condition, { timeout: 20_000, label, detail });
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${stderr}`);
    return stdout.match(/https?:\/\/[^\s]+/)?.[0];
  }, "host URL");
  browser = await launchChromium(url, { profilePrefix: "runic-shared-library-", timeout: 20_000 });
  const { evaluate } = browser;
  const state = () => evaluate(`({
    title: document.querySelector("#title")?.textContent,
    heading: document.querySelector("#about h2")?.textContent,
    version: document.querySelector("#about .version")?.textContent,
    channel: document.querySelector("#about .channel")?.value,
    update: document.querySelector("#about .update-status")?.textContent,
    reports: document.querySelector("#reports")?.textContent,
    status: document.querySelector("#status")?.textContent })`);
  await retry(async () => (await state()).status === "Connected.", "connection");
  const first = await state();
  if (first.title !== "Acme Workbench" || first.heading !== "About Acme Workbench" || first.version !== "Version 2.4.0"
    || first.channel !== "Stable" || first.reports !== "0 reports sent")
    throw new Error(`First render: ${JSON.stringify(first)}`);
  // The shared component's command, then its property write and the command again.
  await evaluate('document.querySelector("#about .check").click()');
  await retry(async () => (await state()).update === "You have the latest version.", "the about panel's command");
  await evaluate(`(() => { const select = document.querySelector("#about .channel");
    select.value = "Preview"; select.dispatchEvent(new Event("change")); })()`);
  await evaluate('document.querySelector("#about .check").click()');
  await retry(async () => (await state()).update === "Version 2.5.0-preview.1 is available.", "the about panel's channel");
  // The application's own rendering of the library's second ViewModel.
  await evaluate('document.querySelector("#report").click()');
  await retry(async () => (await state()).reports === "1 reports sent", "the support command");
  console.log("SHARED_LIBRARY_CLIENTS_OK|no-copies|shared-component|command|property|app-rendered-library-client");
} finally {
  try { await browser?.close(); }
  finally {
    host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
