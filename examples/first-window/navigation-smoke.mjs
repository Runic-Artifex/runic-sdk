import { spawn } from "node:child_process";
import { rm, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

// The window binds all WebUI events, so once the page connects, WebUI cancels its navigations until a Bridge
// call allows them and reports each as a navigation event instead (#196). The host carries out a same-origin
// one, such as a reload or a link to another page, and leaves a cross-origin one cancelled. A probe page with
// webui.js and runic-cswebui.js but no generated client makes no Bridge call, so every navigation below starts
// in that state. Chromium has the Navigation API through which WebUI cancels them.
const dll = process.env.RUNIC_FIRST_WINDOW_DLL
  ?? fileURLToPath(new URL("./bin/Release/net10.0/FirstWindow.dll", import.meta.url));
const probe = join(dirname(dll), "www", "navigation-probe.html");
await writeFile(probe, `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <title>Navigation probe</title>
    <script src="webui.js"></script>
    <script src="runic-cswebui.js"></script>
    <script>sessionStorage.setItem("loads", String(Number(sessionStorage.getItem("loads") ?? 0) + 1));</script>
  </head>
  <body>
    <a id="same" href="navigation-probe.html?link">Same origin</a>
    <a id="fragment" href="#section">Fragment</a>
    <a id="away" href="http://127.0.0.1:9/">Cross origin</a>
    <p id="section">Section</p>
  </body>
</html>
`);
const host = spawn("dotnet", [dll],
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
    return output.match(/RUNIC_APPLICATION_URL=(\S+)/)?.[1];
  }, "host URL");
  browser = await launchChromium(`${url}/navigation-probe.html`, { profilePrefix: "runic-navigation-", timeout: 20_000 });
  const { evaluate } = browser;
  const page = () => evaluate(`({ loads: sessionStorage.getItem("loads"), search: location.search,
    hash: location.hash, origin: performance.timeOrigin, connected: window.webui?.isConnected() ?? false })`);
  // Navigating ends the evaluating document, so its result may be lost.
  const navigate = async script => { await evaluate(script).catch(() => undefined); };
  if (!await evaluate(`"navigation" in globalThis`))
    throw new Error("This Chromium has no Navigation API, so WebUI would not cancel navigations.");
  const first = await retry(async () => { const state = await page(); return state.connected && state.loads === "1" && state; },
    "first connection");

  await navigate("location.reload()");
  const reloaded = await retry(async () => {
    const state = await page();
    return state.connected && state.loads === "2" && state.origin !== first.origin && state;
  }, "a reload before any Bridge call");

  await navigate('document.querySelector("#same").click()');
  await retry(async () => {
    const state = await page();
    return state.connected && state.loads === "3" && state.search === "?link" && state.origin !== reloaded.origin;
  }, "a same-origin link before any Bridge call");

  // A fragment stays in the document and keeps its connection.
  await navigate('document.querySelector("#fragment").click()');
  await retry(async () => {
    const state = await page();
    return state.connected && state.loads === "3" && state.hash === "#section";
  }, "a fragment link before any Bridge call");

  // A cross-origin navigation stays cancelled.
  await navigate('document.querySelector("#away").click()');
  await pause(1_000);
  const stayed = await page();
  if (!stayed.connected || stayed.loads !== "3" || stayed.search !== "?link")
    throw new Error(`A cross-origin link left the window: ${JSON.stringify(stayed)}`);

  // After a Bridge call allows navigation, a reload is the browser's own.
  await evaluate("window.__runicBridge.isConnected()");
  await navigate("location.reload()");
  await retry(async () => { const state = await page(); return state.connected && state.loads === "4"; },
    "a reload after a Bridge call");
  console.log("FIRST_WINDOW_NAVIGATION_OK|reload|same-origin-link|fragment|cross-origin-cancelled|reload-after-call");
} finally {
  try { await browser?.close(); }
  finally {
    host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
    await rm(probe, { force: true });
  }
}
