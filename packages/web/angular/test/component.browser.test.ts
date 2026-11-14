// @vitest-environment node
// Trusted component mode for Angular guests (runicComponentMount) in Chromium, on a host page whose
// Content-Security-Policy admits no inline styles: each element's Angular application renders in its shadow root with
// its styles as adopted stylesheets, changes nothing in the host document (no <head> or <html> change, no document
// stylesheet, no global listener, no history entry), follows its own Bridge, and leaves nothing behind when removed.
// Skipped without PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH (the dev shell and CI set it).
import { build } from "esbuild";
import { chromium, type Browser, type Page } from "playwright-core";
import { afterAll, beforeAll, describe, expect, test } from "vitest";

declare const process: { readonly env: Readonly<Record<string, string | undefined>> };
const chromiumPath = process.env["PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH"];

// JIT compilation needs 'unsafe-eval'; styles need nothing: adopted stylesheets are not subject to style-src.
const policy = "default-src 'none'; img-src data:; script-src 'self' 'unsafe-eval'; style-src 'self'";
const hostPage = `<!doctype html><html lang="en"><head><meta charset="utf-8"><link rel="icon" href="data:,">
<link rel="stylesheet" href="/host.css"><script src="/spy.js"></script><script type="module" src="/page.js"></script></head>
<body><span class="label" id="host-label">host</span><div id="slot"></div></body></html>`;
const hostStyles = ".label { color: rgb(0, 0, 255); } #slot { --host-accent: rgb(255, 128, 0); }";
// Records the global listeners and history entries that script on the page adds, from when the test marks a start.
const spy = `(() => {
  const listeners = [];
  const history = [];
  let marked = false;
  for (const [name, target] of [["window", window], ["document", document]]) {
    const add = target.addEventListener;
    const remove = target.removeEventListener;
    target.addEventListener = function (type, listener, options) {
      if (marked) listeners.push({ name: name + ":" + type, listener, capture: typeof options === "boolean" ? options : !!options?.capture });
      return add.call(this, type, listener, options);
    };
    target.removeEventListener = function (type, listener, options) {
      const capture = typeof options === "boolean" ? options : !!options?.capture;
      const index = listeners.findIndex(entry => entry.name === name + ":" + type && entry.listener === listener && entry.capture === capture);
      if (index >= 0) listeners.splice(index, 1);
      return remove.call(this, type, listener, options);
    };
  }
  for (const method of ["pushState", "replaceState"]) {
    const original = globalThis.history[method];
    globalThis.history[method] = function (...rest) { if (marked) history.push(method); return original.apply(this, rest); };
  }
  globalThis.documentSpy = { mark() { marked = true; }, listeners: () => listeners.map(entry => entry.name), history: () => history };
})();`;

type Snapshot = { head: string; html: string[]; sheets: number; adopted: number; body: string[] };
type Styles = { color: string; weight: string; decoration: string; border: string; styleElements: number; adopted: number };
interface ComponentTest {
  tag: string;
  snapshot(): Snapshot;
  mount(key: string, count: number): void;
  update(key: string, count: number): void;
  text(key: string): string;
  styles(key: string): Styles;
  click(key: string): Promise<string>;
  remove(key: string): Promise<{ instance: boolean; children: number; adopted: number }>;
  again(key: string): Promise<number>;
}
// The page's own globals, for the functions that page.evaluate runs there.
declare function testApi(): ComponentTest;
declare const documentSpy: { mark(): void; listeners(): string[]; history(): string[] };

