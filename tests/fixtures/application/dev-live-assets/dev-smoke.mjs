// `dotnet runic dev` without a Vite or Angular development server (its mode 2): the frontend watcher (`bun run dev`)
// writes Frontend/dist, and the Runic Desktop host serves that directory live and reloads its window when it changes.
// RUNIC_APPLICATION_SERVE_ONLY=1 serves the window's surface without a native window, so headless Chromium is the
// window's page. Run from the SDK root after a Release build of tools/dotnet-runic:
//   node tests/fixtures/application/dev-live-assets/dev-smoke.mjs
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { readFile, rename, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { killTree, launchChromium, scaled, tail, waitFor, waitForExit } from "../../../../examples/shared/smoke.mjs";

const fixture = fileURLToPath(new URL(".", import.meta.url));
const root = join(fixture, "../../../..");
const project = join(fixture, "DevLiveAssets.csproj");
const tool = process.env.RUNIC_DEV_TOOL_DLL ?? join(root, "tools/dotnet-runic/bin/Release/net10.0/dotnet-runic.dll");
if (!existsSync(tool)) throw new Error(`Build tools/dotnet-runic in Release first; ${tool} does not exist.`);
const page = join(fixture, "Frontend/index.html");
const built = join(fixture, "Frontend/dist/index.html");
const original = await readFile(page, "utf8");

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: root, stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    child.stdout.on("data", chunk => { output += chunk; });
    child.stderr.on("data", chunk => { output += chunk; });
    child.on("error", reject);
    child.on("close", code => code === 0 ? resolve(output)
      : reject(new Error(`${command} ${args.join(" ")} exited ${code}:\n${tail(output)}`)));
  });
}

// dev --no-restore installs nothing; the frontend declares no dependencies, so only .NET needs a restore.
await run("dotnet", ["restore", project]);
const dev = spawn("dotnet", [tool, "dev", "--project", project, "-c", "Release", "--no-restore", "--no-dotnet-watch"], {
  cwd: root,
  env: { ...process.env, RUNIC_APPLICATION_SERVE_ONLY: "1" },
  stdio: ["ignore", "pipe", "pipe"],
});
let output = "", errors = "", browser;
dev.stdout.on("data", chunk => { output += chunk; });
dev.stderr.on("data", chunk => { errors += chunk; });
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `dev stdout:\n${tail(output)}\ndev stderr:\n${tail(errors)}`;
const until = (condition, label, timeout) => waitFor(condition, { label, detail, timeout });
const marker = () => browser.evaluate('document.querySelector("#marker")?.textContent ?? null');
const withMarker = text => original.replace('<p id="marker">one</p>', `<p id="marker">${text}</p>`);
// Saves the page as an editor's atomic save does. writeFile truncates before it writes, and Bun's fs.watch reports
// both as one change: build.mjs could copy the page while it is empty and see no later change, leaving an empty
// dist/index.html that the window then rightly shows.
const save = async text => {
  await writeFile(`${page}.tmp`, text);
  await rename(`${page}.tmp`, page);
};

try {
  const started = await until(() => dev.exitCode !== null ? { exited: dev.exitCode }
    : output.match(/RUNIC_APPLICATION_URL=(\S+)/)?.[1], "the Window's surface URL", 180_000);
  if (typeof started !== "string")
    throw new Error(`dotnet runic dev exited with ${started.exited}:\n${tail(output)}\n${tail(errors)}`);
  const url = started;
  if (!/\[dev\] Assets: .*dist \(served live; windows reload on change\)/.test(output))
    throw new Error(`dev did not report the live assets:\n${tail(output)}`);
  browser = await launchChromium(url, { profilePrefix: "runic-dev-assets-" });
  await until(async () => (await marker()) === "one", "the initial page");
  // The host's Views client is not in dist/; the window serves it from the build's www/.
  const client = await browser.evaluate('fetch("runic-views.js").then(response => response.status)');
  if (client !== 200) throw new Error(`runic-views.js answered ${client}.`);

  // A change written to the frontend output directory reloads the window with the new content.
  await writeFile(built, withMarker("two"));
  await until(async () => (await marker()) === "two", "the reload after a dist/ change", 20_000);

  // The frontend watcher copies an edited page into dist/; the window follows it too.
  await save(withMarker("three"));
  await until(async () => (await marker()) === "three", "the reload after the watcher rebuilt dist/", 20_000);
  console.log("DEV_LIVE_ASSETS_OK|served-live|www-fallback|reload-on-dist-change|reload-after-watcher");
} finally {
  await rm(`${page}.tmp`, { force: true });
  await writeFile(page, original);
  try { await browser?.close(); }
  finally {
    // SIGTERM stops dev the way Ctrl+C does: it stops the watcher and the application.
    if (dev.exitCode === null) dev.kill("SIGTERM");
    if (!await waitForExit(dev, scaled(15_000))) killTree(dev);
  }
}
