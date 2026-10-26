// Runs the macOS window chrome script (packages/dotnet/Runic.Desktop/Internal/MacOsWindowChrome.js) in Chromium: it
// must report the drag and no-drag regions that app-region, -webkit-app-region and --app-region declare, from <style>
// elements, same-origin linked sheets and style attributes, and report again when the layout changes. WKWebView runs
// it on the macOS runner (WebViewSmoke --window-chrome), which also checks the native hit test.
// Needs PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH (the dev shell and CI's install-browser set it). Run with:
// bun run test tests/native/Runic.Desktop.WebViewSmoke/window-chrome-script.test.mjs
import { afterAll, beforeAll, beforeEach, describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { chromium } from "playwright-core";

const script = readFileSync(new URL("../../../packages/dotnet/Runic.Desktop/Internal/MacOsWindowChrome.js", import.meta.url), "utf8");
const executablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
if (!executablePath) throw new Error("Set PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH to a Chromium executable.");

const origin = "http://app.test";
let browser;
let page;
let files;
beforeAll(async () => { browser = await chromium.launch({ executablePath, headless: true }); });
afterAll(async () => { await browser?.close(); });
beforeEach(async () => {
  page = await browser.newPage({ viewport: { width: 800, height: 600 } });
  files = {};
  // The page's own origin, as the loopback server of a Runic window; other origins are not served.
  await page.route("**/*", route => {
    const url = new URL(route.request().url());
    const body = url.origin === origin ? files[url.pathname] : undefined;
    if (body === undefined) return route.fulfill({ status: 404, body: "" });
    return route.fulfill({ status: 200, contentType: url.pathname.endsWith(".css") ? "text/css" : "text/html", body });
  });
  // WebKit's message handler, then the script, before the page's own scripts as a WKUserScript at document start.
  await page.addInitScript(() => {
    window.reports = [];
    globalThis.webkit = { messageHandlers: { runicWindowChrome: { postMessage: message => window.reports.push(JSON.parse(message)) } } };
  });
  await page.addInitScript({ content: script });
});

async function open(body, head = "") {
  files["/index.html"] = `<!doctype html><html><head><style>body{margin:0}</style>${head}</head><body>${body}</body></html>`;
  await page.goto(`${origin}/index.html`);
}

// The latest report once it satisfies the check.
async function report(check = () => true) {
  const deadline = Date.now() + 5000;
  for (;;) {
    const reports = await page.evaluate(() => window.reports);
    const latest = reports.at(-1);
    if (latest && check(latest)) return latest;
    if (Date.now() > deadline) throw new Error(`No matching report: ${JSON.stringify(reports)}`);
    await page.waitForTimeout(25);
  }
}

const header = `<header id="bar" style="height:40px">Title<button id="close" style="position:absolute;left:700px;top:5px;width:90px;height:30px">Close</button></header>`;

describe("drag regions", () => {
  test("a page without regions reports none", async () => {
    await open("<main>Hello</main>");
    expect(await report()).toEqual({ w: 800, r: [] });
  });

  test("-webkit-app-region in a style element marks drag and no-drag regions in document order", async () => {
    await open(header, "<style>#bar{-webkit-app-region:drag} #close{-webkit-app-region:no-drag}</style>");
    expect(await report(latest => latest.r.length === 2)).toEqual({ w: 800, r: [[1, 0, 0, 800, 40], [0, 700, 5, 90, 30]] });
  });

  test("app-region, its custom property and group rules count too", async () => {
    await open(header, `<style>@media (min-width: 1px) { header { app-region: drag } }</style>
      <style>button { --app-region: no-drag }</style>`);
    expect((await report(latest => latest.r.length === 2)).r.map(region => region[0])).toEqual([1, 0]);
  });

  test("a same-origin linked sheet is copied", async () => {
    files["/app.css"] = "#bar { -webkit-app-region: drag; color: red } #close { app-region: no-drag }";
    await open(header, `<link rel="stylesheet" href="/app.css">`);
    expect((await report(latest => latest.r.length === 2)).r).toEqual([[1, 0, 0, 800, 40], [0, 700, 5, 90, 30]]);
    // Only --app-region rules are copied, so the copy changes nothing else.
    const copy = await page.evaluate(() => document.querySelector("style[data-runic-app-region]")?.textContent);
    expect(copy).not.toContain("color");
  });

  test("a style attribute marks its element", async () => {
    await open(`<div id="handle" style="height:20px; -webkit-app-region: drag">drag me</div>`);
    expect((await report(latest => latest.r.length === 1)).r).toEqual([[1, 0, 0, 800, 20]]);
  });

  test("hidden elements and unknown values report nothing", async () => {
    await open(`<div style="display:none; -webkit-app-region: drag">hidden</div><div style="height:10px; app-region: none">none</div>`);
    expect((await report()).r).toEqual([]);
  });

  test("regions follow classes, resizes and scrolling", async () => {
    await open(`<div id="top" style="height:30px"></div><div style="height:2000px"></div>`, "<style>.drag{-webkit-app-region:drag}</style>");
    await report();
    await page.evaluate(() => document.getElementById("top").classList.add("drag"));
    expect((await report(latest => latest.r.length === 1)).r).toEqual([[1, 0, 0, 800, 30]]);
    await page.evaluate(() => { document.getElementById("top").style.height = "50px"; });
    expect((await report(latest => latest.r[0]?.[4] === 50)).r).toEqual([[1, 0, 0, 800, 50]]);
    await page.evaluate(() => window.scrollTo(0, 20));
    expect((await report(latest => latest.r[0]?.[2] === -20)).r).toEqual([[1, 0, -20, 800, 50]]);
    await page.evaluate(() => document.getElementById("top").classList.remove("drag"));
    expect((await report(latest => latest.r.length === 0)).r).toEqual([]);
  });

  test("a style element added later is copied", async () => {
    await open(header);
    await report();
    await page.evaluate(() => {
      const style = document.createElement("style");
      style.textContent = "#bar{-webkit-app-region:drag}";
      document.head.append(style);
    });
    expect((await report(latest => latest.r.length === 1)).r).toEqual([[1, 0, 0, 800, 40]]);
  });

  test("the viewport width is reported for the page zoom", async () => {
    await page.setViewportSize({ width: 640, height: 480 });
    await open("<main></main>");
    expect((await report()).w).toBe(640);
  });
});
