// A browser (serve-only) host end to end in Chromium (host-protocol.md, Browser hosts; D059.10): this suite's executable
// runs serve-only (RegionBrowserHostProgram.cs) with a page that holds <runic-hosted-region name="check">, and shows the
// window of a guest (HostedGuestProgram's region-page mode) there. The browser already holds other local applications'
// cookies on every loopback name. Checks:
// - the region attaches, and the guest's page loads in a frame cross-site to the host page ([::1] for a 127.0.0.1 page);
// - frame admission works without cookies: no request of the frame carries one, no response sets one, and the frame
//   token leaves the frame's address;
// - the guest's title reaches the host page, and Tab moves into the frame and out of it;
// - a reload of the host page attaches the window again;
// - another local page can neither frame the guest's page (frame-ancestors) nor open its WebSocket.
// Run after building the suite: node tests/dotnet/Runic.Application.Views.Desktop.Tests/hosted-region-browser-smoke.mjs
// (CONFIGURATION=Debug for a Debug build).
import { spawn } from "node:child_process";
import { copyFileSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { chromium } from "playwright-core";
import { pause, tail, waitFor } from "../../../examples/shared/smoke.mjs";

// The build configuration, as eng/run.mjs reads it; CI builds Release.
const configuration = process.env.CONFIGURATION ?? "Release";
const dll = process.env.RUNIC_VIEWS_DESKTOP_TESTS_DLL
  ?? fileURLToPath(new URL(`./bin/${configuration}/net10.0/Runic.Application.Views.Desktop.Tests.dll`, import.meta.url));
const experimental = fileURLToPath(new URL("../../../packages/web/views/src/experimental.ts", import.meta.url));
// The Views host script, which an application's build copies next to its page.
const desktopViews = fileURLToPath(new URL("../../../packages/dotnet/Runic.Application.Views.Desktop/www/runic-desktop-views.js", import.meta.url));

// The host page: the element between two buttons, so Tab can move through the region.
const folder = mkdtempSync(join(tmpdir(), "runic-region-browser-host-"));
const { outputFiles: [script] } = await build({
  stdin: {
    contents: `import { defineHostedRegion } from ${JSON.stringify(experimental)};
      defineHostedRegion();
      const region = document.querySelector("runic-hosted-region");
      globalThis.__titles = [];
      region.addEventListener("runicregiontitlechange", event => globalThis.__titles.push(event.detail.title));`,
    resolveDir: folder,
    loader: "ts",
  },
  bundle: true,
  format: "esm",
  write: false,
  target: "es2022",
});
writeFileSync(join(folder, "app.js"), script.text);
copyFileSync(desktopViews, join(folder, "runic-desktop-views.js"));
writeFileSync(join(folder, "index.html"), `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>Browser host</title>
<script src="webui.js"></script><script src="runic-desktop-views.js"></script><script type="module" src="app.js"></script></head>
<body><button id="before">Before</button>
<runic-hosted-region name="check" style="display:block;width:640px;height:240px"><p slot="fallback">No check yet.</p></runic-hosted-region>
<button id="after">After</button></body></html>`);

const host = spawn("dotnet", [dll, "--region-browser-host", folder], { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "";
host.stdout.on("data", chunk => { output += chunk; });
host.stderr.on("data", chunk => { errors += chunk; });
// The pages' console and errors, and the region's state, for a failure's detail.
const pageLog = [];
let regionState = async () => "unknown";
const detail = async () => `region state: ${await regionState().catch(error => String(error))}\npage console:\n${tail(pageLog.join("\n"))}\n` +
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`;
const retry = (condition, label, timeout = 20_000) => waitFor(condition, { label, detail, timeout });

// Another local application's page, on a loopback port of its own.
const rogue = createServer((request, response) => {
  const target = new URL(request.url, "http://127.0.0.1").searchParams.get("frame") ?? "about:blank";
  response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
  response.end(`<!doctype html><title>Rogue</title><iframe id="guest" src="${target.replace(/"/g, "&quot;")}"></iframe>`);
});
await new Promise(resolve => rogue.listen(0, "127.0.0.1", resolve));
const rogueOrigin = `http://127.0.0.1:${rogue.address().port}`;

