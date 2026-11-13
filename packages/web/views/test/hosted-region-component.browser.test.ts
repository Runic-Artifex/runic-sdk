import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { after, before, test } from "node:test";
import { chromium, type Browser, type Page } from "playwright-core";
import { serveLoopback, startComponentGuest, type ComponentGuest } from "./component-guest.js";

// <runic-hosted-region> in component mode (region-component/1), in Chromium: a host page on a loopback server with a
// fake producer behind its Bridge and a Content-Security-Policy, and a guest on another loopback port that serves a
// component module built on this package's `component` entry, with CORS for the host page alone, and admits its
// WebSocket with the token and the host page's Origin. Both are real loopback servers: Chromium's Local Network Access
// lets only a page that is itself on loopback reach a loopback address without a prompt.
const dist = new URL("../dist/", import.meta.url);
let hostOrigin = "";
const allowGuest = "script-src 'self' 'unsafe-inline' http://127.0.0.1:*; connect-src 'self' ws://127.0.0.1:*";

const hostPage = `<!doctype html><html><body style="margin:0">
<runic-hosted-region id="region" name="check">
  <p slot="fallback">The check is not running.</p>
</runic-hosted-region>
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
      return JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test-producer", version: "1.0.0" }, features: ["hosted-regions/1"] });
    return '{"ok":true}';
  },
  onMessage(listener) { listeners.add(listener); return () => listeners.delete(listener); },
  onConnectionChange(listener) { listener(true); return () => {}; },
};
const events = [];
document.getElementById("region").addEventListener("runicregionstatechange", event => events.push(event.detail.state));
defineHostedRegion();
globalThis.regionTest = { requests, events, push(message) { for (const listener of listeners) listener(JSON.stringify(message)); } };
</script></body></html>`;

// The guest's component entry: it renders what its guest answers and pushes, through the element's own instance.
const componentModule = `import { defineRunicComponent } from "./views/component.js";
const definition = defineRunicComponent({
  tag: "guest-check-1a2b3c4d",
  mount({ container, instance }) {
    container.innerHTML = '<input aria-label="Field"><output></output>';
    const output = container.querySelector("output");
    const bridge = instance.bridge;
    const stopConnection = bridge.onConnectionChange(connected => {
      if (connected) bridge.request(JSON.stringify({ kind: "probe", theme: getComputedStyle(container).getPropertyValue("--host-accent").trim() }))
        .then(text => { output.textContent = text; }, () => {});
    });
    const stopMessages = bridge.onMessage(message => { output.dataset.push = message; });
    return () => { stopConnection(); stopMessages(); };
  },
});
export const tag = definition.tag;
`;

const chromiumPath = process.env["PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH"];
let browser: Browser | undefined;

before(async () => {
  if (chromiumPath) browser = await chromium.launch({ executablePath: chromiumPath, headless: true });
});
after(async () => { await browser?.close(); });

interface Test {
  regionTest: { requests: Record<string, unknown>[]; events: string[]; push(message: unknown): void };
}

// The guest's files: its component module, and this package's build below it, to the host page's origin alone.
function guestFiles(path: string, origin: string | null): Response | Promise<Response> | undefined {
  if (origin !== hostOrigin) return new Response("Forbidden", { status: 403 });
  const headers = { "Content-Type": "text/javascript", "Access-Control-Allow-Origin": origin, "Vary": "Origin" };
  if (path === "/_runic_component/component.js") return new Response(componentModule, { headers });
  if (path.startsWith("/_runic_component/views/") && !path.includes(".."))
    return readFile(new URL(path.slice("/_runic_component/views/".length), dist), "utf8").then(text => new Response(text, { headers }));
  return undefined;
}

async function open(csp: string): Promise<{ readonly tab: Page; readonly errors: string[]; readonly guest: ComponentGuest; close(): Promise<void> }> {
  const host = serveLoopback(async request => {
    const path = new URL(request.url).pathname;
    if (path === "/") return new Response(hostPage, { headers: { "Content-Type": "text/html", "Content-Security-Policy": csp } });
    if (path === "/favicon.ico") return new Response(null, { status: 204 });
    if (path.startsWith("/dist/") && !path.includes(".."))
      return new Response(await readFile(new URL(path.slice("/dist/".length), dist), "utf8"), { headers: { "Content-Type": "text/javascript" } });
    return new Response("", { status: 404 });
  });
  hostOrigin = host.origin;
  const guest = startComponentGuest({
    origin: hostOrigin,
    answer: request => JSON.stringify({ ok: true, seen: JSON.parse(request) }),
    files: guestFiles,
  });
  const context = await browser!.newContext();
  const tab = await context.newPage();
  const errors: string[] = [];
  tab.on("pageerror", error => errors.push(String(error)));
  tab.on("console", message => { if (message.type() === "error") errors.push(message.text()); });
  lastTab = tab;
  lastErrors = errors;
  await tab.goto(`${hostOrigin}/`);
  await tab.waitForFunction(() => (globalThis as unknown as Test).regionTest.requests.some(request => request.kind === "hosted-region-declare"));
  return { tab, errors, guest, close: async () => { await context.close(); guest.stop(); host.stop(); } };
}

