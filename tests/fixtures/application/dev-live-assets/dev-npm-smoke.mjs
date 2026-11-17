// #1081: `dotnet runic dev` with npm, the runic-app template's default package manager. On Windows Node.js installs npm
// as npm.cmd, which dev must find through PATHEXT and start through cmd.exe. The fixture's frontend runs here as an npm
// project: dev installs it (`npm ci`), starts its watcher (`npm run dev`, which copies the page to Frontend/dist), and
// the Runic Desktop host serves that directory (`--browser`, serve-only). A page edit then reaches the served page,
// so the watcher that npm started is running.
// Run from the SDK root after a Release build of tools/dotnet-runic:
//   node tests/fixtures/application/dev-live-assets/dev-npm-smoke.mjs
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { readFile, rename, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { killTree, scaled, tail, waitFor, waitForExit } from "../../../../examples/shared/smoke.mjs";

const fixture = fileURLToPath(new URL(".", import.meta.url));
const root = join(fixture, "../../../..");
const project = join(fixture, "DevLiveAssets.csproj");
const tool = process.env.RUNIC_DEV_TOOL_DLL ?? join(root, "tools/dotnet-runic/bin/Release/net10.0/dotnet-runic.dll");
if (!existsSync(tool)) throw new Error(`Build tools/dotnet-runic in Release first; ${tool} does not exist.`);
const frontend = join(fixture, "Frontend");
const manifest = join(frontend, "package.json");
const lockFile = join(frontend, "package-lock.json");
const page = join(frontend, "index.html");
const originalManifest = await readFile(manifest, "utf8");
const originalPage = await readFile(page, "utf8");

// The same frontend as an npm project: no packageManager field (dev then infers npm from package-lock.json) and node
// instead of Bun for its scripts. It has no dependencies, so `npm ci` needs only the lock file.
const npmManifest = JSON.parse(originalManifest);
delete npmManifest.packageManager;
npmManifest.name = "dev-live-assets-frontend";
npmManifest.scripts = { build: "node build.mjs", dev: "node build.mjs --watch" };
await writeFile(manifest, JSON.stringify(npmManifest, null, 2) + "\n");
await writeFile(lockFile, JSON.stringify({
  name: npmManifest.name, lockfileVersion: 3, requires: true, packages: { "": { name: npmManifest.name } },
}, null, 2) + "\n");

const environment = { ...process.env };
delete environment.RUNIC_APPLICATION_SERVE_ONLY;
const dev = spawn("dotnet", [tool, "dev", "--project", project, "-c", "Release", "--no-dotnet-watch", "--browser"], {
  cwd: root,
  env: environment,
  stdio: ["ignore", "pipe", "pipe"],
});
let output = "", errors = "";
dev.stdout.on("data", chunk => { output += chunk; });
dev.stderr.on("data", chunk => { errors += chunk; });
const detail = async () => `dev stdout:\n${tail(output)}\ndev stderr:\n${tail(errors)}`;
const until = (condition, label, timeout) => waitFor(condition, { label, detail, timeout });
const served = async url => {
  try {
    const response = await fetch(url);
    return response.ok ? /<p id="marker">([^<]*)<\/p>/.exec(await response.text())?.[1] ?? null : null;
  } catch {
    return null;
  }
};

try {
  const started = await until(() => dev.exitCode !== null ? { exited: dev.exitCode }
    : /^\[dev\] Ready$/m.test(output) && output.match(/^\[dev\] Browser: Window serves at (\S+)$/m)?.[1], "dev's entry address", 300_000);
  if (typeof started !== "string")
    throw new Error(`dotnet runic dev exited with ${started.exited}:\n${tail(output)}\n${tail(errors)}`);
  // dev installed the packages with npm and started the watcher through it.
  if (!/^\[dev\] Installing Views Window frontend dependencies\.\.\.$/m.test(output))
    throw new Error(`dev did not install the frontend with npm ci:\n${tail(output)}`);
  if (!/^\[frontend\] Watching index\.html$/m.test(output)) throw new Error(`npm run dev did not start the watcher:\n${tail(output)}`);
  await until(async () => (await served(started)) === "one", "the initial page", 30_000);

  // The watcher copies an edited page into dist/, and the host serves the new page.
  await writeFile(`${page}.tmp`, originalPage.replace('<p id="marker">one</p>', '<p id="marker">npm</p>'));
  await rename(`${page}.tmp`, page);
  await until(async () => (await served(started)) === "npm", "the page that the npm watcher copied", 30_000);
  console.log("DEV_NPM_OK|npm-ci|npm-run-dev|served|watcher-copied");
} finally {
  // SIGTERM stops dev the way Ctrl+C does on Unix. On Windows it would end dev alone, so its tree is killed instead
  // (dev's own stop of npm's tree is tested in ToolTests.WindowsCommands.cs).
  if (process.platform === "win32") killTree(dev);
  else if (dev.exitCode === null) dev.kill("SIGTERM");
  if (!await waitForExit(dev, scaled(15_000))) killTree(dev);
  await rm(`${page}.tmp`, { force: true });
  await writeFile(page, originalPage);
  await writeFile(manifest, originalManifest);
  await rm(lockFile, { force: true });
  await rm(join(frontend, "node_modules"), { recursive: true, force: true });
}
