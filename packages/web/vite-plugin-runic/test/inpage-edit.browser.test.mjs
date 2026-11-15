// Editing a translation on the page (#957) under `vite dev` in a browser: select a text in Runic Inspect, open "Edit",
// change its MF2 source and save. The page shows the new text without a reload, the `.rmf2` file changes only in that
// message, and the card keeps its selection. A text that does not compile shows the diagnostics; a file that changed
// since the read shows its text and lets the user reapply. From another machine (this machine's LAN address), nothing is
// readable or editable without configuration, and everything but `/__runic/open` is with `RUNIC_DEV_ALLOW`.
//
// Unreleased Runic Translations is not installed in this repository, so a stand-in plays its dev server, as in
// inspect.browser.test.mjs: the text-change channel with `markers` (runic-translations-sdk#184), the in-page editing
// API with its replies, hash check, 409 and 422 (#182), a source-preserving write of the `.rmf2` entry, and the in-place
// update on the channel (#179). It admits clients as #182's plugin does, through RUNIC_DEV_ALLOW (read with this
// package's access list). inpage-edit.translations.browser.test.mjs runs the same flow against the real plugin when
// RUNIC_TRANSLATIONS_PLUGIN names its build.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { mkdir, mkdtemp, readFile, realpath, rm, symlink, writeFile } from "node:fs/promises";
import { networkInterfaces, tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { chromium, webkit } from "playwright-core";
import { createServer } from "vite";
import { runic } from "../dist/index.js";
import { resolveAccessList } from "../dist/access.js";
import { scaled } from "../../../../tests/support/timeouts.mjs";

const here = fileURLToPath(new URL("..", import.meta.url));
const packages = fileURLToPath(new URL("../../", import.meta.url));
const lan = Object.values(networkInterfaces()).flat().find((entry) => entry?.family === "IPv4" && !entry.internal)?.address;

function webkitExecutable() {
  if (process.env.PLAYWRIGHT_WEBKIT_EXECUTABLE_PATH) return process.env.PLAYWRIGHT_WEBKIT_EXECUTABLE_PATH;
  try {
    const path = webkit.executablePath();
    return existsSync(path) ? path : undefined;
  } catch {
    return undefined;
  }
}
const engines = {
  chromium: { type: chromium, executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH },
  webkit: { type: webkit, executablePath: webkitExecutable() },
};

const english = "# The application's messages\n\n# Shown above the counter.\nheading = Counter\nincrement_tip = Adds the step\nclicks =\n" +
  "  .input {$count :integer}\n  .match $count\n  one {{One click}}\n  * {{{$count} clicks}}\n";
const german = "heading = Zähler\nincrement_tip = Erhöht um den Schritt\n";

// The stand-in catalog module: the channel's markers as #184's dev client adds them, message functions that read the
// current texts, and the in-place update of #179 (new texts, then revision + 1 and the listeners).
const catalogModule = (texts) => String.raw`
const key = Symbol.for("runic.translations.text");
const text = (globalThis[key] ??= { version: 1, revision: 0, listeners: new Set() });
const pattern = /⁣([⁡⁢]{1,24})⁤/g;
const marked = [], ids = new Map();
let enabled = false;
const notify = () => { text.revision++; for (const listener of [...text.listeners]) listener(); };
text.markers ??= {
  version: 1,
  get enabled() { return enabled; },
  set(on) { if (on === enabled) return 0; enabled = on; notify(); return text.listeners.size; },
  marker(catalog, messageKey, locale) {
    const name = catalog + "/" + messageKey + "/" + locale;
    if (!ids.has(name)) { ids.set(name, marked.length); marked.push({ catalog, key: messageKey, locale }); }
    return "⁣" + ids.get(name).toString(2).replaceAll("0", "⁡").replaceAll("1", "⁢") + "⁤";
  },
  decode(value) {
    return [...String(value).matchAll(pattern)].map(match => marked[parseInt(match[1].replaceAll("⁡", "0").replaceAll("⁢", "1"), 2)]).filter(Boolean);
  },
  strip(value) { return value.replace(pattern, ""); },
};
const texts = ${JSON.stringify(texts)};
if (import.meta.hot) import.meta.hot.on("stand-in:texts", next => { Object.assign(texts, next); notify(); });
const message = messageKey => () => text.markers.enabled ? text.markers.marker("app", messageKey, "en") + texts[messageKey] : texts[messageKey];
export const m = { heading: message("heading"), increment_tip: message("increment_tip") };
`;

// The entries of an .rmf2 file: `key = text` on one line, or `key =` with the text indented below it.
function entries(source) {
  const lines = source.split("\n");
  const found = new Map();
  for (let index = 0; index < lines.length; index++) {
    const match = /^([A-Za-z_][\w.]*) =(?: (.*))?$/u.exec(lines[index]);
    if (!match) continue;
    let end = index + 1;
    if (match[2] === undefined) while (end < lines.length && lines[end].startsWith("  ")) end++;
    const text = match[2] ?? lines.slice(index + 1, end).map((line) => line.slice(2)).join("\n");
    found.set(match[1], { start: index, end, text, line: index + 1 });
  }
  return { lines, found };
}

const sha256 = (content) => `sha256:${createHash("sha256").update(content).digest("hex")}`;

// The stand-in for vite-plugin-runic-translations' dev server: GET and POST /__runic-translations/message.
function translationsStandIn() {
  let root;
  return {
    name: "translations-stand-in",
    configResolved(config) { root = config.root; },
    resolveId: (id) => id === "virtual:stand-in-translations" ? "\0stand-in-translations" : undefined,
    async load(id) {
      if (id !== "\0stand-in-translations") return undefined;
      const { found } = entries(await readFile(join(root, "translations", "en.rmf2"), "utf8"));
      return catalogModule({ heading: found.get("heading").text, increment_tip: found.get("increment_tip").text });
    },
    configureServer(server) {
      const access = resolveAccessList([]);
      const file = (locale) => join(server.config.root, "translations", `${locale}.rmf2`);
      const read = async (key) => {
        const locales = [];
        for (const locale of ["en", "de"]) {
          const content = await readFile(file(locale), "utf8");
          const entry = entries(content).found.get(key);
          if (locale === "en" && entry === undefined) return undefined;
          locales.push({
            locale, ...(locale === "de" ? { fallback: "en" } : {}), origin: entry ? "catalog" : "missing", text: entry?.text ?? null,
            ...(entry ? { source: { path: file(locale), line: entry.line, column: 1 } } : {}),
            file: { path: file(locale), exists: true, hash: sha256(content) }, editable: true,
            structure: key === "clicks" && entry
              ? { supported: true, inputs: [{ name: "count", type: "int64", function: "integer", declared: true }], selectors: ["count"], selectorFunctions: ["plural"],
                variants: [{ id: "0", keys: ["one"], pattern: "One click" }, { id: "1", keys: ["*"], pattern: "{$count} clicks" }], pluralCategories: ["one", "other"] }
              : { supported: true, inputs: [], selectors: [], selectorFunctions: [], variants: [{ id: "0", keys: [], pattern: entry?.text ?? "" }], pluralCategories: [] },
          });
        }
        return { catalog: "app", kind: "catalog", key, logicalKey: key, baseLocale: "en", content: "inline", structured: false,
          inputs: key === "clicks" ? [{ name: "count", type: "int64" }] : [], markup: [], locales };
      };
      server.middlewares.use("/__runic-translations/message", async (request, response) => {
        const send = (status, body) => {
          response.statusCode = status;
          response.setHeader("Content-Type", "application/json");
          response.end(JSON.stringify(body));
        };
        // As #182: the client's address (loopback, or RUNIC_DEV_ALLOW), then the page's origin.
        if (!(await access.allowsRequest(request))) {
          return send(403, { error: "forbidden", detail: "In-page translation editing accepts requests from loopback only, unless the client's host or network is allowed." });
        }
        const origin = request.headers.origin;
        if ((request.method === "POST" && origin === undefined) || (origin !== undefined && new URL(origin).host !== request.headers.host)) {
          return send(403, { error: "forbidden", detail: "Same-origin requests only." });
        }
        if (request.method === "GET") {
          const url = new URL(request.url, "http://localhost");
          const message = url.searchParams.get("catalog") === "app" ? await read(url.searchParams.get("key")) : undefined;
          return message ? send(200, { message }) : send(404, { error: "not-found", detail: "Unknown message." });
        }
        const chunks = [];
        for await (const chunk of request) chunks.push(chunk);
        const { key, locale, text, expectedHash } = JSON.parse(Buffer.concat(chunks).toString("utf8"));
        const path = file(locale);
        const content = await readFile(path, "utf8");
        if (sha256(content) !== expectedHash) {
          return send(409, { error: "conflict", detail: `${locale}.rmf2 changed since it was read.`, diagnostics: [], message: await read(key) });
        }
        const { lines, found } = entries(content);
        const entry = found.get(key);
        if ((text.match(/\{/gu) ?? []).length !== (text.match(/\}/gu) ?? []).length) {
          return send(422, { error: "invalid", detail: "The text does not compile.", diagnostics: [
            { code: "RTR0001", severity: "error", message: "Unbalanced braces in the message.", path, line: entry.line, column: key.length + 4 },
          ] });
        }
        // Source-preserving: only the entry's own lines change.
        const replacement = text.includes("\n") ? [`${key} =`, ...text.split("\n").map((line) => `  ${line}`)] : [`${key} = ${text}`];
        lines.splice(entry.start, entry.end - entry.start, ...replacement);
        await writeFile(path, lines.join("\n"));
        // The in-place update (#179): the page's message texts change, without a reload.
        if (locale === "en") server.ws.send({ type: "custom", event: "stand-in:texts", data: { [key]: text } });
        send(200, { written: [path], message: await read(key) });
      });
    },
  };
}

const counterPage = `import { useTextLocale } from "@runic-artifex/react";
import { m } from "virtual:stand-in-translations";

export function CounterPage() {
  useTextLocale();
  return <section>
    <h1 id="heading">{m.heading()}</h1>
    <button id="increment" title={m.increment_tip()}>Increment</button>
  </section>;
}
`;

async function link(root, name, target) {
  const path = join(root, "node_modules", ...name.split("/"));
  await mkdir(dirname(path), { recursive: true });
  await symlink(await realpath(target), path, "dir");
}

async function withPage({ engine = engines.chromium, host = "127.0.0.1", allow } = {}, run) {
  const root = await realpath(await mkdtemp(join(tmpdir(), "runic-inpage-edit-")));
  const previous = process.env.RUNIC_DEV_ALLOW;
  let server, browser;
  try {
    await mkdir(join(root, "translations"));
    await writeFile(join(root, "translations", "en.rmf2"), english);
    await writeFile(join(root, "translations", "de.rmf2"), german);
    await writeFile(join(root, "CounterPage.jsx"), counterPage);
    // The page's Translations runtime, as an application connects it; Views then follows the text-change channel.
    await writeFile(join(root, "main.jsx"), 'import { createRoot } from "react-dom/client";\nimport { configureTranslatedText } from "@runic-artifex/views";\n' +
      'import { CounterPage } from "./CounterPage.jsx";\nconfigureTranslatedText({ resolve: () => undefined, getLocale: () => "en" });\n' +
      'createRoot(document.getElementById("root")).render(<CounterPage />);\nglobalThis.editTest = { ready: true };\n');
    await writeFile(join(root, "index.html"), '<!doctype html><html><body><div id="root"></div><script type="module" src="/main.jsx"></script></body></html>');
    await writeFile(join(root, "Counter.cs"), "namespace Demo;\n");
    await writeFile(join(root, "sources.json"), JSON.stringify({ format: "runic-bridge-sources/1", viewModels: [], members: [] }));
    await link(root, "@runic-artifex/vite-plugin-runic", here);
    await link(root, "@runic-artifex/views", join(packages, "views"));
    await link(root, "@runic-artifex/react", join(packages, "react"));
    await link(root, "react", join(here, "node_modules", "react"));
    await link(root, "react-dom", join(here, "node_modules", "react-dom"));
    if (allow === undefined) delete process.env.RUNIC_DEV_ALLOW; else process.env.RUNIC_DEV_ALLOW = allow;
    server = await createServer({
      root, configFile: false, logLevel: "silent",
      plugins: [translationsStandIn(), runic({ sourceMap: "sources.json" })],
      resolve: { dedupe: ["react", "react-dom"] },
      server: { host, port: 0, fs: { strict: false } },
    });
    await server.listen();
    browser = await engine.type.launch({ executablePath: engine.executablePath, headless: true });
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", (error) => errors.push(String(error)));
    await page.goto(`http://${host === "0.0.0.0" ? lan : host}:${server.httpServer.address().port}`);
    await page.waitForFunction(() => globalThis.editTest?.ready === true, null, { timeout: scaled(30_000) });
    await run({ page, root, en: join(root, "translations", "en.rmf2"), de: join(root, "translations", "de.rmf2") });
    assert.deepEqual(errors, []);
  } finally {
    if (previous === undefined) delete process.env.RUNIC_DEV_ALLOW; else process.env.RUNIC_DEV_ALLOW = previous;
    await browser?.close();
    await server?.close();
    await rm(root, { recursive: true, force: true });
  }
}

const plain = (selector) => `document.querySelector(${JSON.stringify(selector)})?.textContent.replace(/[\\u2061-\\u2064]/gu, "")`;
const describe = (page, selector) => page.evaluate((selector) => globalThis[Symbol.for("runic.vite.inspect")].describe(document.querySelector(selector)), selector);
const focused = (page) => page.evaluate(() => {
  const active = document.querySelector("runic-inspect")?.shadowRoot?.activeElement;
  return active ? { tag: active.tagName.toLowerCase(), id: active.id, message: active.dataset?.message } : undefined;
});

async function selectHeading(page) {
  await page.keyboard.press("Alt+Shift+X");
  await page.waitForFunction(() => document.querySelector("#heading").textContent !== "Counter", null, { timeout: scaled(10_000) });
  const box = await page.locator("#heading").boundingBox();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  const card = page.locator("runic-inspect .card");
  await card.getByText("app:heading").first().waitFor({ timeout: scaled(10_000) });
  return card;
}

/** Fills the editor and saves with Ctrl+Enter (⌘+Enter would do on macOS). */
async function saveText(editor, text) {
  await editor.locator("textarea").fill(text);
  await editor.locator("textarea").press("Control+Enter");
}

for (const [name, engine] of Object.entries(engines)) test(`edit a translation on the page in ${name}`, {
  timeout: scaled(120_000), skip: !engine.executablePath,
}, async () => {
  await withPage({ engine }, async ({ page, en, de }) => {
    await page.evaluate(() => { globalThis.sameDocument = true; });
    const card = await selectHeading(page);

    // Edit, from the keyboard: the panel reads the message and focuses its text.
    const edit = card.getByRole("button", { name: "Edit app:heading" });
    await edit.focus();
    await page.keyboard.press("Enter");
    const editor = card.locator("section.editor");
    await page.waitForFunction(() => document.querySelector("runic-inspect").shadowRoot.querySelector("section.editor")?.dataset.status === "ready",
      null, { timeout: scaled(10_000) });
    assert.deepEqual(await focused(page), { tag: "textarea", id: "runic-inspect-edit-text", message: undefined });
    assert.equal(await editor.locator("textarea").inputValue(), "Counter");
    assert.deepEqual(await editor.locator("select option").evaluateAll((options) => options.map((option) => option.value)), ["en", "de"]);
    assert.match(await editor.textContent(), /translations\/en\.rmf2:4/u);

    // Save: the file changes in that line only, and the page shows the new text in place.
    await saveText(editor, "Clicks counter");
    await page.waitForFunction(`${plain("#heading")} === "Clicks counter"`, null, { timeout: scaled(10_000) });
    assert.equal(await page.evaluate(() => globalThis.sameDocument), true, "no reload");
    assert.equal(await readFile(en, "utf8"), english.replace("heading = Counter", "heading = Clicks counter"), "comments and other entries kept");
    assert.equal(await readFile(de, "utf8"), german);
    await editor.locator(".status", { hasText: "Saved to en.rmf2" }).waitFor({ timeout: scaled(10_000) });
    // The card keeps its selection and describes the element again.
    await card.getByText('"Clicks counter"').waitFor({ timeout: scaled(10_000) });
    assert.equal(await editor.isVisible(), true);

    // A text that does not compile: the diagnostics, and nothing written.
    await saveText(editor, "Clicks {");
    await editor.locator(".diagnostics li", { hasText: "RTR0001" }).waitFor({ timeout: scaled(10_000) });
    assert.match(await editor.locator(".diagnostics li").textContent(), /RTR0001 line 4:\d+: Unbalanced braces/u);
    assert.equal(await editor.getAttribute("data-status"), "invalid");
    assert.equal(await readFile(en, "utf8"), english.replace("heading = Counter", "heading = Clicks counter"));

    // A stale edit: the file was saved elsewhere since the read. The panel shows that text; Reapply writes over it.
    await editor.getByRole("button", { name: "Revert" }).click();
    await writeFile(en, english.replace("heading = Counter", "heading = Edited elsewhere"));
    await editor.locator("textarea").fill("Mine");
    await editor.getByRole("button", { name: "Save" }).click();
    const conflict = editor.getByRole("group", { name: "Conflict" });
    await conflict.waitFor({ timeout: scaled(10_000) });
    assert.match(await conflict.textContent(), /Edited elsewhere/u);
    assert.equal(await editor.locator("textarea").inputValue(), "Mine", "the edit is kept");
    assert.equal(await editor.getByRole("button", { name: "Save" }).isDisabled(), true, "decide first");
    await conflict.getByRole("button", { name: "Reapply my text" }).click();
    await editor.locator("textarea").press("Control+Enter");
    await page.waitForFunction(`${plain("#heading")} === "Mine"`, null, { timeout: scaled(10_000) });
    assert.equal(await readFile(en, "utf8"), english.replace("heading = Counter", "heading = Mine"));

    // Another locale: the page shows English, the German source changes.
    await editor.locator("select").selectOption("de");
    assert.equal(await editor.locator("textarea").inputValue(), "Zähler");
    await saveText(editor, "Zählwerk");
    await editor.locator(".status", { hasText: "Saved to de.rmf2" }).waitFor({ timeout: scaled(10_000) });
    assert.equal(await readFile(de, "utf8"), german.replace("Zähler", "Zählwerk"));
    assert.equal(await page.evaluate(plain("#heading")), "Mine");

    // Esc closes the panel and returns to its Edit button; Esc again leaves select mode with plain text.
    await editor.locator("textarea").press("Escape");
    assert.equal(await editor.isVisible(), false);
    assert.deepEqual(await focused(page), { tag: "button", id: "", message: "app:heading" });
    // The page's text changes again while the focus is there (here: a text-change notification, as after a save or an
    // edit elsewhere). The overview renders its controls anew, and the focus moves to the new Edit button.
    await page.evaluate(() => {
      document.querySelector("runic-inspect").shadowRoot.activeElement.dataset.before = "";
      for (const listener of [...globalThis[Symbol.for("runic.translations.text")].listeners]) listener();
    });
    await page.waitForFunction(() => {
      const button = document.querySelector("runic-inspect").shadowRoot.querySelector('button.edit[data-message="app:heading"]');
      return button !== null && !("before" in button.dataset);
    }, null, { timeout: scaled(10_000) });
    assert.deepEqual(await focused(page), { tag: "button", id: "", message: "app:heading" }, "the focus follows the overview's refresh");
    await page.keyboard.press("Escape");
    await page.waitForFunction(() => document.querySelector("#heading").textContent === "Mine", null, { timeout: scaled(10_000) });
    assert.equal(await page.evaluate(() => globalThis.sameDocument), true);
  });
});

test("from another machine: refused without configuration, editable with RUNIC_DEV_ALLOW, never opened", {
  timeout: scaled(120_000), skip: (!engines.chromium.executablePath || lan === undefined) && "needs Chromium and a non-loopback IPv4 address",
}, async () => {
  // Without configuration: the overview says why it has no source map, no message locations and no Edit.
  await withPage({ host: "0.0.0.0" }, async ({ page }) => {
    await page.evaluate(() => globalThis[Symbol.for("runic.vite.inspect")].toggle(true));
    await page.waitForFunction(() => document.querySelector("#heading").textContent !== "Counter", null, { timeout: scaled(10_000) });
    const description = await describe(page, "#heading");
    assert.deepEqual(description.messages.map(({ key, editable, location }) => ({ key, editable, location })), [{ key: "heading", editable: undefined, location: undefined }]);
    const notes = description.notes.join("\n");
    assert.match(notes, /refused this machine the Bridge source map \(403\)/u);
    assert.match(notes, /Translations dev server refused this machine its message API \(403\).*RUNIC_DEV_ALLOW/u);
    assert.match(notes, /opened from another machine/u);
    const box = await page.locator("#heading").boundingBox();
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    const card = page.locator("runic-inspect .card");
    await card.getByText("app:heading").first().waitFor({ timeout: scaled(10_000) });
    assert.equal(await card.getByRole("button", { name: "Edit app:heading" }).count(), 0);
  });

  // RUNIC_DEV_ALLOW names this machine: both plugins answer it; the editor saves; opening in the editor stays local.
  await withPage({ host: "0.0.0.0", allow: lan }, async ({ page, en }) => {
    const card = await selectHeading(page);
    const description = await describe(page, "#heading");
    assert.equal(description.messages[0].editable, true);
    const notes = description.notes.join("\n");
    assert.doesNotMatch(notes, /403/u);
    assert.match(notes, /opened from another machine/u);
    assert.equal(await card.locator("a[data-open]").count(), 0, "locations are shown, not linked");
    assert.match(await card.locator("[data-location=translations]").first().textContent(), /translations\/en\.rmf2:4/u);
    await card.getByRole("button", { name: "Edit app:heading" }).click();
    const editor = card.locator("section.editor");
    await saveText(editor, "Remote edit");
    await page.waitForFunction(`${plain("#heading")} === "Remote edit"`, null, { timeout: scaled(10_000) });
    assert.match(await readFile(en, "utf8"), /^heading = Remote edit$/mu);
    assert.equal(await page.evaluate((location) => globalThis[Symbol.for("runic.vite.inspect")].open(location), description.messages[0].location), false,
      "/__runic/open answers the development machine only");
  });
});
