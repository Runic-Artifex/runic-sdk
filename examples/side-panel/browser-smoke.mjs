// Runs the side panel example in serve-only mode and drives it from headless Chromium. A browser has no native window
// to grow, so OpenSidePanelAsync refuses with Unsupported and the page shows the debug panel as an overlay, which the
// toggle hides again. The panel's own route (#/debug) renders the debug tool against the same ViewModel, with its own
// title and a named complementary landmark.
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

const dll = process.env.RUNIC_SIDE_PANEL_DLL ?? fileURLToPath(new URL("./bin/Release/net10.0/SidePanelSample.dll", import.meta.url));
const host = spawn("dotnet", [dll], { stdio: ["pipe", "pipe", "pipe"], env: { ...process.env, RUNIC_APPLICATION_SERVE_ONLY: "1" } });
let output = "", errors = "", browser;
host.stdout.on("data", chunk => { output += chunk; });
host.stderr.on("data", chunk => { errors += chunk; });
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`;
const retry = (condition, label) => waitFor(condition, { label, detail });
const check = (condition, message, value) => {
  if (!condition) throw new Error(`${message}: ${JSON.stringify(value)}`);
};
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    return output.match(/RUNIC_APPLICATION_URL=(\S+)/)?.[1];
  }, "Desktop surface URL");
  browser = await launchChromium(url, { profilePrefix: "runic-side-panel-" });
  const { evaluate } = browser;
  const text = selector => evaluate(`document.querySelector(${JSON.stringify(selector)})?.textContent ?? null`);
  await retry(async () => await text("#quantity") === "1", "the order form");
  await evaluate('document.querySelector("#add").click()');
  await retry(async () => await text("#quantity") === "2", "the quantity after Add");

  // No native window: the panel is refused and the page shows it over its content.
  await evaluate('document.querySelector("#toggle-debug").click()');
  const overlay = await retry(() => evaluate(`(() => { const aside = document.querySelector("aside.debug.overlay");
    return aside ? { label: aside.getAttribute("aria-label"), notice: aside.querySelector(".notice")?.textContent,
      quantity: aside.querySelector("#debug-quantity")?.textContent,
      pressed: document.querySelector("#toggle-debug").getAttribute("aria-pressed") } : null; })()`), "the debug overlay");
  check(overlay.label === "Debug tools" && overlay.notice?.includes("(Unsupported)") && overlay.quantity === "2" && overlay.pressed === "true",
    "The overlay", overlay);
  await evaluate('document.querySelector("#debug-move").click()');
  await retry(() => evaluate('document.querySelector("aside.debug.overlay.left") !== null'), "the overlay on the left");
  await evaluate('document.querySelector("#debug-close").click()');
  await retry(() => evaluate('document.querySelector("aside.debug") === null'), "the overlay hidden");

  // The panel's route: the debug tool alone, bound to the same ViewModel.
  await evaluate('location.hash = "#/debug"; location.reload()');
  const panel = await retry(() => evaluate(`(() => { const aside = document.querySelector("aside.debug.page");
    return aside && aside.querySelector("#debug-quantity") ? { title: document.title, label: aside.getAttribute("aria-label"),
      quantity: aside.querySelector("#debug-quantity").textContent, log: aside.querySelectorAll("#debug-log li").length,
      form: document.querySelector("#quantity") !== null } : null; })()`), "the panel's page");
  check(panel.title === "Debug tools" && panel.label === "Debug tools" && panel.quantity === "2" && panel.log >= 3 && !panel.form,
    "The panel's page", panel);

  host.stdin.end("\n");
  await retry(() => host.exitCode !== null, "host exit after a standard input line");
  if (host.exitCode !== 0) throw new Error(`Host exited with ${host.exitCode}: ${errors}`);
  console.log("SIDE_PANEL_BROWSER_OK|overlay|unsupported|move|close|panel-route");
} finally {
  try { await browser?.close(); }
  finally {
    if (host.exitCode === null && !host.stdin.writableEnded) host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