const push = (tab: Page, message: Record<string, unknown>) =>
  tab.evaluate(value => (globalThis as unknown as Test).regionTest.push(value), message);
const state = (tab: Page) => tab.evaluate(() => document.getElementById("region")!.getAttribute("state"));

async function until(condition: () => boolean | Promise<boolean>, message: string): Promise<void> {
  const deadline = Date.now() + 10_000;
  while (!await condition()) {
    if (Date.now() > deadline) assert.fail(`${message} (state ${await state(lastTab!)}, errors ${JSON.stringify(lastErrors)})`);
    await new Promise(resolve => setTimeout(resolve, 25));
  }
}

let lastTab: Page | undefined;
let lastErrors: string[] = [];
const skip = !chromiumPath;

test("Chromium: a component loads from its guest's origin, connects with its token and the page's Origin, and goes with its content", { timeout: 60_000, skip }, async () => {
  const { tab, errors, guest, close } = await open(allowGuest);
  try {
    await tab.addStyleTag({ content: "#region { --host-accent: rebeccapurple; }" });
    await push(tab, {
      kind: "hosted-region-content", name: "check", embedding: "component",
      module: `${guest.origin}/_runic_component/component.js`, url: guest.url, token: guest.token,
    });
    await until(async () => await state(tab) === "attached", "The region did not attach its component.");
    await until(() => guest.requests.length > 0, "The component sent no request to its guest.");
    // The component's own request, from the host page's document: Origin is the host page's, the token its subprotocol.
    assert.deepEqual(JSON.parse(guest.requests[0]!), { kind: "probe", theme: "rebeccapurple" });
    assert.equal(guest.upgrades[0]!.origin, hostOrigin);
    assert.equal(guest.upgrades[0]!.protocols, `runic-desktop.${guest.token}`);
    assert.equal(guest.upgrades[0]!.admitted, true);
    // The component is in the page's own document: it takes part in the page's focus order.
    await tab.keyboard.press("Tab");
    await until(async () => await tab.evaluate(() => document.getElementById("region")!.hasAttribute("focused")), "Tab did not move into the component.");

    await push(tab, { kind: "hosted-region-empty", name: "check", reason: "detached" });
    await until(async () => await state(tab) === "detached", "The region did not show its detached state.");
    await until(() => guest.open() === 0, "The component's connection stayed open after its content went.");
    assert.deepEqual(errors, []);
    const events = await tab.evaluate(() => (globalThis as unknown as Test).regionTest.events);
    assert.deepEqual(events, ["loading", "attached", "detached"]);
  } finally {
    await close();
  }
});

test("Chromium: a page whose Content-Security-Policy does not allow the guest leaves the region unsupported", { timeout: 60_000, skip }, async () => {
  const { tab, errors, guest, close } = await open("script-src 'self' 'unsafe-inline'; connect-src 'self'");
  try {
    await push(tab, {
      kind: "hosted-region-content", name: "check", embedding: "component",
      module: `${guest.origin}/_runic_component/component.js`, url: guest.url, token: guest.token,
    });
    await until(async () => await state(tab) === "unsupported", "The region did not report that it cannot show the component.");
    assert.equal(guest.upgrades.length, 0);
    assert.ok(errors.some(error => error.includes("Content-Security-Policy")), `No CSP hint among ${JSON.stringify(errors)}`);
  } finally {
    await close();
  }
});

test("Chromium: a component push without a token or with an address that is not a WebSocket is not shown", { timeout: 60_000, skip }, async () => {
  const { tab, guest, close } = await open(allowGuest);
  try {
    const before = (await tab.evaluate(() => (globalThis as unknown as Test).regionTest.events)).length;
    await push(tab, {
      kind: "hosted-region-content", name: "check", embedding: "component",
      module: `${guest.origin}/_runic_component/component.js`, url: guest.origin, token: guest.token,
    });
    await push(tab, {
      kind: "hosted-region-content", name: "check", embedding: "component",
      module: `${guest.origin}/_runic_component/component.js`, url: guest.url,
    });
    await tab.waitForTimeout(300);
    assert.equal((await tab.evaluate(() => (globalThis as unknown as Test).regionTest.events)).length, before);
    assert.equal(guest.upgrades.length, 0);
  } finally {
    await close();
  }
});