let browser;
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`The host exited with ${host.exitCode}.`);
    return output.match(/RUNIC_APPLICATION_URL=(\S+)/)?.[1];
  }, "serve-only URL");
  await retry(() => output.includes("RUNIC_TEST_GUEST_STARTED"), "the guest's start");
  const hostOrigin = new URL(url).origin;

  browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, headless: true });
  const context = await browser.newContext();
  // Other local applications' cookies, on every loopback name the frame could use; one named like Runic's own.
  const cookies = [];
  for (const site of ["http://127.0.0.1", "http://localhost", "http://[::1]"]) {
    cookies.push({ name: "other-app-strict", value: "1", url: `${site}/`, httpOnly: true, sameSite: "Strict" });
    cookies.push({ name: "other-app-lax", value: "1", url: `${site}/`, sameSite: "Lax" });
    cookies.push({ name: "webui_auth", value: "other", url: `${site}/`, httpOnly: true, sameSite: "Strict" });
  }
  await context.addCookies(cookies);

  const page = await context.newPage();
  page.on("console", message => pageLog.push(`[${message.type()}] ${message.text()}`));
  page.on("pageerror", error => pageLog.push(`[pageerror] ${error.message}`));
  regionState = () => page.evaluate(() => document.querySelector("runic-hosted-region")?.getAttribute("state") ?? "no element");
  // Every request the host page makes outside its own origin, with its headers.
  const crossOrigin = [];
  page.on("request", request => {
    if (new URL(request.url()).origin === hostOrigin) return;
    crossOrigin.push(request.allHeaders().then(headers => ({ url: request.url(), cookie: headers.cookie })));
  });
  const setCookies = [];
  page.on("response", response => {
    if (new URL(response.url()).origin !== hostOrigin && response.headers()["set-cookie"]) setCookies.push(response.url());
  });
  await page.goto(url);

  const state = () => page.evaluate(() => document.querySelector("runic-hosted-region")?.getAttribute("state"));
  const guestFrame = () => page.frames().find(frame => frame !== page.mainFrame() && frame.url().startsWith("http"));
  await retry(async () => await state() === "attached", "the region's attachment");
  const frame = await retry(() => guestFrame(), "the guest's frame");
  const frameUrl = new URL(frame.url());
  // Cross-site: the other loopback family, never the host page's own.
  if (frameUrl.hostname === new URL(hostOrigin).hostname)
    throw new Error(`The guest's frame is same-site to the host page: ${frameUrl.origin} in ${hostOrigin}.`);
  if (new URL(hostOrigin).hostname === "127.0.0.1" && frameUrl.hostname !== "[::1]")
    throw new Error(`The frame of a 127.0.0.1 page is served from ${frameUrl.origin}.`);
  // The token leaves the frame's address once the page read it.
  await retry(() => !guestFrame()?.url().includes("runic-frame="), "the token's removal from the frame's address");
  await retry(async () => (await frame.title()) === "Check", "the guest page's title");
  await retry(() => page.evaluate(() => globalThis.__titles.includes("Check")), "the title on the host page");

  // Frame admission needs no cookie: none of the browser's loopback cookies reached the guest, and it set none.
  const requests = await Promise.all(crossOrigin);
  const withCookies = requests.filter(request => request.cookie);
  if (withCookies.length > 0) throw new Error(`Cookies reached the guest: ${JSON.stringify(withCookies)}`);
  if (setCookies.length > 0) throw new Error(`The guest set cookies: ${setCookies.join(", ")}`);
  if (!requests.some(request => request.url.includes("/_runic_frame/"))) throw new Error("The frame launch address was not loaded.");

  // Tab from the host page into the frame, through it, and out to the host page again.
  await page.focus("#before");
  await page.keyboard.press("Tab");
  await retry(async () => (await frame.evaluate(() => document.activeElement?.id)) === "name", "Tab into the region");
  await page.keyboard.press("Tab");
  await retry(async () => (await frame.evaluate(() => document.activeElement?.id)) === "code", "Tab through the region");
  await page.keyboard.press("Tab");
  await retry(async () => (await page.evaluate(() => document.activeElement?.id)) === "after", "Tab out of the region");

  // Another local application's page cannot frame the guest's page, which allows only the host page as its embedder.
  const other = await context.newPage();
  const blocked = [];
  other.on("console", message => { if (message.text().includes("frame-ancestors")) blocked.push(message.text()); });
  await other.goto(`${rogueOrigin}/?frame=${encodeURIComponent(frameUrl.origin + frameUrl.pathname)}`);
  await retry(async () => blocked.length > 0 || other.frames().some(child => child.url().startsWith("chrome-error:")),
    "the refusal to frame the guest's page elsewhere");
  if (other.frames().some(child => child !== other.mainFrame() && child.url().startsWith(frameUrl.origin)) &&
      await other.frames()[1].title().catch(() => "") === "Check")
    throw new Error("Another local page framed the guest's page.");
  // Nor open its WebSocket, from its own origin.
  const socket = await other.evaluate(address => new Promise(resolve => {
    const ws = new WebSocket(address, ["runic-desktop.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"]);
    ws.onopen = () => { ws.close(); resolve("open"); };
    ws.onerror = () => resolve("refused");
    setTimeout(() => resolve("timeout"), 5_000);
  }), `ws://${frameUrl.host}${frameUrl.pathname.replace(/[^/]*$/, "")}_webui_ws_connect`);
  if (socket !== "refused") throw new Error(`Another local page's WebSocket to the guest was ${socket}.`);
  await other.close();

  // A reload of the host page detaches the region; its window attaches again, with a new frame launch address.
  await page.reload();
  await retry(async () => await state() === "attached", "the region's attachment after a reload");
  const again = await retry(() => guestFrame(), "the guest's frame after a reload");
  await retry(async () => (await again.title()) === "Check", "the guest page's title after a reload");

  host.stdin.end("\n");
  await retry(() => host.exitCode !== null, "the host's exit after a standard input line");
  if (host.exitCode !== 0) throw new Error(`The host exited with ${host.exitCode}.\n${detail()}`);
  console.log("REGION_BROWSER_HOST_OK|cross-site|no-cookies|title|tab|rogue-frame|rogue-socket|reload");
} finally {
  try { await browser?.close(); }
  finally {
    if (host.exitCode === null && !host.stdin.writableEnded) host.stdin.end("\n");
    await pause(500);
    if (host.exitCode === null) host.kill("SIGTERM");
    rogue.close();
    rmSync(folder, { recursive: true, force: true });
  }
}
