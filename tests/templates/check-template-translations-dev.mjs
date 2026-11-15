// Checks the development loop of a generated --translations project whose page compiles its catalog with the
// Translations Vite plugin (#944): under `dotnet runic dev`, an edited, an added and a removed message reach the page
// without a build.
//
//   bun check-template-translations-dev.mjs --host desktop|cswebui --project <directory> -- <command>...
//
// The command (dotnet runic dev) starts the application with RUNIC_APPLICATION_SERVE_ONLY=1, as check-template-page.mjs
// does. The check opens the page in Chromium, waits for the Bridge, then edits the title in Frontend/translations/en.rmf2
// and waits for the page's heading to show it in place: the template's plugin (0.12, runic-translations-sdk#179) updates
// an edited message without reloading the page, and configureTranslatedText renders it again. It then adds a message to
// every locale's file and waits until the development server's virtual:runic-translations/app module exports it, and
// removes it again and waits until the module no longer has it (an added or removed message may reload the page). It
// restores the catalog's files in every case.
import { spawn } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { chromium } from "playwright-core";
import { scaled } from "../support/timeouts.mjs";

// dotnet runic dev installs the frontend dependencies and builds before the application reports its URL.
const startTimeout = scaled(900_000);
const timeout = scaled(60_000);
const tail = text => text.split("\n").slice(-60).join("\n");

function startApplication(command, noDisplay) {
  const env = { ...process.env, RUNIC_APPLICATION_SERVE_ONLY: "1" };
  if (noDisplay) { delete env.DISPLAY; delete env.WAYLAND_DISPLAY; }
  // Its own process group, so stopping it ends the development server and everything it started.
  const child = spawn(command[0], command.slice(1), { env, detached: true, stdio: ["ignore", "pipe", "pipe"] });
  const application = { child, output: "", exited: null };
  child.stdout.on("data", chunk => { application.output += chunk; });
  child.stderr.on("data", chunk => { application.output += chunk; });
  application.exit = new Promise(resolve => child.on("exit", (code, signal) => resolve(application.exited = { code, signal })));
  child.on("error", error => { application.output += `\n${error.stack}`; application.exited ??= { code: null, signal: "spawn" }; });
  return application;
}

async function waitForOutput(application, pattern) {
  for (let waited = 0; waited < startTimeout; waited += 200) {
    const match = application.output.match(pattern);
    if (match) return match[1];
    if (application.exited) break;
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  throw new Error(`dotnet runic dev did not print ${pattern}.`);
}

async function stopApplication(application) {
  if (application.exited) return;
  // dotnet runic dev stops its development server and host on SIGTERM.
  try { process.kill(-application.child.pid, "SIGTERM"); } catch { /* already gone */ }
  const stopped = await Promise.race([application.exit, new Promise(resolve => setTimeout(resolve, scaled(30_000), null))]);
  if (!stopped) {
    try { process.kill(-application.child.pid, "SIGKILL"); } catch { /* already gone */ }
    await application.exit;
  }
}

async function check(url, viteOrigin, catalog) {
  const files = { en: join(catalog, "en.rmf2"), de: join(catalog, "de.rmf2") };
  const originals = Object.fromEntries(Object.entries(files).map(([locale, path]) => [locale, readFileSync(path, "utf8")]));
  const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, headless: true, timeout });
  try {
    const page = await browser.newPage();
    await page.goto(url, { waitUntil: "load", timeout });
    await page.getByRole("status").filter({ hasText: "Connected to the .NET Window." }).waitFor({ timeout });
    await page.getByRole("heading", { level: 1, name: "Runic Views", exact: true }).waitFor({ timeout });

    // An edited message: the page shows the new text in place. A reload would lose this marker.
    await page.evaluate(() => { window.__runicTemplateCheck = "before the edit"; });
    const edited = originals.en.replace(/^  title = Runic Views$/m, "  title = Runic Views, edited in development");
    if (edited === originals.en) throw new Error(`${files.en} has no "  title = Runic Views" line to edit.`);
    const started = Date.now();
    writeFileSync(files.en, edited);
    await page.getByRole("heading", { level: 1, name: "Runic Views, edited in development", exact: true }).waitFor({ timeout });
    const editMs = Date.now() - started;
    if (await page.evaluate(() => window.__runicTemplateCheck) !== "before the edit")
      throw new Error("The page reloaded for an edited message; the Translations Vite plugin 0.12 updates it in place.");

    // An added and a removed message: every locale translates it, else the catalog does not compile (RTR0010). The page
    // renders no new message, so the check reads the development server's module, as an import of it would.
    const note = () => page.evaluate(async origin => {
      // t is the timestamp that Vite's HMR client adds; Vite drops it before resolving the module.
      const module = await import(`${origin}/@id/virtual:runic-translations/app?t=${Date.now()}`);
      return typeof module.m.template_check === "function" ? module.m.template_check() : null;
    }, viteOrigin);
    // Between the two writes the catalog does not compile, and the browser keeps an import that failed then for the
    // page's lifetime, so a failed import reloads the page before the next try.
    const until = async (predicate, what) => {
      let value;
      for (let waited = 0; waited < timeout; waited += 250) {
        value = await note().catch(error => `error: ${error.message}`);
        if (predicate(value)) return;
        if (typeof value === "string" && value.startsWith("error: "))
          await page.reload({ waitUntil: "load", timeout }).catch(() => undefined);
        await page.waitForTimeout(250);
      }
      throw new Error(`The development server's catalog did not ${what}; the module answered ${JSON.stringify(value)}.`);
    };
    writeFileSync(files.de, `${originals.de}\ntemplate_check = Neu in der Entwicklung\n`);
    writeFileSync(files.en, `${edited}\ntemplate_check = Added in development\n`);
    await until(value => value === "Added in development", "export the added message");
    writeFileSync(files.en, edited);
    writeFileSync(files.de, originals.de);
    await until(value => value === null, "drop the removed message");
    return editMs;
  } finally {
    for (const [locale, path] of Object.entries(files)) writeFileSync(path, originals[locale]);
    await browser.close();
  }
}

async function main(args) {
  const separator = args.indexOf("--");
  const host = args[0] === "--host" ? args[1] : null;
  const project = args[2] === "--project" ? args[3] : null;
  if (!["desktop", "cswebui"].includes(host) || !project || separator !== 4 || separator === args.length - 1)
    throw new Error("Usage: check-template-translations-dev.mjs --host desktop|cswebui --project <directory> -- <command>...");
  const application = startApplication(args.slice(separator + 1), host === "desktop");
  try {
    const viteOrigin = new URL(await waitForOutput(application, /Vite development server ready at (http:\/\/127\.0\.0\.1:\d+)\//)).origin;
    const url = await waitForOutput(application, /RUNIC_APPLICATION_URL=(\S+)/);
    const editMs = await check(url, viteOrigin, join(project, "Frontend/translations"));
    console.log(`TRANSLATIONS_DEV_OK|edited message shown in place after ${editMs} ms|added and removed message served`);
  } catch (error) {
    error.message += `\ndotnet runic dev output:\n${tail(application.output)}`;
    throw error;
  } finally {
    await stopApplication(application);
  }
}

if (import.meta.main) {
  try {
    await main(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}
