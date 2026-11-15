// The widget's Angular component (Widget/Angular, `-p:WidgetFrontend=Angular`) in a host page, in Chromium and, when
// Playwright's WebKit is installed, WebKit: the component conformance checks of the `component` build target
// (packages/web/vite-plugin-runic/test/component.browser.test.mjs) for an Angular guest built with Vite and the Analog
// plugin. Build it first:
//
//   (cd examples/hosted-components/Widget/Angular && bun install --frozen-lockfile && bun run build)
//   node examples/hosted-components/angular-component-smoke.mjs
//
// The host page stands in for the dashboard: its Content-Security-Policy admits the guests' origin for scripts and no
// inline styles, it sets --dashboard-accent, and it connects each element to a mock Bridge of the widget's ViewModel.
// The guest servers stand in for the widget's surface: they serve _runic_component/ with CORS for the host's origin
// only. Two servers serve the same build, as the dashboard's two widget processes do.
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { readdir, readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { extname, join, normalize, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium, webkit } from "playwright-core";
import { scaled } from "../../tests/support/timeouts.mjs";

const dist = fileURLToPath(new URL("./Widget/Angular/dist/", import.meta.url));
const viewsDist = fileURLToPath(new URL("../../packages/web/views/dist/", import.meta.url));
const component = join(dist, "_runic_component");
if (!existsSync(join(component, "runic-component.json"))) {
  throw new Error(`${component} has no component build. Run bun run build in examples/hosted-components/Widget/Angular first.`);
}
const types = { ".js": "text/javascript", ".json": "application/json", ".css": "text/css" };

function webkitExecutable() {
  if (process.env.PLAYWRIGHT_WEBKIT_EXECUTABLE_PATH) return process.env.PLAYWRIGHT_WEBKIT_EXECUTABLE_PATH;
  try {
    const path = webkit.executablePath();
    return existsSync(path) ? path : undefined;
  } catch {
    return undefined;
  }
}
const engines = [
  { name: "chromium", type: chromium, executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH },
  { name: "webkit", type: webkit, executablePath: webkitExecutable() },
];

// 1. The build: a module, a manifest and a generated tag; Angular and the Runic packages bundled and linked ahead of
// time (the APF build of @runic-artifex/angular, no partial declarations left for a JIT compiler); no stylesheet file.
const manifest = JSON.parse(await readFile(join(component, "runic-component.json"), "utf8"));
assert.deepEqual(Object.keys(manifest), ["version", "name", "module", "tag"]);
assert.equal(manifest.module, "component.js");
assert.match(manifest.tag, /^service-widget-[0-9a-f]{8}$/);
const files = await readdir(component, { recursive: true });
assert.deepEqual(files.filter(file => file.endsWith(".css")), [], "the CSS is in the module, not a document stylesheet");
const module = await readFile(join(component, "component.js"), "utf8");
assert.doesNotMatch(module, /^\s*import[^;]*from\s*["'](?!\.\/)/m, "the component imports only its own chunks");
assert.doesNotMatch(module, /ɵɵngDeclare/, "the Angular packages are linked");
assert.doesNotMatch(module, /JIT compiler unavailable/, "the bundle has no development-mode Angular");

const servers = [];
async function listen(handler) {
  const server = createServer(handler);
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  servers.push(server);
  return `http://127.0.0.1:${server.address().port}`;
}

// Answers `response` with the file `path` names below `directory`, or 404.
async function sendFile(response, directory, path, headers = {}) {
  const root = directory.endsWith(sep) ? directory : directory + sep;
  const file = normalize(join(root, decodeURIComponent(path)));
  try {
    if (!file.startsWith(root)) throw new Error("outside");
    const body = await readFile(file);
    response.writeHead(200, { ...headers, "Content-Type": types[extname(file)] ?? "application/octet-stream" }).end(body);
  } catch {
    response.writeHead(404, headers).end();
  }
}

// A guest's surface: the widget's build, with CORS for the embedder's origin only.
const serveGuest = embedder => listen((request, response) => sendFile(response, dist, new URL(request.url, "http://localhost").pathname,
  { "Cache-Control": "no-store", Vary: "Origin", ...(request.headers.origin === embedder ? { "Access-Control-Allow-Origin": embedder } : {}) }));

// The host page, its script and styles, and its copy of @runic-artifex/views at /views/, under `policy`.
const serveHost = policy => listen((request, response) => {
  const path = new URL(request.url, "http://localhost").pathname;
  const headers = { "Cache-Control": "no-store", "Content-Security-Policy": policy };
  if (path === "/") response.writeHead(200, { ...headers, "Content-Type": "text/html; charset=utf-8" }).end(hostPage);
  else if (path === "/host.js") response.writeHead(200, { ...headers, "Content-Type": "text/javascript" }).end(hostScript);
  else if (path === "/host.css") response.writeHead(200, { ...headers, "Content-Type": "text/css" }).end(hostStyles);
  else if (path.startsWith("/views/")) void sendFile(response, viewsDist, path.slice("/views/".length), headers);
  else response.writeHead(404, headers).end();
});
// The dashboard's policy (Dashboard/Program.cs), with no inline styles: Angular's styles must not need them.
const policy = "default-src 'self'; img-src data:; script-src 'self' http://127.0.0.1:*; style-src 'self'; connect-src 'self' ws://127.0.0.1:*";

const hostStyles = `body { margin: 0; font-family: serif; } .label { color: rgb(0, 0, 255); }
#slot { --dashboard-accent: rgb(255, 128, 0); }`;
const hostPage = `<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><link rel="icon" href="data:,"><link rel="stylesheet" href="/host.css">
<script type="module" src="/host.js"></script></head>
<body><span class="label" id="host-label">host</span><div id="slot"></div></body>
</html>`;
// The host page's script: its own copy of @runic-artifex/views (at /views/) for the mock Bridges, and a record of
// the global listeners and history entries that script on the page adds.
const hostScript = `import { createMockBridge } from "/views/mock.js";
const listeners = [];
const history = [];
for (const [name, target] of [["window", window], ["document", document]]) {
  const add = target.addEventListener;
  const remove = target.removeEventListener;
  const capture = options => typeof options === "boolean" ? options : !!options?.capture;
  target.addEventListener = function (type, listener, options) {
    listeners.push({ name: name + ":" + type, listener, capture: capture(options) });
    return add.call(this, type, listener, options);
  };
  target.removeEventListener = function (type, listener, options) {
    const index = listeners.findIndex(entry => entry.name === name + ":" + type && entry.listener === listener && entry.capture === capture(options));
    if (index >= 0) listeners.splice(index, 1);
    return remove.call(this, type, listener, options);
  };
}
for (const method of ["pushState", "replaceState"]) {
  const original = globalThis.history[method];
  globalThis.history[method] = function (...rest) { history.push(method); return original.apply(this, rest); };
}
const guests = {};
globalThis.widgetTest = {
  snapshot: () => ({
    head: document.head.innerHTML, title: document.title,
    html: [...document.documentElement.attributes].map(attribute => attribute.name + "=" + attribute.value),
    sheets: document.styleSheets.length, adopted: document.adoptedStyleSheets.length,
    body: [...document.body.children].map(child => child.tagName.toLowerCase() + (child.id ? "#" + child.id : "")),
    listeners: listeners.map(entry => entry.name), history: [...history],
  }),
  async mount(key, moduleUrl, name) {
    const module = await import(moduleUrl);
    const mock = createMockBridge();
    const widget = mock.view("widget", {
      state: { name, processId: 4000 + Object.keys(guests).length, seconds: 1, pings: 0, canPing: true, isPingExecuting: false },
      commands: { ping: state => ({ ...state, pings: state.pings + 1 }) },
    });
    const element = document.createElement(module.tag);
    element.id = key;
    element.connect({ bridge: { isConnected: () => mock.isConnected(), request: message => mock.request(message),
      onMessage: listener => mock.onMessage(listener), onConnectionChange: listener => mock.onConnectionChange(listener) } });
    document.getElementById("slot").append(element);
    guests[key] = { element, widget };
    return module.tag;
  },
  update(key, seconds) { guests[key].widget.update({ seconds }); },
  text(key) { return [...guests[key].element.shadowRoot.querySelectorAll("h2, p, button")].map(element => element.textContent.trim()).join(" | "); },
  styles(key) {
    const root = guests[key].element.shadowRoot;
    const widget = getComputedStyle(root.querySelector(".widget"));
    return {
      accent: widget.borderInlineStartColor, accentWidth: widget.borderInlineStartWidth,
      heading: getComputedStyle(root.querySelector("h2")).fontSize, font: getComputedStyle(root.querySelector("h2")).fontFamily,
      elements: root.querySelectorAll("style, link").length, adopted: root.adoptedStyleSheets.length,
    };
  },
  ping(key) { guests[key].element.shadowRoot.querySelector("button").click(); },
  async remove(key) {
    const { element } = guests[key];
    element.remove();
    await new Promise(resolve => setTimeout(resolve, 0));
    return { instance: element.instance === undefined, children: element.shadowRoot.childNodes.length, adopted: element.shadowRoot.adoptedStyleSheets.length };
  },
};
globalThis.widgetReady = true;
`;

try {
  let ran = 0;
  for (const engine of engines) {
    if (engine.executablePath === undefined && engine.name === "webkit") {
      console.log("ANGULAR_COMPONENT_SKIPPED webkit: no Playwright WebKit executable");
      continue;
    }
    // Fresh origins for each engine.
    const host = await serveHost(policy);
    const indexer = await serveGuest(host);
    const sync = await serveGuest(host);

    const browser = await engine.type.launch({ executablePath: engine.executablePath, headless: true });
    try {
      const page = await browser.newPage();
      const errors = [];
      page.on("pageerror", error => errors.push(String(error)));
      page.on("console", message => { if (["error", "warning"].includes(message.type())) errors.push(message.text()); });
      await page.goto(`${host}/`);
      await page.waitForFunction(() => globalThis.widgetReady === true, null, { timeout: scaled(20_000) });
      const before = await page.evaluate(() => widgetTest.snapshot());
      const wait = (predicate, argument) => page.waitForFunction(predicate, argument, { timeout: scaled(20_000) });

      // 2. Tags: the build's, and the same build from the second widget's origin gets the next free name.
      const indexerTag = await page.evaluate(([url]) => widgetTest.mount("indexer", url, "Indexer"), [`${indexer}/_runic_component/component.js`]);
      const syncTag = await page.evaluate(([url]) => widgetTest.mount("sync", url, "Sync"), [`${sync}/_runic_component/component.js`]);
      assert.equal(indexerTag, manifest.tag);
      assert.equal(syncTag, `${manifest.tag}-2`);

      // 3. It renders with its styles: Angular's component styles as adopted stylesheets of each shadow root (after the
      // element's base sheet), no <style> or <link>; the host page's --dashboard-accent inherits, its fonts do not
      // reach in through selectors, and its own .label keeps its colour.
      await wait(() => ["indexer", "sync"].every(key => /Ping \(0\)/.test(widgetTest.text(key))));
      assert.match(await page.evaluate(() => widgetTest.text("indexer")), /^Indexer \| Process 4000, running for 1 s \| Ping \(0\)$/);
      assert.match(await page.evaluate(() => widgetTest.text("sync")), /^Sync \| Process 4001, running for 1 s \| Ping \(0\)$/);
      for (const key of ["indexer", "sync"]) {
        const styles = await page.evaluate(([id]) => widgetTest.styles(id), [key]);
        assert.deepEqual({ ...styles, font: styles.font.includes("system-ui") },
          { accent: "rgb(255, 128, 0)", accentWidth: "4px", heading: "16px", font: true, elements: 0, adopted: 2 }, `${key} styles`);
      }
      assert.equal(await page.evaluate(() => getComputedStyle(document.getElementById("host-label")).color), "rgb(0, 0, 255)");

      // 4. It updates from its own ViewModel, and its commands reach that ViewModel only.
      await page.evaluate(() => widgetTest.update("indexer", 42));
      await wait(() => /running for 42 s/.test(widgetTest.text("indexer")));
      await page.evaluate(() => widgetTest.ping("sync"));
      await wait(() => /Ping \(1\)/.test(widgetTest.text("sync")));
      assert.match(await page.evaluate(() => widgetTest.text("indexer")), /Ping \(0\)/);
      assert.match(await page.evaluate(() => widgetTest.text("sync")), /running for 1 s/);

      // 5. Nothing in the host document changed while the widgets ran: <head>, <html lang dir>, title, stylesheets,
      // and no history entry. (The Views runtime's connection listeners are the elements' and end with them.)
      const during = await page.evaluate(() => widgetTest.snapshot());
      assert.deepEqual({ ...during, listeners: [] }, { ...before, listeners: [] });

      // 6. Teardown: removing an element ends its application, its instance and its stylesheets; the other keeps
      // rendering. With both removed the document is as it was, without a listener the widgets added.
      assert.deepEqual(await page.evaluate(() => widgetTest.remove("indexer")), { instance: true, children: 0, adopted: 1 });
      await page.evaluate(() => widgetTest.update("sync", 7));
      await wait(() => /running for 7 s/.test(widgetTest.text("sync")));
      assert.deepEqual(await page.evaluate(() => widgetTest.remove("sync")), { instance: true, children: 0, adopted: 1 });
      assert.deepEqual(await page.evaluate(() => widgetTest.snapshot()), before);

      assert.deepEqual(errors, []);
      ran++;
      console.log(`ANGULAR_COMPONENT_OK ${engine.name} ${indexerTag} ${syncTag}`);
    } finally {
      await browser.close();
    }
  }
  assert.ok(ran > 0, "no browser ran");
} finally {
  for (const server of servers) {
    server.closeAllConnections();
    server.close();
  }
}
