import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { after, before, test } from "node:test";
import { chromium, type Browser, type BrowserContext, type Frame, type Page } from "playwright-core";

// <runic-hosted-region> in Chromium (hosted-regions/1): a host page on http://localhost with a fake producer behind
// its Bridge, and a guest page on another origin (http://127.0.0.1), so the frame is cross-origin as in a real host.
// The producer records the page's requests; the test pushes what the host decides.
const dist = new URL("../dist/", import.meta.url);
const hostOrigin = "http://localhost";
const guestOrigin = "http://127.0.0.1";

const hostPage = (features: string) => `<!doctype html><html><body style="margin:0">
<button id="before">Before</button>
<runic-hosted-region id="region" name="check" style="height:200px">
  <p slot="fallback" id="fallback">The check is not running.</p>
  <p slot="ended" id="ended">The check ended.</p>
</runic-hosted-region>
<button id="after">After</button>
<div style="height:3000px"></div>
<script type="module">
import { defineHostedRegion } from "/dist/experimental.js";
const listeners = new Set();
const requests = [];
globalThis.__runicBridge = {
  isConnected: () => true,
  async request(text) {
    const message = JSON.parse(text);
    requests.push(message);
    if (message.kind === "hello")
      return JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test-producer", version: "1.0.0" }, features: ${features} });
    return '{"ok":true}';
  },
  onMessage(listener) { listeners.add(listener); return () => listeners.delete(listener); },
  onConnectionChange(listener) { listener(true); return () => {}; },
};
const events = [];
const region = document.getElementById("region");
region.addEventListener("runicregionstatechange", event => events.push(event.detail.state));
region.addEventListener("runicregiontitlechange", event => events.push("title:" + event.detail.title));
defineHostedRegion();
globalThis.regionTest = {
  requests, events,
  push(message) { for (const listener of listeners) listener(JSON.stringify(message)); },
};
</script></body></html>`;

const guestPage = `<!doctype html><html><head><title>Guest</title></head><body>
<input id="first" aria-label="First"><button id="last">Last</button>
<script>
globalThis.loads = (globalThis.loads ?? 0) + 1;
// ?steal=<ms>: the page focuses its field by script after that long, without the user's activation.
const steal = Number(new URLSearchParams(location.search).get("steal"));
if (steal > 0) setTimeout(() => document.getElementById("first").focus(), steal);
</script></body></html>`;

const chromiumPath = process.env["PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH"];
let browser: Browser | undefined;

before(async () => {
  if (chromiumPath) browser = await chromium.launch({ executablePath: chromiumPath, headless: true });
});
after(async () => { await browser?.close(); });

interface Opened {
  readonly context: BrowserContext;
  readonly tab: Page;
  readonly errors: string[];
  readonly guestLoads: string[];
}

// Serves the host page, `dist` and the guest's pages without a server; each guest address counts its loads. `policy` is
// the host page's Content-Security-Policy.
async function open(features = `["hosted-regions/1"]`, declares = true, policy?: string): Promise<Opened> {
  const context = await browser!.newContext();
  const tab = await context.newPage();
  const errors: string[] = [];
  const guestLoads: string[] = [];
  tab.on("pageerror", error => errors.push(String(error)));
  await context.route(`${hostOrigin}/**`, async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/")
      return route.fulfill({ contentType: "text/html", body: hostPage(features), headers: policy ? { "content-security-policy": policy } : {} });
    if (path.startsWith("/dist/") && !path.includes(".."))
      return route.fulfill({ contentType: "text/javascript", body: await readFile(new URL(path.slice("/dist/".length), dist), "utf8") });
    return route.fulfill({ status: 404, body: "" });
  });
  await context.route(`${guestOrigin}/**`, route => {
    guestLoads.push(new URL(route.request().url()).pathname);
    return route.fulfill({ contentType: "text/html", body: guestPage });
  });
  await tab.goto(`${hostOrigin}/`);
  if (declares) await tab.waitForFunction(() => (globalThis as unknown as Test).regionTest.requests.some(request => request.kind === "hosted-region-declare"));
  return { context, tab, errors, guestLoads };
}

