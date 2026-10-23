// The in-page menu bar of a Notes frontend (RUNIC_WEB_ROOT: React, Vue, Svelte or Angular) in Chromium, driven
// with real keys and clicks. The built page is served as it is, with `installMockMenuBar` as the host: the
// example's CS-WebUI host has no in-page menu bar routes yet, so the menus come from the mock. Checks the menubar
// library's integration: click and invoke, Alt and F10 focus, arrows, Alt plus an access key, Escape, checked,
// radio and disabled items, a page-matched shortcut, and an axe-core scan of the open menubar.
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { extname, join, normalize, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { chromium } from "playwright-core";
import { scaled } from "../shared/smoke.mjs";

const root = process.env.RUNIC_WEB_ROOT;
if (!root) throw new Error("Set RUNIC_WEB_ROOT to a built Notes frontend.");
// A secure origin, as the host's own, for the page's crypto.randomUUID; nothing listens there.
const origin = "http://localhost:4173";
const mock = fileURLToPath(new URL("../../packages/web/views/src/mock.ts", import.meta.url));
// axe-core is a development dependency of the plain frontend, installed by its test step.
const axe = createRequire(new URL("./Frontend/package.json", import.meta.url)).resolve("axe-core/axe.min.js");

const item = (key, label, accessKey, commandId, extra = {}) => ({
  kind: "item", key, label, ...(accessKey ? { accessKey } : {}), accessKeyIndex: accessKey ? label.indexOf(accessKey) : -1,
  commandId, enabled: true, ...extra,
});
const menu = (key, label, accessKey, role, entries, extra = {}) => ({
  kind: "menu", key, label, accessKey, accessKeyIndex: label.indexOf(accessKey), role, enabled: true, entries, ...extra,
});
const shortcut = (key, text, aria) => ({ shortcut: `Primary+${key}`, shortcutText: text, ariaKeyShortcuts: aria });
// The bar Runic Desktop reports on Linux for File, Edit, View and Help (specs/desktop/menu-bar-page.md).
const bar = pane => [
  menu("1m", "File", "F", "file", [
    item("1m/0i", "New", "N", "file.new", shortcut("N", "Ctrl+N", "Control+N")),
    item("1m/1i", "Save", "S", "file.save", shortcut("S", "Ctrl+S", "Control+S")),
    { kind: "separator", key: "1m/2s" },
    menu("1m/3m", "Export", "E", "none", [item("1m/3m/0i", "PDF", "P", "file.export", { commandParameter: "pdf" })]),
  ]),
  menu("0m", "Edit", "E", "none", [
    item("0m/0i", "Undo", "U", "edit.undo", { enabled: false, ...shortcut("Z", "Ctrl+Z", "Control+Z") }),
    item("0m/1i", "Find", "F", "edit.find", shortcut("F", "Ctrl+F", "Control+F")),
  ]),
  menu("2m", "View", "V", "none", [
    item("2m/0r", "Editor", "E", "view.pane", { commandParameter: "editor", radioGroup: "view.pane", checked: pane === "editor" }),
    item("2m/1r", "Preview", "P", "view.pane", { commandParameter: "preview", radioGroup: "view.pane", checked: pane === "preview" }),
    { kind: "separator", key: "2m/2s" },
    item("2m/3c", "Sidebar", "S", "view.sidebar", { checked: true }),
  ]),
  menu("3m", "Help", "H", "help", [item("3m/0i", "About Notes", "A", "help.about")]),
];

const { outputFiles: [script] } = await build({
  stdin: {
    contents: `import { installMockMenuBar } from ${JSON.stringify(mock)};
      globalThis.__menuBars = ${JSON.stringify({ editor: bar("editor"), preview: bar("preview") })};
      globalThis.__menuBarHost = installMockMenuBar(globalThis.__menuBars.editor, { platform: "linux" });`,
    resolveDir: fileURLToPath(new URL(".", import.meta.url)),
    loader: "ts",
  },
  bundle: true,
  format: "iife",
  write: false,
  logLevel: "silent",
});

const types = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css", ".svg": "image/svg+xml", ".ico": "image/x-icon" };
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, headless: true });
let page;
try {
  page = await browser.newPage();
  page.setDefaultTimeout(scaled(10_000));
  await page.route(`${origin}/**`, async route => {
    const path = decodeURIComponent(new URL(route.request().url()).pathname);
    // The CS-WebUI scripts would install their own Bridge over the mock.
    if (/^\/(?:webui|runic-cswebui|runic-dev-reload)\.js$/.test(path)) return route.fulfill({ contentType: "text/javascript", body: "" });
    const file = normalize(join(root, path === "/" ? "index.html" : path));
    if (!file.startsWith(normalize(root) + sep)) return route.fulfill({ status: 404, body: "" });
    try { return route.fulfill({ contentType: types[extname(file)] ?? "application/octet-stream", body: await readFile(file) }); }
    catch { return route.fulfill({ status: 404, body: "" }); }
  });
  await page.addInitScript({ content: script.text });
  await page.goto(`${origin}/`);

  const menubar = page.getByRole("menubar");
  const trigger = name => menubar.getByRole("menuitem", { name, exact: true });
  const openMenu = page.getByRole("menu").first();
  const invocations = () => page.evaluate(() => globalThis.__menuBarHost.invocations.map(entry =>
    entry.commandParameter === undefined ? entry.commandId : `${entry.commandId}:${entry.commandParameter}`));
  const active = () => page.evaluate(() => {
    const element = document.activeElement;
    return { role: element?.getAttribute("role") ?? element?.tagName ?? "", text: element?.textContent?.replace(/\s+/g, " ").trim() ?? "" };
  });
  const expectActive = async (role, text, step) => {
    await page.waitForFunction(([role, text]) => {
      const element = document.activeElement;
      // Check and radio marks come before the label.
      return element?.getAttribute("role") === role && element.textContent?.replace(/^[^\p{L}]+/u, "").startsWith(text);
    }, [role, text], { timeout: scaled(5_000) }).catch(async () => {
      throw new Error(`${step}: focus is on ${JSON.stringify(await active())}, not the ${role} "${text}".`);
    });
  };
  const expectInvocations = async (expected, step) => {
    await page.waitForFunction(count => globalThis.__menuBarHost.invocations.length >= count, expected.length, { timeout: scaled(5_000) })
      .catch(() => undefined);
    // A second run of the same item would land within this pause.
    await page.waitForTimeout(150);
    const actual = await invocations();
    if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error(`${step}: invoked ${JSON.stringify(actual)}, expected ${JSON.stringify(expected)}.`);
  };
  const expectClosed = async step => {
    await page.getByRole("menu").first().waitFor({ state: "hidden", timeout: scaled(5_000) })
      .catch(() => { throw new Error(`${step}: a menu is still open.`); });
  };
  const blur = () => page.evaluate(() => { document.activeElement?.blur?.(); document.body.focus(); });

  // The bar renders the host's menus in the platform's order.
  await menubar.waitFor();
  const titles = (await menubar.getByRole("menuitem").allTextContents()).map(text => text.replace(/\s+/g, " ").trim());
  if (JSON.stringify(titles) !== JSON.stringify(["File", "Edit", "View", "Help"])) throw new Error(`The bar shows ${JSON.stringify(titles)}.`);
  const keyShortcuts = await menubar.getByRole("menuitem").evaluateAll(elements => elements.filter(element => element.hasAttribute("aria-keyshortcuts")).length);
  if (keyShortcuts !== 0) throw new Error("A menu trigger has aria-keyshortcuts.");

  // A click opens a menu, and a click on an item invokes it through the host and closes the menu.
  await trigger("File").click();
  await openMenu.getByRole("menuitem", { name: /^Save/ }).waitFor();
  const save = openMenu.getByRole("menuitem", { name: /^Save/ });
  if (await save.getAttribute("aria-keyshortcuts") !== "Control+S") throw new Error("Save has no aria-keyshortcuts=Control+S.");
  await save.click();
  await expectInvocations(["file.save"], "Clicking Save");
  await expectClosed("After Save");

  // An Alt tap focuses the first menu and underlines the access keys; arrows move between menus and into one;
  // Escape closes the menu and keeps focus on its trigger.
  await blur();
  await page.keyboard.press("Alt");
  await expectActive("menuitem", "File", "An Alt tap");
  await menubar.locator("u").first().waitFor({ timeout: scaled(5_000) })
    .catch(() => { throw new Error("The access keys are not underlined after an Alt tap."); });
  await page.keyboard.press("ArrowRight");
  await expectActive("menuitem", "Edit", "ArrowRight on File");
  await page.keyboard.press("ArrowDown");
  await openMenu.waitFor();
  const undo = openMenu.getByRole("menuitem", { name: /^Undo/ });
  if (await undo.getAttribute("aria-disabled") !== "true") throw new Error("Undo, which cannot run, is not aria-disabled.");
  await page.keyboard.press("Escape");
  await expectClosed("Escape in Edit");
  await expectActive("menuitem", "Edit", "Escape in Edit");
  // A disabled item does not run.
  await trigger("Edit").click();
  await undo.click({ force: true });
  await page.keyboard.press("Escape");
  await expectClosed("Escape after clicking Undo");
  await expectInvocations(["file.save"], "Clicking the disabled Undo");

  // F10 focuses the first menu as well.
  await blur();
  await page.keyboard.press("F10");
  await expectActive("menuitem", "File", "F10");
  await page.keyboard.press("Escape");

  // Alt plus a menu's access key opens it; check and radio items show the host's state, and Enter runs one.
  await blur();
  await page.keyboard.press("Alt+v");
  await openMenu.getByRole("menuitemradio", { name: /^Editor/ }).waitFor();
  const radio = name => openMenu.getByRole("menuitemradio", { name: new RegExp(`^${name}`) });
  if (await radio("Editor").getAttribute("aria-checked") !== "true" || await radio("Preview").getAttribute("aria-checked") !== "false")
    throw new Error("The View radio items do not show the Editor pane as checked.");
  if (await openMenu.getByRole("menuitemcheckbox", { name: /^Sidebar/ }).getAttribute("aria-checked") !== "true")
    throw new Error("The Sidebar check item is not checked.");
  // The open menu has focus on its first item, as after ArrowDown on its trigger.
  await expectActive("menuitemradio", "Editor", "Alt+V");
  await page.keyboard.press("ArrowDown");
  await expectActive("menuitemradio", "Preview", "ArrowDown in View");
  await page.keyboard.press("Enter");
  await expectInvocations(["file.save", "view.pane:preview"], "Enter on Preview");
  await expectClosed("After Preview");
  // The host changes the pane, as the ViewModel does; the bar follows.
  await page.evaluate(() => globalThis.__menuBarHost.update({ menus: globalThis.__menuBars.preview }));
  await blur();
  await page.keyboard.press("Alt+v");
  await radio("Preview").waitFor();
  await page.waitForFunction(() => document.querySelector('[role=menuitemradio][aria-checked=true]')?.textContent?.includes("Preview"), null,
    { timeout: scaled(5_000) }).catch(() => { throw new Error("The Preview radio item did not follow the host."); });

  // An axe-core scan of the menubar with its open menu.
  await page.addScriptTag({ path: axe });
  const violations = await page.evaluate(async () => {
    const result = await globalThis.axe.run({ include: [["[role=menubar]"], ["[role=menu]"]] }, { resultTypes: ["violations"] });
    return result.violations.map(violation => `${violation.id}: ${violation.nodes.map(node => node.target.join(" ")).join(", ")}`);
  });
  if (violations.length) throw new Error(`axe-core found violations in the open menubar:\n${violations.join("\n")}`);
  await page.keyboard.press("Escape");
  await expectClosed("Escape in View");

  // Primary+S runs Save once, matched by the page because this host has no key path.
  await blur();
  await page.keyboard.press("Control+s");
  await expectInvocations(["file.save", "view.pane:preview", "file.save"], "Control+S");

  console.log(`MENU_BAR_OK ${root}`);
} catch (error) {
  if (page) console.error(`Page:\n${(await page.locator("body").innerHTML().catch(() => "unavailable")).slice(0, 6000)}`);
  throw error;
} finally {
  await browser.close();
}
