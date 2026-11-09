import { spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

// The translated example end to end: the .NET build generated the catalog's C# API and the page's ESM from one
// runic.json. RUNIC_LOCALE starts the application in German; a switch on the page moves it to Arabic, right to left.
const dll = process.env.RUNIC_MULTI_WINDOW_DLL
  ?? fileURLToPath(new URL("./bin/Release/net10.0/MultiWindow.dll", import.meta.url));
// The settings file goes to a fresh folder, so a saved language of an earlier run does not apply.
const settings = mkdtempSync(join(tmpdir(), "runic-multi-window-smoke-"));
const host = spawn("dotnet", [dll], {
  stdio: ["pipe", "pipe", "pipe"],
  env: { ...process.env, RUNIC_APPLICATION_SERVE_ONLY: "1", RUNIC_LOCALE: "de", XDG_CONFIG_HOME: settings, APPDATA: settings },
});
let output = "", errors = "", browser;
host.stdout.on("data", chunk => { output += chunk; });
host.stderr.on("data", chunk => { errors += chunk; });
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`;
const retry = (condition, label) => waitFor(condition, { label, detail });
// Placeholders of a catalog with a right-to-left locale are isolated with FSI/LRI/RLI … PDI.
const isolates = /[⁦-⁩]/g;
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    return output.match(/RUNIC_APPLICATION_URL=(\S+)/)?.[1];
  }, "serve-only URL");
  browser = await launchChromium(url, { profilePrefix: "runic-multi-window-" });
  const { evaluate } = browser;
  const page = () => evaluate(`JSON.stringify({
    lang: document.documentElement.lang, dir: document.documentElement.dir,
    status: document.querySelector("#status")?.value, label: document.querySelector("#title-label")?.textContent,
    error: document.querySelector("#title-error")?.textContent })`).then(JSON.parse);
  const until = (predicate, label) => retry(async () => { const state = await page(); return predicate(state) ? state : undefined; }, label);

  // RUNIC_LOCALE=de: the ViewModel's status reference and the page's own text, in German.
  await until(state => state.lang === "de" && state.dir === "ltr" && state.status === "Bereit." && state.label === "Projekttitel",
    "German from RUNIC_LOCALE");
  // A validation message as a text reference, in the window's language.
  await evaluate(`(() => { const input = document.querySelector("#new-title"); input.value = "${"x".repeat(41)}";
    input.dispatchEvent(new Event("input", { bubbles: true })); })()`);
  await until(state => state.error === "Der Titel darf höchstens 40 Zeichen lang sein.", "German validation message");

  // The page asks the application to switch; every text follows, right to left.
  await evaluate(`(() => { const select = document.querySelector("#language"); select.value = "ar";
    select.dispatchEvent(new Event("change", { bubbles: true })); })()`);
  const arabic = await until(state => state.lang === "ar" && state.dir === "rtl" && state.status === "جاهز."
    && state.label === "عنوان المشروع" && state.error?.replace(isolates, "") === "يجب ألا يزيد العنوان عن 40 حرفًا.",
    "Arabic after the switch");
  if (arabic.error === arabic.error.replace(isolates, "")) throw new Error(`The Arabic placeholder is not isolated: ${arabic.error}`);

  host.stdin.end("\n");
  await retry(() => host.exitCode !== null, "host exit after a standard input line");
  if (host.exitCode !== 0) throw new Error(`Host exited with ${host.exitCode}: ${errors}`);
  console.log("MULTI_WINDOW_TRANSLATED_OK|runic-locale|references|validation|page-text|switch|rtl");
} finally {
  try { await browser?.close(); }
  finally {
    if (host.exitCode === null && !host.stdin.writableEnded) host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
    rmSync(settings, { recursive: true, force: true });
  }
}