interface Test {
  regionTest: { requests: Record<string, unknown>[]; events: string[]; push(message: unknown): void };
}

const requests = (tab: Page) => tab.evaluate(() => (globalThis as unknown as Test).regionTest.requests.filter(request => request.kind !== "hello"));
const push = (tab: Page, message: Record<string, unknown>) =>
  tab.evaluate(value => (globalThis as unknown as Test).regionTest.push(value), message);
const state = (tab: Page) => tab.evaluate(() => document.getElementById("region")!.getAttribute("state"));

// The last state report, after `condition` holds for it.
async function report(tab: Page, condition: (report: Record<string, unknown>) => boolean, message: string): Promise<Record<string, unknown>> {
  const deadline = Date.now() + 5_000;
  for (;;) {
    const reports = (await requests(tab)).filter(request => request["kind"] === "hosted-region-state");
    const last = reports.at(-1);
    if (last && condition(last)) return last;
    if (Date.now() > deadline) assert.fail(`${message}: the reports were ${JSON.stringify(reports)}`);
    await tab.waitForTimeout(25);
  }
}

async function guestFrame(tab: Page): Promise<Frame> {
  for (let attempt = 0; attempt < 200; attempt++) {
    const frame = tab.frames().find(candidate => candidate.url().startsWith(guestOrigin));
    if (frame) { await frame.waitForLoadState(); return frame; }
    await tab.waitForTimeout(25);
  }
  return assert.fail("The guest's frame did not load.");
}

async function attach(tab: Page, src = `${guestOrigin}/_runic_frame/code-1`, title: string | null = "System check"): Promise<Frame> {
  await push(tab, { kind: "hosted-region-content", name: "check", embedding: "frame", src, title, allow: "" });
  const frame = await guestFrame(tab);
  await tab.waitForFunction(() => document.getElementById("region")!.getAttribute("state") === "attached");
  return frame;
}

const skip = !chromiumPath;

test("Chromium: the region declares itself, reports its size and place, and shows its fallback until a frame comes", { timeout: 60_000, skip }, async () => {
  const { context, tab, errors } = await open();
  try {
    const declared = (await requests(tab))[0];
    assert.deepEqual(declared, { kind: "hosted-region-declare", name: "check", sizing: "fill", session: declared!["session"] });
    const first = await report(tab, value => value["visible"] === true, "The region was not reported visible");
    assert.equal(first["focused"], false);
    assert.equal(first["focusSource"], null);
    assert.equal(first["height"], 200);
    assert.equal(first["scale"], 1);
    assert.ok((first["width"] as number) > 0 && (first["y"] as number) > 0, JSON.stringify(first));
    assert.equal(await state(tab), "waiting");
    assert.equal(await tab.evaluate(() => (document.getElementById("fallback") as HTMLElement).offsetHeight > 0), true, "The fallback is not shown.");
    // The element gives the page no frame and no shadow root.
    assert.deepEqual(await tab.evaluate(() => {
      const region = document.getElementById("region")!;
      return [region.shadowRoot, region.querySelector("iframe"), Object.keys(region).length];
    }), [null, null, 0]);

    // Created by script, as frameworks create it, with its attributes set as properties.
    assert.deepEqual(await tab.evaluate(() => {
      const created = document.createElement("runic-hosted-region");
      const before = created.getAttribute("state");
      created.name = "other";
      created.sizing = "content-height";
      created.label = "Other";
      document.body.append(created);
      const after = [created.getAttribute("name"), created.getAttribute("sizing"), created.getAttribute("label"), created.getAttribute("state")];
      created.remove();
      return [before, ...after];
    }), [null, "other", "content-height", "Other", "waiting"]);

    // Scrolling moves the region in the viewport: a new report with its place.
    await tab.evaluate(() => scrollTo(0, 50));
    await report(tab, value => value["y"] === (first["y"] as number) - 50, "The scroll was not reported");
    await tab.evaluate(() => scrollTo(0, 3000));
    await report(tab, value => value["visible"] === false, "The region scrolled out of view was not reported hidden");
    assert.deepEqual(errors, []);
  } finally { await context.close(); }
});

