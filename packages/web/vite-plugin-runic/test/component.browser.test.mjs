// The component build target (trusted component mode): two guests' components, built next to their pages, load from
// their own origins into one host page, in Chromium and WebKit, and share no tags, CSS or state. The guest servers here
// stand in for the guests' surfaces: they answer only the embedder's origin with CORS.
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdtemp, readdir, readFile, rm } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { extname, join, normalize, sep } from "node:path";
import { after, before, test } from "node:test";
import { fileURLToPath } from "node:url";
import { chromium, webkit } from "playwright-core";
import { createBuilder } from "vite";
import { runic } from "../dist/index.js";
import { scaled } from "../../../../tests/support/timeouts.mjs";

const fixtures = fileURLToPath(new URL("./fixtures/components/", import.meta.url));
const viewsDist = fileURLToPath(new URL("../../views/dist/", import.meta.url));
const types = { ".js": "text/javascript", ".json": "application/json", ".svg": "image/svg+xml" };

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

let output;
const servers = [];

async function build(name) {
  const builder = await createBuilder({
    root: join(fixtures, name),
    configFile: false,
    logLevel: "warn",
    build: { outDir: join(output, name), emptyOutDir: true },
    plugins: [runic({ devtools: false, component: { entry: "src/component.js" } })],
  });
  await builder.buildApp();
}

// Serves the files of `directory` below `prefix`, and `page` at `/`. With `embedder`, as a guest's surface serves a
// component: CORS for that origin only.
async function serve(directory, { prefix = "/", embedder, page } = {}) {
  const root = directory.endsWith(sep) ? directory : directory + sep;
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, "http://localhost");
    if (page !== undefined && url.pathname === "/") {
      response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" }).end(page);
      return;
    }
    const headers = { "Cache-Control": "no-store" };
    if (embedder !== undefined) {
      headers.Vary = "Origin";
      if (request.headers.origin === embedder) headers["Access-Control-Allow-Origin"] = embedder;
    }
    const file = url.pathname.startsWith(prefix) ? normalize(join(root, decodeURIComponent(url.pathname.slice(prefix.length)))) : "";
    if (!file.startsWith(root)) {
      response.writeHead(404, headers).end();
      return;
    }
    try {
      const body = await readFile(file);
      response.writeHead(200, { ...headers, "Content-Type": types[extname(file)] ?? "application/octet-stream" }).end(body);
    } catch {
      response.writeHead(404, headers).end();
    }
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  servers.push(server);
  return `http://127.0.0.1:${server.address().port}`;
}

// The host page loads its own copy of @runic-artifex/views (the views package's dist at /views/) for the mock Bridges.
const hostPage = `<!doctype html>
<html lang="en">
<head><link rel="icon" href="data:,"><style>.label { color: rgb(0, 0, 255); }</style></head>
<body>
<span class="label" id="host-label">host</span>
<div id="slot"></div>
<script type="module">
import { createMockBridge } from "/views/mock.js";
const guests = {};
globalThis.runicTest = {
  async mount(key, moduleUrl, count) {
    const module = await import(moduleUrl);
    const mock = createMockBridge();
    const counter = mock.view("counter", { state: { count } });
    // A Bridge with a name, so a component can tell whose instance its code reached.
    const bridge = {
      name: key,
      isConnected: () => mock.isConnected(),
      request: (message) => mock.request(message),
      onMessage: (listener) => mock.onMessage(listener),
      onConnectionChange: (listener) => mock.onConnectionChange(listener),
    };
    const element = document.createElement(module.tag);
    element.id = key;
    element.connect({ bridge });
    document.getElementById("slot").append(element);
    guests[key] = { counter, element };
    return module.tag;
  },
  update(key, count) { guests[key].counter.update({ count }); },
  text(key) { return guests[key].element.shadowRoot.textContent; },
  async remove(key) {
    const { element } = guests[key];
    element.remove();
    await new Promise((resolve) => setTimeout(resolve, 0));
    return { instance: element.instance === undefined, children: element.shadowRoot.childNodes.length };
  },
};
globalThis.runicReady = true;
</script>
</body>
</html>`;