describe.skipIf(!chromiumPath)("runicComponentMount in Chromium", () => {
  let browser: Browser;
  let script: string;
  beforeAll(async () => {
    const bundle = await build({
      entryPoints: [new URL("browser/component-page.ts", import.meta.url).pathname], bundle: true, write: false, format: "esm",
      platform: "browser", target: "es2022", define: { "process.env.NODE_ENV": '"production"' }, logLevel: "silent",
    });
    script = bundle.outputFiles[0]!.text;
    browser = await chromium.launch({ executablePath: chromiumPath!, headless: true });
  }, 60_000);
  afterAll(async () => { await browser?.close(); });

  async function open(): Promise<{ page: Page; errors: string[] }> {
    const page = await browser.newPage();
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(String(error)));
    page.on("console", message => { if (message.type() === "error" || message.type() === "warning") errors.push(message.text()); });
    // The runtime needs crypto.randomUUID, which a page has only in a secure context such as http://localhost.
    await page.route("http://localhost/**", route => {
      const path = new URL(route.request().url()).pathname;
      const files: Record<string, [string, string]> = {
        "/page.js": ["text/javascript", script], "/spy.js": ["text/javascript", spy], "/host.css": ["text/css", hostStyles],
      };
      const [contentType, body] = files[path] ?? ["text/html", hostPage];
      return route.fulfill({ contentType, body, headers: { "Content-Security-Policy": policy } });
    });
    await page.goto("http://localhost/");
    await page.waitForFunction(() => "testApi" in globalThis);
    return { page, errors };
  }

  test("renders each element in its shadow root with its styles, and changes nothing in the document", async () => {
    const { page, errors } = await open();
    const before = await page.evaluate(() => testApi().snapshot());
    await page.evaluate(() => documentSpy.mark());
    await page.evaluate(() => testApi().mount("alpha", 1));
    await page.waitForFunction(() => /count 1/.test(testApi().text("alpha")));
    // With one element on the page, the event handler's code outside the injection context reaches its instance (with
    // several elements of one bundle, such code has none: views' README, "Components").
    expect(await page.evaluate(() => testApi().click("alpha"))).toBe("alpha");
    await page.evaluate(() => testApi().mount("beta", 2));
    await page.waitForFunction(() => /count 2/.test(testApi().text("beta")));

    // Its own ViewModel: each element follows its Bridge.
    await page.evaluate(() => testApi().update("alpha", 5));
    await page.waitForFunction(() => /count 5/.test(testApi().text("alpha")));
    expect(await page.evaluate(() => testApi().text("beta"))).toMatch(/count 2/);

    // Styles: Emulated and None encapsulation, as adopted stylesheets after the bundle's (base and bundle CSS), with no
    // <style> or <link> element; the host page's custom property inherits, and its own .label keeps its colour.
    for (const key of ["alpha", "beta"]) {
      const styles = await page.evaluate(id => testApi().styles(id), key);
      expect(styles).toEqual({ color: "rgb(200, 0, 0)", weight: "700", decoration: "underline", border: "rgb(255, 128, 0)", styleElements: 0, adopted: 4 });
    }
    expect(await page.evaluate(() => getComputedStyle(document.getElementById("host-label")!).color)).toBe("rgb(0, 0, 255)");

    const during = await page.evaluate(() => testApi().snapshot());
    expect(during).toEqual(before);

    // Removing an element destroys its application and its stylesheets; the other keeps rendering.
    expect(await page.evaluate(() => testApi().remove("alpha"))).toEqual({ instance: true, children: 0, adopted: 2 });
    await page.evaluate(() => testApi().update("beta", 7));
    await page.waitForFunction(() => /count 7/.test(testApi().text("beta")));
    // Added again, it mounts a new application with one set of stylesheets.
    expect(await page.evaluate(() => testApi().again("alpha"))).toBe(4);
    expect(await page.evaluate(() => testApi().remove("alpha"))).toEqual({ instance: true, children: 0, adopted: 2 });
    expect(await page.evaluate(() => testApi().remove("beta"))).toEqual({ instance: true, children: 0, adopted: 2 });

    expect(await page.evaluate(() => testApi().snapshot())).toEqual(before);
    // Every global listener that mounting added (the Views runtime's per-instance connection listeners) is gone.
    expect(await page.evaluate(() => documentSpy.listeners())).toEqual([]);
    expect(await page.evaluate(() => documentSpy.history())).toEqual([]);
    expect(errors).toEqual([]);
    await page.close();
  }, 60_000);

  test("an element removed before its application started leaves nothing behind", async () => {
    const { page, errors } = await open();
    const before = await page.evaluate(() => testApi().snapshot());
    const result = await page.evaluate(async () => {
      testApi().mount("early", 1);
      return testApi().remove("early");
    });
    expect(result).toEqual({ instance: true, children: 0, adopted: 2 });
    await page.waitForTimeout(100);
    expect(await page.evaluate(() => testApi().snapshot())).toEqual(before);
    expect(errors).toEqual([]);
    await page.close();
  }, 60_000);
});