test("Chromium: a frame push shows the guest's page in a sandboxed cross-origin frame, loaded once", { timeout: 60_000, skip }, async () => {
  const { context, tab, errors, guestLoads } = await open();
  try {
    const frame = await attach(tab);
    const element = (await frame.frameElement()) as import("playwright-core").ElementHandle<HTMLIFrameElement>;
    assert.deepEqual(await element.evaluate(frame => [frame.getAttribute("sandbox"), frame.getAttribute("allow"), frame.getAttribute("referrerpolicy"),
      frame.title, frame.getAttribute("part")]), ["allow-scripts allow-same-origin allow-forms", "", "no-referrer", "System check", "frame"]);
    assert.deepEqual(await tab.evaluate(() => (globalThis as unknown as Test).regionTest.events), ["loading", "title:System check", "attached"]);
    assert.equal(await tab.evaluate(() => (document.getElementById("fallback") as HTMLElement).offsetHeight), 0, "The fallback still shows.");
    // The host page cannot reach into the guest's page.
    assert.equal(await element.evaluate(frame => { try { return frame.contentDocument === null; } catch { return true; } }), true);

    // The title follows hosted-region-update unless the page labels the region.
    await push(tab, { kind: "hosted-region-update", name: "check", title: "Checking" });
    assert.equal(await element.evaluate(frame => frame.title), "Checking");
    await tab.evaluate(() => document.getElementById("region")!.setAttribute("label", "System check step"));
    assert.equal(await element.evaluate(frame => frame.title), "System check step");

    // Moving the element removes the frame rather than loading its spent address again, and declares the region anew.
    const before = (await requests(tab)).length;
    await tab.evaluate(() => document.body.append(document.getElementById("region")!));
    await tab.waitForTimeout(200);
    const moved = (await requests(tab)).slice(before).map(request => request["kind"]);
    assert.deepEqual(moved.filter(kind => kind !== "hosted-region-state"), ["hosted-region-remove", "hosted-region-declare"]);
    assert.equal(await state(tab), "waiting");
    assert.deepEqual(guestLoads, ["/_runic_frame/code-1"], "The frame's address loaded more than once.");

    // ended shows the slot of that state, and the frame is gone.
    await attach(tab, `${guestOrigin}/_runic_frame/code-2`);
    await push(tab, { kind: "hosted-region-empty", name: "check", reason: "ended" });
    assert.equal(await state(tab), "ended");
    assert.equal(tab.frames().length, 1, "The frame stayed after the region was emptied.");
    assert.deepEqual(await tab.evaluate(() => [(document.getElementById("ended") as HTMLElement).offsetHeight > 0,
      (document.getElementById("fallback") as HTMLElement).offsetHeight]), [true, 0]);

    // An embedding this page does not know shows the fallback as unsupported.
    await push(tab, { kind: "hosted-region-content", name: "check", embedding: "native", handle: 1 });
    assert.equal(await state(tab), "unsupported");
    assert.deepEqual(errors, []);
  } finally { await context.close(); }
});

