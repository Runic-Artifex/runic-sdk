// Editing a translation on the page (#957) against the real Runic Translations dev server: its Vite plugin (0.12, with
// the in-page editing API of runic-translations-sdk#182, the markers of #184 and RUNIC_DEV_ALLOW of #186) and its
// compiler (`runic-translations serve`). A React page shows `m.heading()`; Runic Inspect selects it, edits and saves.
// The page shows the new text without a reload, the `.rmf2` file changes only in that entry, a stale edit gives 409,
// and a client of another machine is refused without configuration and edits with RUNIC_DEV_ALLOW.
//
// Opt-in, as this repository does not install unreleased Translations packages (CI runs the stand-in of
// inpage-edit.browser.test.mjs):
//   RUNIC_TRANSLATIONS_PLUGIN  the plugin: its packed .tgz or its package directory (with dist/)
//   RUNIC_TRANSLATIONS_TOOL    dotnet-runic-translations.dll of the same Translations build
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, statSync } from "node:fs";
import { mkdir, mkdtemp, readFile, realpath, rm, symlink, writeFile } from "node:fs/promises";
import { networkInterfaces, tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import test from "node:test";
import { chromium } from "playwright-core";
import { createServer } from "vite";
import { runic } from "../dist/index.js";
import { scaled } from "../../../../tests/support/timeouts.mjs";

const here = fileURLToPath(new URL("..", import.meta.url));
const packages = fileURLToPath(new URL("../../", import.meta.url));
const pluginSource = process.env.RUNIC_TRANSLATIONS_PLUGIN;
const tool = process.env.RUNIC_TRANSLATIONS_TOOL;
const executablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
const lan = Object.values(networkInterfaces()).flat().find((entry) => entry?.family === "IPv4" && !entry.internal)?.address;
const skip = !pluginSource || !tool || !executablePath ? "set RUNIC_TRANSLATIONS_PLUGIN, RUNIC_TRANSLATIONS_TOOL and PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH" : false;

const english = "# The application's messages\n\n# Shown above the counter.\nheading = Counter\nclicks =\n" +
  "  .input {$count :integer}\n  .match $count\n  one {{One click}}\n  * {{{$count} clicks}}\n";
const german = "heading = Zähler\nclicks =\n  .input {$count :integer}\n  .match $count\n  one {{Ein Klick}}\n  * {{{$count} Klicks}}\n";

async function link(root, name, target) {
  const path = join(root, "node_modules", ...name.split("/"));
  await mkdir(dirname(path), { recursive: true });
  await symlink(await realpath(target), path, "dir");
}

async function withPage({ host = "127.0.0.1", allow } = {}, run) {
  const root = await realpath(await mkdtemp(join(tmpdir(), "runic-inpage-edit-real-")));
  const previous = process.env.RUNIC_DEV_ALLOW;
  let server, browser;
  try {
    // The plugin as an application installs it.
    const pluginDirectory = join(root, "node_modules", "@runic-artifex", "vite-plugin-runic-translations");
    await mkdir(pluginDirectory, { recursive: true });
    if (statSync(pluginSource).isDirectory()) {
      execFileSync("cp", ["-R", join(pluginSource, "dist"), join(pluginSource, "package.json"), pluginDirectory]);
    } else {
      execFileSync("tar", ["-xzf", resolve(pluginSource), "-C", pluginDirectory, "--strip-components=1"]);
    }
    const { runicTranslations } = await import(pathToFileURL(join(pluginDirectory, "dist", "index.js")).href);
    await mkdir(join(root, "translations"));
    await writeFile(join(root, "package.json"), JSON.stringify({ name: "inpage-edit", private: true, type: "module" }));
    await writeFile(join(root, "translations", "runic.json"), JSON.stringify({ schemaVersion: 1, catalog: "app",
      code: { namespace: "App", className: "AppText" }, baseLocale: "en", locales: ["en", "de"] }));
    await writeFile(join(root, "translations", "en.rmf2"), english);
    await writeFile(join(root, "translations", "de.rmf2"), german);
    await writeFile(join(root, "CounterPage.jsx"), `import { useTextLocale } from "@runic-artifex/react";
import { m } from "virtual:runic-translations/app";

export function CounterPage() {
  useTextLocale();
  return <section><h1 id="heading">{m.heading()}</h1><p id="clicks">{m.clicks({ count: 2 })}</p></section>;
}
`);
    await writeFile(join(root, "main.jsx"), `import { createRoot } from "react-dom/client";
import { configureTranslatedText } from "@runic-artifex/views";
import { CounterPage } from "./CounterPage.jsx";
configureTranslatedText({ resolve: () => undefined, getLocale: () => "en" });
createRoot(document.getElementById("root")).render(<CounterPage />);
globalThis.editTest = { ready: true };
`);
    await writeFile(join(root, "index.html"), '<!doctype html><html lang="en"><body><div id="root"></div><script type="module" src="/main.jsx"></script></body></html>');
    await writeFile(join(root, "sources.json"), JSON.stringify({ format: "runic.views.bridge-sources/1", viewModels: [], members: [] }));
    await link(root, "@runic-artifex/vite-plugin-runic", here);
    await link(root, "@runic-artifex/views", join(packages, "views"));
    await link(root, "@runic-artifex/react", join(packages, "react"));
    await link(root, "react", join(here, "node_modules", "react"));
    await link(root, "react-dom", join(here, "node_modules", "react-dom"));
    await link(root, "vite", join(here, "node_modules", "vite"));
    if (allow === undefined) delete process.env.RUNIC_DEV_ALLOW; else process.env.RUNIC_DEV_ALLOW = allow;
    delete process.env.RUNIC_TRANSLATIONS_EDIT_ALLOW;
    server = await createServer({
      root, configFile: false, logLevel: "warn",
      plugins: [
        runicTranslations({ project: join(root, "translations"), cwd: root, command: "dotnet", commandArguments: [tool] }),
        runic({ sourceMap: "sources.json" }),
      ],
      resolve: { dedupe: ["react", "react-dom"] },
      server: { host, port: 0, fs: { strict: false } },
    });
    await server.listen();
    browser = await chromium.launch({ executablePath, headless: true });
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", (error) => errors.push(String(error)));
    await page.goto(`http://${host === "0.0.0.0" ? lan : host}:${server.httpServer.address().port}`);
    await page.waitForFunction(() => globalThis.editTest?.ready === true, null, { timeout: scaled(60_000) });
    await page.waitForFunction(() => document.querySelector("#heading")?.textContent === "Counter", null, { timeout: scaled(30_000) });
    await run({ page, en: join(root, "translations", "en.rmf2"), de: join(root, "translations", "de.rmf2") });
    assert.deepEqual(errors, []);
  } finally {
    if (previous === undefined) delete process.env.RUNIC_DEV_ALLOW; else process.env.RUNIC_DEV_ALLOW = previous;
    await browser?.close();
    await server?.close();
    await rm(root, { recursive: true, force: true });
  }
}

const plain = (selector) => `document.querySelector(${JSON.stringify(selector)})?.textContent.replace(/[\\u2061-\\u2064]/gu, "")`;
const editorStatus = (status) => `document.querySelector("runic-inspect")?.shadowRoot?.querySelector("section.editor")?.dataset.status === ${JSON.stringify(status)}`;

async function openEditor(page) {
  await page.keyboard.press("Alt+Shift+X");
  await page.waitForFunction(() => document.querySelector("#heading").textContent !== "Counter", null, { timeout: scaled(15_000) });
  const box = await page.locator("#heading").boundingBox();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  const card = page.locator("runic-inspect .card");
  const edit = card.getByRole("button", { name: "Edit app:heading" });
  await edit.waitFor({ timeout: scaled(30_000) });
  await edit.focus();
  await page.keyboard.press("Enter");
  await page.waitForFunction(editorStatus("ready"), null, { timeout: scaled(30_000) });
  return { card, editor: card.locator("section.editor") };
}

test("edit a translation on the page with the real Runic Translations dev server", { timeout: scaled(300_000), skip }, async () => {
  await withPage({}, async ({ page, en, de }) => {
    await page.evaluate(() => { globalThis.sameDocument = true; });
    const { card, editor } = await openEditor(page);
    assert.equal(await editor.locator("textarea").inputValue(), "Counter");
    assert.deepEqual(await editor.locator("select option").evaluateAll((options) => options.map((option) => option.value)), ["en", "de"]);

    // Save: written by the compiler's source-preserving writer, shown in place.
    await editor.locator("textarea").fill("Clicks counter");
    await editor.locator("textarea").press("Control+Enter");
    await page.waitForFunction(`${plain("#heading")} === "Clicks counter"`, null, { timeout: scaled(30_000) });
    assert.equal(await page.evaluate(() => globalThis.sameDocument), true, "no reload");
    assert.equal(await readFile(en, "utf8"), english.replace("heading = Counter", "heading = Clicks counter"), "only the entry changed");
    assert.equal(await readFile(de, "utf8"), german);
    await card.getByText('"Clicks counter"').waitFor({ timeout: scaled(30_000) });

    // A text that does not compile: RTR diagnostics, nothing written.
    await editor.locator("textarea").fill("Clicks {$");
    await editor.locator("textarea").press("Control+Enter");
    await page.waitForFunction(editorStatus("invalid"), null, { timeout: scaled(30_000) });
    assert.match(await editor.locator(".diagnostics").textContent(), /RTR\d{4}/u);
    assert.equal(await readFile(en, "utf8"), english.replace("heading = Counter", "heading = Clicks counter"));

    // A stale edit: 409 with the file's text, then reapplied.
    await editor.getByRole("button", { name: "Revert" }).click();
    await writeFile(en, english.replace("heading = Counter", "heading = Edited elsewhere"));
    await page.waitForFunction(`${plain("#heading")} === "Edited elsewhere"`, null, { timeout: scaled(30_000) });
    // The panel read the message again on the page's change (no edit pending), so a write now would succeed; make it stale.
    await page.waitForFunction(() => document.querySelector("runic-inspect").shadowRoot.querySelector("textarea").value === "Edited elsewhere", null, { timeout: scaled(30_000) });
    await editor.locator("textarea").fill("Mine");
    await writeFile(en, english.replace("heading = Counter", "heading = Edited again"));
    await editor.getByRole("button", { name: "Save" }).click();
    await page.waitForFunction(editorStatus("conflict"), null, { timeout: scaled(30_000) });
    assert.match(await editor.getByRole("group", { name: "Conflict" }).textContent(), /Edited again/u);
    await editor.getByRole("button", { name: "Reapply my text" }).click();
    await editor.locator("textarea").press("Control+Enter");
    await page.waitForFunction(`${plain("#heading")} === "Mine"`, null, { timeout: scaled(30_000) });
    assert.equal(await readFile(en, "utf8"), english.replace("heading = Counter", "heading = Mine"));
  });
});

test("from another machine with the real Runic Translations dev server: 403, then RUNIC_DEV_ALLOW", {
  timeout: scaled(300_000), skip: skip || (lan === undefined && "no non-loopback IPv4 address"),
}, async () => {
  await withPage({ host: "0.0.0.0" }, async ({ page }) => {
    await page.evaluate(() => globalThis[Symbol.for("runic.vite.inspect")].toggle(true));
    await page.waitForFunction(() => document.querySelector("#heading").textContent !== "Counter", null, { timeout: scaled(15_000) });
    const description = await page.evaluate(() => globalThis[Symbol.for("runic.vite.inspect")].describe(document.querySelector("#heading")));
    assert.equal(description.messages[0]?.key, "heading");
    assert.equal(description.messages[0].editable, undefined);
    assert.match(description.notes.join("\n"), /Translations dev server refused this machine its message API \(403\)/u);
    const status = await page.evaluate(() => fetch("/__runic-translations/message?key=heading&catalog=app").then((response) => response.status));
    assert.equal(status, 403);
  });
  await withPage({ host: "0.0.0.0", allow: lan }, async ({ page, en }) => {
    const { editor } = await openEditor(page);
    await editor.locator("textarea").fill("Remote edit");
    await editor.locator("textarea").press("Control+Enter");
    await page.waitForFunction(`${plain("#heading")} === "Remote edit"`, null, { timeout: scaled(30_000) });
    assert.match(await readFile(en, "utf8"), /^heading = Remote edit$/mu);
  });
});