before(async () => {
  output = await mkdtemp(join(tmpdir(), "runic-vite-component-"));
  await build("alpha");
  await build("beta");
}, { timeout: scaled(120_000) });

after(async () => {
  for (const server of servers) {
    server.closeAllConnections();
    server.close();
  }
  if (output) await rm(output, { recursive: true, force: true });
});

test("the component target builds a module, a manifest and a generated tag next to the page", async () => {
  const alpha = join(output, "alpha");
  const component = join(alpha, "_runic_component");
  assert.ok(existsSync(join(alpha, "index.html")), "the page is still built");
  const manifest = JSON.parse(await readFile(join(component, "runic-component.json"), "utf8"));
  assert.deepEqual(Object.keys(manifest), ["version", "name", "module", "tag"]);
  assert.equal(manifest.version, 1);
  assert.equal(manifest.module, "component.js");
  assert.equal(manifest.name, "fixtures-alpha-guest");
  assert.match(manifest.tag, /^fixtures-alpha-guest-[0-9a-f]{8}$/);
  const files = await readdir(component, { recursive: true });
  assert.ok(files.includes("component.js"));
  assert.ok(files.includes("runic-component-meta.js"));
  assert.ok(files.some((file) => /badge-.*\.svg$/.test(file)), "an asset above the inline limit is a file of its own");
  assert.deepEqual(files.filter((file) => file.endsWith(".css")), [], "the CSS is in the module, not a document stylesheet");
  const module = await readFile(join(component, "component.js"), "utf8");
  assert.match(module, /from\s*["']\.\/runic-component-meta\.js["']/);
  // The framework runtime is bundled: the component needs no import map of the host page.
  assert.doesNotMatch(module, /from\s*["'](react|react-dom|react-dom\/client|@runic-artifex\/[^"']+)["']/);
  const meta = await readFile(join(component, "runic-component-meta.js"), "utf8");
  assert.ok(meta.includes(JSON.stringify(manifest.tag)));
  assert.match(meta, /new URL\("assets\/[^"]+\.css", import\.meta\.url\)/);

  const beta = JSON.parse(await readFile(join(output, "beta", "_runic_component", "runic-component.json"), "utf8"));
  assert.match(beta.tag, /^fixtures-beta-guest-[0-9a-f]{8}$/);
});

for (const engine of engines) {
  test(`two components on one page share no tags, CSS or state (${engine.name})`, {
    timeout: scaled(90_000),
    skip: engine.executablePath === undefined ? `no ${engine.name} executable` : false,
  }, async () => {
    const host = await serve(viewsDist, { prefix: "/views/", page: hostPage });
    const alpha = await serve(join(output, "alpha"), { embedder: host });
    const beta = await serve(join(output, "beta"), { embedder: host });
    // The same build of alpha from a second guest's origin, as two running instances of one application.
    const again = await serve(join(output, "alpha"), { embedder: host });

    const browser = await engine.type.launch({ executablePath: engine.executablePath, headless: true });
    try {
      const page = await browser.newPage();
      const errors = [];
      page.on("pageerror", (error) => errors.push(String(error)));
      page.on("console", (message) => { if (message.type() === "error") errors.push(message.text()); });
      await page.goto(`${host}/`);
      await page.waitForFunction(() => globalThis.runicReady === true);
      const initial = await page.evaluate(() => ({ head: document.head.children.length, sheets: document.styleSheets.length }));

      const mount = (key, origin, count) => page.evaluate(([id, url, value]) => globalThis.runicTest.mount(id, url, value),
        [key, `${origin}/_runic_component/component.js`, count]);
      const text = (key) => page.evaluate(([id]) => globalThis.runicTest.text(id), [key]);
      const alphaTag = await mount("alpha", alpha, 1);
      const betaTag = await mount("beta", beta, 2);
      const againTag = await mount("again", again, 3);

      // Tags: per guest, generated at build time; the same build from another origin gets the next free name.
      assert.match(alphaTag, /^fixtures-alpha-guest-[0-9a-f]{8}$/);
      assert.match(betaTag, /^fixtures-beta-guest-[0-9a-f]{8}$/);
      assert.equal(againTag, `${alphaTag}-2`);

      // State: each element's Views connect through its own Bridge.
      await page.waitForFunction(() => ["alpha", "beta", "again"].every((key) => /\d/.test(globalThis.runicTest.text(key))));
      assert.match(await text("alpha"), /alpha 1/);
      assert.match(await text("beta"), /beta 2/);
      assert.match(await text("again"), /alpha 3/);
      await page.evaluate(() => globalThis.runicTest.update("alpha", 5));
      await page.waitForFunction(() => /alpha 5/.test(globalThis.runicTest.text("alpha")));
      assert.match(await text("beta"), /beta 2/);
      assert.match(await text("again"), /alpha 3/);

      // CSS: each shadow root has its bundle's styles, the host page keeps its own, and the document gets none.
      const styles = await page.evaluate(() => {
        const inside = (key, selector) => getComputedStyle(document.getElementById(key).shadowRoot.querySelector(selector));
        return {
          alpha: inside("alpha", ".label").color, alphaWeight: inside("alpha", ".label").fontWeight,
          beta: inside("beta", ".label").color, betaWeight: inside("beta", ".label").fontWeight,
          host: getComputedStyle(document.getElementById("host-label")).color,
          alphaBadge: inside("alpha", ".badge").backgroundImage, againBadge: inside("again", ".badge").backgroundImage,
          head: document.head.children.length, sheets: document.styleSheets.length,
          adopted: document.adoptedStyleSheets.length,
          lang: document.documentElement.getAttribute("lang"), dir: document.documentElement.getAttribute("dir"),
        };
      });
      assert.equal(styles.alpha, "rgb(200, 0, 0)");
      assert.equal(styles.beta, "rgb(0, 128, 0)");
      assert.equal(styles.betaWeight, "700");
      assert.notEqual(styles.alphaWeight, "700");
      assert.equal(styles.host, "rgb(0, 0, 255)");
      assert.equal(styles.head, initial.head);
      assert.equal(styles.sheets, initial.sheets);
      assert.equal(styles.adopted, 0);
      assert.equal(styles.lang, "en");
      assert.equal(styles.dir, null);
      // Assets are addressed at the origin each module was loaded from, and load from there.
      const badge = (origin) => new RegExp(`^url\\("?${origin}/_runic_component/assets/badge-[^"]+\\.svg"?\\)$`);
      assert.match(styles.alphaBadge, badge(alpha));
      assert.match(styles.againBadge, badge(again));
      await page.waitForFunction(([origin]) => performance.getEntriesByType("resource")
        .some((entry) => entry.name.startsWith(`${origin}/_runic_component/assets/badge-`)), [alpha]);

      // Code outside the provider and outside instance.run reaches its own element's instance, never the page's.
      for (const key of ["alpha", "beta", "again"]) {
        const owner = await page.evaluate(async ([id]) => {
          const root = document.getElementById(id).shadowRoot;
          root.querySelector("button").click();
          for (let attempt = 0; attempt < 100 && root.querySelector("output").textContent === ""; attempt++) {
            await new Promise((resolve) => setTimeout(resolve, 10));
          }
          return root.querySelector("output").textContent;
        }, [key]);
        assert.equal(owner, key);
      }

      // Removing an element ends its instance and its Views; the others keep theirs.
      assert.deepEqual(await page.evaluate(() => globalThis.runicTest.remove("alpha")), { instance: true, children: 0 });
      await page.evaluate(() => globalThis.runicTest.update("again", 7));
      await page.waitForFunction(() => /alpha 7/.test(globalThis.runicTest.text("again")));
      assert.match(await text("beta"), /beta 2/);

      assert.deepEqual(errors, []);
    } finally {
      await browser.close();
    }
  });
}