test("Chromium: the frame fills the region under a page policy without inline styles", { timeout: 60_000, skip }, async () => {
  // style-src falls back to 'self': a <style> element in the element's shadow root would be blocked, and the frame would
  // keep an iframe's default 300 x 150. The test page's own inline script is allowed; its inline style attributes are not.
  const { context, tab, errors } = await open(undefined, true,
    "default-src 'self'; script-src 'self' 'unsafe-inline'; frame-src http:; object-src 'none'; base-uri 'self'");
  try {
    await tab.evaluate(() => { document.getElementById("region")!.style.height = "240px"; });
    const frame = await attach(tab);
    const element = (await frame.frameElement()) as import("playwright-core").ElementHandle<HTMLIFrameElement>;
    const [width, height, regionWidth] = await element.evaluate(frame => [frame.offsetWidth, frame.offsetHeight,
      document.getElementById("region")!.clientWidth]);
    assert.deepEqual([width, height], [regionWidth, 240], "The frame does not fill the region.");
    assert.ok(width > 300, `The region is only ${width} px wide.`);
    assert.deepEqual(errors, []);
  } finally { await context.close(); }
});

test("Chromium: the frame's color-scheme follows the region's colorScheme, also under a page policy without inline styles", { timeout: 60_000, skip }, async () => {
  // style-src falls back to 'self': the element sets the property through CSSOM, which the policy does not block.
  const { context, tab, errors } = await open(undefined, true,
    "default-src 'self'; script-src 'self' 'unsafe-inline'; frame-src http:; object-src 'none'; base-uri 'self'");
  try {
    await push(tab, { kind: "hosted-region-content", name: "check", embedding: "frame", src: `${guestOrigin}/_runic_frame/code-1`, title: null,
      allow: "", colorScheme: "dark" });
    const frame = await guestFrame(tab);
    const element = (await frame.frameElement()) as import("playwright-core").ElementHandle<HTMLIFrameElement>;
    // The inline value, and what the engine uses: the plain keyword, not `only` (D066.10).
    const scheme = () => element.evaluate(frame => [frame.style.colorScheme, getComputedStyle(frame).colorScheme]);
    assert.deepEqual(await scheme(), ["dark", "dark"], "set before the frame loads");
    await push(tab, { kind: "hosted-region-update", name: "check", colorScheme: "light" });
    assert.deepEqual(await scheme(), ["light", "light"]);
    // An update without the member leaves it.
    await push(tab, { kind: "hosted-region-update", name: "check", title: "Checking" });
    assert.deepEqual(await scheme(), ["light", "light"]);
    // null, and a value the client does not know, clear it: the frame follows the host page.
    await push(tab, { kind: "hosted-region-update", name: "check", colorScheme: null });
    assert.deepEqual(await scheme(), ["", "normal"]);
    await push(tab, { kind: "hosted-region-update", name: "check", colorScheme: "dark" });
    await push(tab, { kind: "hosted-region-update", name: "check", colorScheme: "sepia" });
    assert.deepEqual(await scheme(), ["", "normal"]);
    // New content without the member does not keep the earlier content's scheme.
    await push(tab, { kind: "hosted-region-update", name: "check", colorScheme: "dark" });
    await push(tab, { kind: "hosted-region-content", name: "check", embedding: "frame", src: `${guestOrigin}/_runic_frame/code-2`, title: null, allow: "" });
    await tab.waitForFunction(() => document.getElementById("region")!.getAttribute("state") === "attached");
    const next = (await (await guestFrame(tab)).frameElement()) as import("playwright-core").ElementHandle<HTMLIFrameElement>;
    assert.deepEqual(await next.evaluate(frame => [frame.src.endsWith("code-2"), frame.style.colorScheme]), [true, ""]);
    assert.deepEqual(errors, []);
  } finally { await context.close(); }
});

