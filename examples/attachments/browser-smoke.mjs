// Runs the attachments example in serve-only mode and drives it from headless Chromium (Views feature blobs/1): the
// avatar the ViewModel draws loads in its <img>, Redraw replaces it with a new URL and releases the old one, a file
// uploads with progress, and its attachment downloads with the bytes the page sent.
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

const dll = process.env.RUNIC_ATTACHMENTS_DLL ?? fileURLToPath(new URL("./bin/Release/net10.0/Attachments.dll", import.meta.url));
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
  browser = await launchChromium(url, { profilePrefix: "runic-attachments-" });
  const { evaluate } = browser;
  const avatar = () => evaluate(`(() => { const image = document.querySelector("#avatar");
    return image?.complete && image.naturalWidth > 0 ? image.src : null; })()`);
  const first = await retry(avatar, "the avatar the ViewModel drew");
  check(new URL(first).pathname.includes("/_runic/blobs/"), "The avatar URL", first);

  await evaluate('document.querySelector("#redraw").click()');
  const second = await retry(async () => { const now = await avatar(); return now && now !== first ? now : null; }, "the redrawn avatar");
  // The browser keeps the old avatar in its cache (immutable); the host no longer serves it.
  const released = await evaluate(`fetch(${JSON.stringify(first)}, { cache: "no-store" }).then(response => response.status)`);
  check(released === 404, "The replaced avatar's URL", released);
  check(await evaluate(`fetch(${JSON.stringify(second)}).then(response => response.headers.get("Content-Type"))`) === "image/svg+xml",
    "The avatar's type", second);

  const content = "x".repeat(300_000);
  await evaluate(`globalThis.attachFile(new File([${JSON.stringify(content)}], "report.txt", { type: "text/plain" }))`);
  const link = await retry(() => evaluate(`(() => { const link = document.querySelector("#attachments a");
    return link ? { href: link.href, text: link.textContent, download: link.download } : null; })()`), "the attachment");
  check(link.text === "report.txt (300000 bytes)" && link.download === "report.txt", "The attachment", link);
  check(await evaluate('document.querySelector("#status").value') === "Attached report.txt", "The status",
    await evaluate('document.querySelector("#status").value'));
  const downloaded = await evaluate(`fetch(${JSON.stringify(link.href)}).then(async response => ({ disposition: response.headers.get("Content-Disposition"),
    size: (await response.text()).length }))`);
  check(downloaded.size === content.length && downloaded.disposition?.startsWith("attachment;"), "The download", downloaded);

  host.stdin.end("\n");
  await retry(() => host.exitCode !== null, "host exit after a standard input line");
  if (host.exitCode !== 0) throw new Error(`Host exited with ${host.exitCode}: ${errors}`);
  console.log("ATTACHMENTS_BROWSER_OK|avatar|redraw|release|upload|download");
} finally {
  try { await browser?.close(); }
  finally {
    if (host.exitCode === null && !host.stdin.writableEnded) host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
