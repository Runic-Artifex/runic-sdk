import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { chromium, type Browser } from "playwright-core";

// The in-page menu bar in Chromium with the mock host: real key events reach the controller, the page renders the
// state with its own markup, and a click and a shortcut run items through invoke.
const dist = new URL("../dist/", import.meta.url);
const fixture = new URL("../../../../specs/desktop/fixtures/menu-bar-page.json", import.meta.url);

const page = `<!doctype html><html><body>
<input id="editor" aria-label="Editor">
<nav id="bar" role="menubar"></nav>
<script type="module">
import { createMenuBarController, splitAccessKey } from "/dist/index.js";
import { installMockMenuBar } from "/dist/mock.js";
const { menus } = await (await fetch("/fixture.json")).json();
const host = installMockMenuBar(menus, { platform: "linux" });
const bar = createMenuBarController();
const requests = [];
bar.onRequest(request => requests.push(request.kind === "open" ? "open:" + request.menu.label : request.kind));
// The page keeps Ctrl+N for its editor.
document.getElementById("editor").addEventListener("keydown", event => { if (event.ctrlKey && event.key === "n") event.preventDefault(); });
function label(entry) {
  const [before, key, after] = splitAccessKey(entry);
  const span = document.createElement("span");
  span.append(before);
  if (key) { const marked = document.createElement(bar.current.accessKeyHints ? "u" : "span"); marked.textContent = key; span.append(marked); }
  span.append(after);
  return span;
}
function render() {
  const nav = document.getElementById("bar");
  nav.replaceChildren();
  if (bar.current.placement !== "page") return;
  for (const menu of bar.current.menus) {
    const group = document.createElement("div");
    group.setAttribute("role", "menu");
    group.append(label(menu));
    for (const entry of menu.entries) {
      if (entry.kind !== "item") continue;
      const button = document.createElement("button");
      button.setAttribute("role", "menuitem");
      button.dataset.command = entry.commandId;
      button.disabled = !entry.enabled;
      if (entry.ariaKeyShortcuts) button.setAttribute("aria-keyshortcuts", entry.ariaKeyShortcuts);
      button.append(label(entry));
      button.addEventListener("click", () => void bar.invoke(entry));
      group.append(button);
    }
    nav.append(group);
  }
}
bar.subscribe(render);
render();
globalThis.menuBarTest = { host, bar, requests };
</script></body></html>`;

const chromiumPath = process.env["PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH"];

/** Serves the page, the fixture and `dist` on `origin` without a server. */
async function open(browser: Browser, origin: string) {
  const tab = await browser.newPage();
  const errors: string[] = [];
  tab.on("pageerror", error => errors.push(String(error)));
  await tab.route(`${origin}/**`, async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/") return route.fulfill({ contentType: "text/html", body: page });
    if (path === "/fixture.json") return route.fulfill({ contentType: "application/json", body: await readFile(fixture, "utf8") });
    if (path.startsWith("/dist/") && !path.includes(".."))
      return route.fulfill({ contentType: "text/javascript", body: await readFile(new URL(path.slice("/dist/".length), dist), "utf8") });
    return route.fulfill({ status: 404, body: "" });
  });
  await tab.goto(`${origin}/`);
  return { tab, errors };
}

const ready = () => (globalThis as { menuBarTest?: { bar: { current: { status: string } } } }).menuBarTest?.bar.current.status === "ready";

// The runtime needs crypto.randomUUID (session and presentation ids), which a page has only in a secure context;
// the hosts serve secure contexts (custom schemes, http://localhost), so the page is served from localhost.
test("Chromium: the page renders the mock bar, and keys and clicks reach the controller", { timeout: 60_000, skip: !chromiumPath }, async () => {
  let browser: Browser | undefined;
  try {
    browser = await chromium.launch({ executablePath: chromiumPath!, headless: true });
    const { tab, errors } = await open(browser, "http://localhost");
    await tab.waitForFunction(ready);
    const invocations = () => tab.evaluate(() =>
      (globalThis as unknown as { menuBarTest: { host: { invocations: { commandId: string }[] } } }).menuBarTest.host.invocations.map(item => item.commandId));
    const requests = () => tab.evaluate(() => (globalThis as unknown as { menuBarTest: { requests: string[] } }).menuBarTest.requests);

    // The state renders, with aria-keyshortcuts and disabled items.
    assert.equal(await tab.locator("[data-command=file\\.save]").getAttribute("aria-keyshortcuts"), "Control+Shift+S");
    assert.equal(await tab.locator("[data-command=edit\\.delete]").isDisabled(), true);

    // A click runs the item; a shortcut runs it where the host has no key path; a key the page keeps does not.
    await tab.locator("[data-command=file\\.new]").click();
    await tab.keyboard.press("Control+Shift+S");
    await tab.waitForFunction(() =>
      (globalThis as unknown as { menuBarTest: { host: { invocations: unknown[] } } }).menuBarTest.host.invocations.length === 2);
    await tab.locator("#editor").focus();
    await tab.keyboard.press("Control+n");
    await tab.locator("#editor").blur();
    assert.deepEqual(await invocations(), ["file.new", "file.save"]);

    // Holding Alt underlines the access keys; an Alt tap, F10 and Alt+F are requests for the page's menubar.
    await tab.keyboard.down("Alt");
    await tab.locator("u").first().waitFor();
    await tab.keyboard.up("Alt");
    await tab.keyboard.press("Escape");
    assert.equal(await tab.locator("u").count(), 0);
    await tab.keyboard.press("F10");
    await tab.keyboard.press("Escape");
    await tab.keyboard.press("Alt+f");
    assert.deepEqual(await requests(), ["focus", "focus", "open:File"]);

    // A change on the host reaches the page.
    await tab.evaluate(() => {
      const { host } = (globalThis as unknown as { menuBarTest: { host: { menus: { entries: unknown[] }[]; update(change: unknown): void } } }).menuBarTest;
      host.update({ menus: host.menus.slice(1) });
    });
    await tab.locator("[data-command=file\\.new]").waitFor({ state: "detached" });
    assert.deepEqual(errors, []);
  } finally {
    await browser?.close();
  }
});

test("Chromium: outside a secure context the controller does not stay connecting without a request", { timeout: 60_000 }, async () => {
  let browser: Browser | undefined;
  try {
    browser = await chromium.launch({ executablePath: chromiumPath!, headless: true });
    const { tab } = await open(browser, "http://runic.test");
    assert.equal(await tab.evaluate(() => globalThis.isSecureContext), false);
    await tab.waitForFunction(() => (globalThis as { menuBarTest?: { bar: { current: { status: string } } } }).menuBarTest?.bar.current.status !== "connecting",
      undefined, { timeout: 10_000 });
  } finally {
    await browser?.close();
  }
});