test("Chromium: content-height applies the guest's height, and a host without the feature leaves the region unsupported", { timeout: 60_000, skip }, async () => {
  const { context, tab } = await open();
  try {
    await tab.evaluate(() => {
      const region = document.getElementById("region")!;
      region.style.height = "auto";
      region.setAttribute("sizing", "content-height");
    });
    await tab.waitForFunction(() => (globalThis as unknown as Test).regionTest.requests.some(request => request["sizing"] === "content-height"));
    const frame = await attach(tab);
    await push(tab, { kind: "hosted-region-update", name: "check", height: 321 });
    assert.equal(await (await frame.frameElement()).evaluate(frame => (frame as HTMLElement).getBoundingClientRect().height), 321);
    await report(tab, value => value["height"] === 321, "The new height was not reported");
  } finally { await context.close(); }

  const plain = await open(`["text-splice/1"]`, false);
  try {
    await plain.tab.waitForFunction(() => document.getElementById("region")!.getAttribute("state") === "unsupported");
    assert.deepEqual((await requests(plain.tab)).map(request => request["kind"]), []);
  } finally { await plain.context.close(); }
});

test("Chromium: the region reports how the focus entered it", { timeout: 60_000, skip }, async () => {
  const { context, tab } = await open();
  try {
    const frame = await attach(tab);
    // A click into the frame, with no input of the host page before it: the page gets user activation from the frame.
    await frame.click("#first");
    await report(tab, value => value["focused"] === true, "The click into the frame was not reported");
    assert.equal((await report(tab, value => value["focused"] === true, "")).focusSource, "pointer");

    // Out with Tab, then back in with Shift+Tab and Tab.
    await frame.focus("#last");
    await tab.keyboard.press("Tab");
    assert.equal(await tab.evaluate(() => document.activeElement?.id), "after");
    await report(tab, value => value["focused"] === false && value["focusSource"] === null, "Leaving the region was not reported");
    await tab.keyboard.press("Shift+Tab");
    assert.equal(await frame.evaluate(() => document.activeElement?.id), "last");
    assert.equal((await report(tab, value => value["focused"] === true, "Shift+Tab into the region was not reported")).focusSource, "shift-tab");
    await tab.focus("#before");
    await report(tab, value => value["focused"] === false, "Focusing the host page was not reported");
    await tab.keyboard.press("Tab");
    assert.equal(await frame.evaluate(() => document.activeElement?.id), "first");
    assert.equal((await report(tab, value => value["focused"] === true, "Tab into the region was not reported")).focusSource, "tab");

    // The host's focus push moves the focus only while the page holds none.
    await tab.focus("#before");
    await report(tab, value => value["focused"] === false, "Focusing the host page was not reported");
    await push(tab, { kind: "hosted-region-focus", name: "check" });
    await tab.waitForTimeout(100);
    assert.equal(await tab.evaluate(() => document.activeElement?.id), "before", "The host's focus push took the focus from the page.");
    await tab.evaluate(() => (document.activeElement as HTMLElement).blur());
    await push(tab, { kind: "hosted-region-focus", name: "check" });
    assert.equal((await report(tab, value => value["focused"] === true, "The host's focus push was not reported")).focusSource, "script");

  } finally { await context.close(); }
});

// Playwright's evaluate runs with a user gesture, which a frame passes to its ancestors, so the guest page focuses
// itself on a timer of its own.
test("Chromium: focus that the guest's script takes counts as script", { timeout: 60_000, skip }, async () => {
  const { context, tab } = await open();
  try {
    await attach(tab, `${guestOrigin}/_runic_frame/code-1?steal=5500`);
    await tab.evaluate(() => (document.getElementById("before") as HTMLElement).focus());
    await tab.waitForTimeout(6_000);
    const taken = await tab.evaluate(() => document.activeElement?.id);
    // Whether an engine lets a cross-origin frame take the focus by script is its own decision; if it does, the
    // region reports it as script.
    if (taken === "region")
      assert.equal((await report(tab, value => value["focused"] === true, "The guest's focus was not reported")).focusSource, "script");
    else assert.equal(taken, "before");
    console.log(`Chromium: a cross-origin frame's script focus ${taken === "region" ? "takes" : "does not take"} the focus from the host page.`);
  } finally { await context.close(); }
});
