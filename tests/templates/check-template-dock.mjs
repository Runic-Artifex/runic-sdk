// Checks the Vite DevTools dock and Runic Inspect of a generated CommunityToolkit.Mvvm project under `dotnet runic dev`:
// the Counter button's command is a row `command CounterViewModel.increment → Increment() <n> ms` of the Runic dock's
// Bridge calls, whose button opens the [RelayCommand] method in the editor (#954), and Runic Inspect, on the .NET host's
// page, shows the button's command with that C# location and its link opens it too (#1012).
//
//   bun check-template-dock.mjs --host desktop|cswebui --project <directory> -- <command>...
//
// The command (dotnet runic dev) starts the application with RUNIC_APPLICATION_SERVE_ONLY=1, as check-template-page.mjs
// does, with LAUNCH_EDITOR set to a script that records the file, line and column it is asked to open, and with the
// dock's one-time client authorization off for this loopback-only run. The check opens the page in Chromium (the .NET
// host's document, whose modules come from Vite's development server), shows the Counter View, increments it, opens
// the Runic dock and presses the row's open button.
import { spawn } from "node:child_process";
import { chmodSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { chromium } from "playwright-core";
import { scaled } from "../support/timeouts.mjs";

// dotnet runic dev installs the frontend dependencies and builds before the application reports its URL.
const startTimeout = scaled(900_000);
const timeout = scaled(60_000);
const tail = text => text.split("\n").slice(-60).join("\n");

function startApplication(command, noDisplay, environment) {
  const env = { ...process.env, ...environment, RUNIC_APPLICATION_SERVE_ONLY: "1" };
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

async function waitForFile(path) {
  for (let waited = 0; waited < timeout; waited += 100) {
    try { return readFileSync(path, "utf8"); }
    catch { await new Promise(resolve => setTimeout(resolve, 100)); }
  }
  throw new Error("The dock's open button opened no editor.");
}

// The [RelayCommand] method the row opens: Increment() in WorkspaceViewModel.cs.
function incrementLine(project) {
  const lines = readFileSync(join(project, "WorkspaceViewModel.cs"), "utf8").split("\n");
  const index = lines.findIndex(line => /^\s*private void Increment\(\)/.test(line));
  if (index < 0) throw new Error("WorkspaceViewModel.cs has no Increment() method: the check needs --view-models toolkit.");
  return index + 1;
}

async function check(url, project, record) {
  const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH, headless: true, timeout });
  try {
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(String(error)));
    await page.goto(url, { waitUntil: "load", timeout });
    await page.getByRole("status").filter({ hasText: "Connected to the .NET Window." }).waitFor({ timeout });
    await page.getByRole("button", { name: "Counter", exact: true }).click({ timeout });
    await page.getByRole("heading", { name: "Counter View" }).waitFor({ timeout });
    await page.getByRole("button", { name: "Increment", exact: true }).click({ timeout });

    // The dock registered: its Runic button, which opens the Runic panel.
    const runic = page.locator('button[aria-label="Runic"]');
    await runic.waitFor({ state: "attached", timeout });
    await runic.dispatchEvent("click");
    const row = page.getByText(/^command CounterViewModel\.increment → Increment\(\) [\d.]+ ms$/);
    await row.waitFor({ timeout });
    const label = (await row.first().textContent()).trim();

    // Its button opens the [RelayCommand] method.
    await page.getByRole("button", { name: "Open Increment()" }).first().dispatchEvent("click");
    const [file, line] = (await waitForFile(record)).trim().split("\n");
    const expected = join(project, "WorkspaceViewModel.cs");
    if (file !== expected || Number(line) !== incrementLine(project))
      throw new Error(`The dock opened ${file}:${line}, not ${expected}:${incrementLine(project)}.`);
    rmSync(record, { force: true });

    // Runic Inspect on the same page: the button's command with its C# location, which its link opens.
    const located = await page.evaluate(async () => {
      const button = [...document.querySelectorAll("button")].find((candidate) => candidate.textContent.trim() === "Increment");
      const inspect = globalThis[Symbol.for("runic.vite.inspect")];
      const description = await inspect.describe(button);
      await inspect.select(button);
      return description.commands[0]?.location;
    });
    if (located?.file !== expected || located.line !== incrementLine(project))
      throw new Error(`Runic Inspect located the command at ${JSON.stringify(located)}, not ${expected}:${incrementLine(project)}.`);
    await page.locator("runic-inspect .card a", { hasText: `WorkspaceViewModel.cs:${located.line}` }).first().dispatchEvent("click");
    const [inspectFile, inspectLine] = (await waitForFile(record)).trim().split("\n");
    if (inspectFile !== expected || Number(inspectLine) !== incrementLine(project))
      throw new Error(`Runic Inspect's link opened ${inspectFile}:${inspectLine}, not ${expected}:${incrementLine(project)}.`);
    if (errors.length > 0) throw new Error(`The page threw:\n${errors.join("\n")}`);
    return `${label} → ${file.split(/[\\/]/).pop()}:${line}|inspect ${inspectFile.split(/[\\/]/).pop()}:${inspectLine}`;
  } finally {
    await browser.close();
  }
}

async function main(args) {
  const separator = args.indexOf("--");
  const host = args[0] === "--host" ? args[1] : null;
  const project = args[2] === "--project" ? args[3] : null;
  if (!["desktop", "cswebui"].includes(host) || !project || separator !== 4 || separator === args.length - 1)
    throw new Error("Usage: check-template-dock.mjs --host desktop|cswebui --project <directory> -- <command>...");
  const directory = mkdtempSync(join(tmpdir(), "runic-template-dock-"));
  const editor = join(directory, "record-editor.sh");
  const record = join(directory, "editor-arguments.txt");
  // launch-editor calls an editor it does not know with the file, line and column.
  writeFileSync(editor, '#!/bin/sh\nprintf "%s\\n" "$@" > "$RUNIC_TEST_EDITOR_RECORD"\n');
  chmodSync(editor, 0o755);
  const application = startApplication(args.slice(separator + 1), host === "desktop", {
    LAUNCH_EDITOR: editor, RUNIC_TEST_EDITOR_RECORD: record, VITE_DEVTOOLS_DISABLE_CLIENT_AUTH: "true",
  });
  try {
    await waitForOutput(application, /(Vite development server ready at http:\/\/127\.0\.0\.1:\d+\/)/);
    const url = await waitForOutput(application, /RUNIC_APPLICATION_URL=(\S+)/);
    console.log(`DOCK_DEV_OK|${await check(url, project, record)}`);
  } catch (error) {
    error.message += `\ndotnet runic dev output:\n${tail(application.output)}`;
    throw error;
  } finally {
    await stopApplication(application);
    rmSync(directory, { recursive: true, force: true });
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
