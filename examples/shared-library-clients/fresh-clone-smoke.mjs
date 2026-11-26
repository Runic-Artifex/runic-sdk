// devtools-library-dx S4 (F6's regression test): the shared library clients example as a real application builds it, from
// a fresh clone, with no manual step. The example is copied out of the repository (so the repository's
// Directory.Build.props, which turns the frontend install off for in-repository projects, does not apply), as a Bun, npm
// and pnpm workspace whose root lists shared-ui and App/Frontend, with shared-ui made a compiled package (a tsc build
// into dist/). The SDK's web packages are packed and installed from their archives; the projects build the SDK from
// source, as the example does. For each package manager:
//
// 1. the author's one install writes the root lock file (third-party packages a day old, D038), and the copy is cleaned
//    as a fresh clone (node_modules, bin, obj, dist and the generated folders deleted);
// 2. `dotnet runic dev --no-window` reaches Ready: the build installs the workspace and builds the library's package,
//    which the page imports (before 0.24 Vite failed to resolve it and dev stopped after 30 s with RAPPDEV1007);
// 3. cleaned again, `dotnet build` passes with installs enabled, writes the contract declarations into the package's
//    dist/ and the page into www/, and a second build installs and builds no frontend.
//
//   node examples/shared-library-clients/fresh-clone-smoke.mjs
//
// It needs the Release build of RunicSdk.Core.slnx (the generator and dotnet-runic) and of packages/web/views and
// packages/web/vite-plugin-runic (their dist/), Bun, npm and pnpm, and the npm registry. RUNIC_FRESH_CLONE_MANAGERS
// selects managers (bun,npm,pnpm); RUNIC_FRESH_CLONE_KEEP=1 keeps the temporary folder.
import { execFileSync, spawn, spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { firstPartyScope, minimumReleaseAge, npmReleaseAgeArguments } from "../../eng/dependencies/release-age.mjs";
import { tail } from "../shared/smoke.mjs";

const example = fileURLToPath(new URL(".", import.meta.url));
const sdk = resolve(example, "../..");
const configuration = process.env.CONFIGURATION ?? "Release";
const tool = join(sdk, `tools/dotnet-runic/bin/${configuration}/net10.0/dotnet-runic.dll`);
const managers = (process.env.RUNIC_FRESH_CLONE_MANAGERS ?? "bun,npm,pnpm").split(",").map(name => name.trim()).filter(Boolean);
for (const required of [tool, join(sdk, "packages/web/views/dist"), join(sdk, "packages/web/vite-plugin-runic/dist")])
  if (!existsSync(required)) throw new Error(`${required} is missing: build RunicSdk.Core.slnx (-c ${configuration}) and the web packages first.`);

const scratch = mkdtempSync(join(tmpdir(), "runic-fresh-clone-"));
const run = (command, args, options = {}) => {
  const result = spawnSync(command, args, { encoding: "utf8", ...options });
  return { status: result.status, output: `${result.stdout ?? ""}${result.stderr ?? ""}${result.error ? String(result.error) : ""}` };
};
const json = (file, edit) => { const value = JSON.parse(readFileSync(file, "utf8")); edit(value); writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`); };
const text = (file, edit) => writeFileSync(file, edit(readFileSync(file, "utf8")));

// What the example's .gitignore leaves out of a clone.
function freshClone(folder) {
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const path = join(folder, entry.name);
    // Retries, as a build server that outlives its build may still be closing a file there.
    if (["node_modules", "bin", "obj", "dist", "generated"].includes(entry.name)) rmSync(path, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
    else freshClone(path);
  }
}

function workspace(manager, packs, archives) {
  const repo = join(scratch, manager);
  cpSync(example, repo, { recursive: true, filter: source => !/(^|[\\/])(node_modules|bin|obj|dist)([\\/]|$)/.test(relative(example, source)) });
  for (const project of ["App/SharedLibraryApp.csproj", "SharedUi/SharedUi.csproj"]) {
    text(join(repo, project), source => {
      let result = source.replaceAll("../../../", `${sdk.replaceAll("\\", "/")}/`);
      // dev serves a Window project; Bun's build command is the targets' default for a Bun workspace.
      if (project.startsWith("App/")) result = result
        .replace("<OutputType>Exe</OutputType>", "<OutputType>Exe</OutputType>\n    <RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject>")
        .replace(/\s*<RunicApplicationFrontendBuildCommand>.*<\/RunicApplicationFrontendBuildCommand>/, "");
      return result;
    });
    rmSync(join(repo, project, "../packages.lock.json"), { force: true });
  }
  // Outside the repository: no install-off default, the SDK's package versions and package sources.
  writeFileSync(join(repo, "Directory.Build.props"), "<Project />\n");
  writeFileSync(join(repo, "Directory.Packages.props"), `<Project>\n  <Import Project="${join(sdk, "Directory.Packages.props")}" />\n</Project>\n`);
  writeFileSync(join(repo, "NuGet.config"), readFileSync(join(sdk, "NuGet.config"), "utf8")
    .replace('value=".cache/nuget"', `value="${join(sdk, ".cache/nuget")}"`));
  // A compiled library package: its build writes dist/, which its exports name.
  json(join(repo, "shared-ui/package.json"), value => {
    value.version = "0.0.0";
    value.scripts = { ...value.scripts, build: "tsc -p tsconfig.build.json" };
    value.exports = {
      ".": { types: "./dist/index.d.ts", default: "./dist/index.js" },
      "./generated": { types: "./dist/generated/runic-clients.d.ts", default: "./dist/generated/runic-clients.js" },
      "./generated/mocks": { types: "./dist/generated/runic-mocks.d.ts", default: "./dist/generated/runic-mocks.js" },
    };
  });
  writeFileSync(join(repo, "shared-ui/tsconfig.build.json"), `${JSON.stringify({ extends: "./tsconfig.json",
    compilerOptions: { noEmit: false, declaration: true, outDir: "dist", rootDir: "src" }, include: ["src/**/*.ts"] }, null, 2)}\n`);
  // The workspace, as the example's README tells a real application to set it up.
  const archive = name => `file:${join(packs, name).replaceAll("\\", "/")}`;
  json(join(repo, "App/Frontend/package.json"), value => {
    value.dependencies = { "@acme/shared-ui": manager === "npm" ? "*" : "workspace:*", "@runic-artifex/views": archive(archives.views) };
    value.devDependencies = { "@runic-artifex/vite-plugin-runic": archive(archives.plugin), typescript: "6.0.3", vite: "8.3.4" };
  });
  const root = { name: "acme", private: true, workspaces: ["shared-ui", "App/Frontend"] };
  const overrides = { "@runic-artifex/views": archive(archives.views) };
  if (manager === "bun") Object.assign(root, { packageManager: "bun@1.4.4", overrides });
  if (manager === "npm") Object.assign(root, { overrides });
  if (manager === "pnpm") {
    delete root.workspaces;
    writeFileSync(join(repo, "pnpm-workspace.yaml"), `packages:\n  - shared-ui\n  - App/Frontend\noverrides:\n  "@runic-artifex/views": "${archive(archives.views)}"\n`);
  }
  writeFileSync(join(repo, "package.json"), `${JSON.stringify(root, null, 2)}\n`);
  text(join(repo, "App/Frontend/tsconfig.json"), source => source.replace(/,\s*\/\/ Inside this repository[\s\S]*?"paths": \{[\s\S]*?\}\s*\n/, "\n"));
  text(join(repo, "App/Frontend/vite.config.ts"), source => source
    .replace(/\s*\/\/ Inside this repository nothing installs[\s\S]*?alias: \[[\s\S]*?\],/, "")
    .replace(/const sharedUi = .*\n/, "").replace(/, sharedUi\]/, "]"));
  // The author's one install, which writes the lock file they commit.
  const install = {
    bun: ["install", "--ignore-scripts", `--minimum-release-age=${minimumReleaseAge}`, "--minimum-release-age-excludes=@runic-artifex/views", "--minimum-release-age-excludes=@runic-artifex/vite-plugin-runic"],
    npm: ["install", "--ignore-scripts", "--no-audit", "--no-fund", ...npmReleaseAgeArguments()],
    pnpm: ["install", "--ignore-scripts", `--config.minimumReleaseAge=${minimumReleaseAge / 60}`, `--config.minimumReleaseAgeExclude=${firstPartyScope}`],
  }[manager];
  const installed = run(manager, install, { cwd: repo, shell: process.platform === "win32" });
  if (installed.status !== 0) throw new Error(`${manager}: the author's install failed:\n${tail(installed.output)}`);
  freshClone(repo);
  return repo;
}

// dev --no-window until "[dev] Ready"; a failure or 180 s without it fails. It returns once dev has exited, as dev
// still writes below obj/ while it stops, and the next step deletes obj/.
function devReady(repo) {
  return new Promise((resolvePromise, reject) => {
    const started = Date.now();
    const child = spawn("dotnet", [tool, "dev", "--no-window", "--configuration", configuration, "--project", "App"],
      { cwd: repo, detached: process.platform !== "win32" });
    const exited = new Promise(resolveExit => child.once("exit", resolveExit));
    let output = "", settled = false;
    const finish = async error => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      const seconds = (Date.now() - started) / 1000;
      try { process.platform === "win32" ? spawnSync("taskkill", ["/pid", String(child.pid), "/t", "/f"]) : process.kill(-child.pid, "SIGTERM"); } catch {}
      const stopped = await Promise.race([exited.then(() => true), new Promise(resolveWait => setTimeout(resolveWait, 60_000, false))]);
      if (!error && !stopped) error = "dev did not exit within 60 s of SIGTERM.";
      if (error) reject(new Error(`${error}\n${tail(output)}`));
      else resolvePromise(seconds);
    };
    const timer = setTimeout(() => finish("dev did not reach Ready in 180 s."), 180_000);
    const read = chunk => { output += chunk; if (output.includes("[dev] Ready")) finish(); };
    child.stdout.on("data", read);
    child.stderr.on("data", read);
    child.on("exit", code => finish(`dev exited with ${code} before Ready.`));
  });
}

const build = repo => run("dotnet", ["build", "App/SharedLibraryApp.csproj", "-c", configuration, "-nologo", "-v:m", "-clp:NoSummary"], { cwd: repo });
const frontendWork = /Installing the frontend workspace|Building the frontend package|vite v\d|\$ tsc /;

try {
  const packs = join(scratch, "packs");
  mkdirSync(packs, { recursive: true });
  const pack = folder => execFileSync("npm", ["pack", "--silent", "--pack-destination", packs],
    { cwd: join(sdk, folder), encoding: "utf8", shell: process.platform === "win32" }).trim().split("\n").pop();
  const archives = { views: pack("packages/web/views"), plugin: pack("packages/web/vite-plugin-runic") };
  for (const manager of managers) {
    const repo = workspace(manager, packs, archives);
    const seconds = await devReady(repo);
    const contracts = join(repo, "shared-ui/dist/generated/about.contract.d.ts");
    if (!existsSync(contracts)) throw new Error(`${manager}: dev's build did not build the library's package (${contracts}).`);

    freshClone(repo);
    let result = build(repo);
    if (result.status !== 0) throw new Error(`${manager}: the fresh clone's dotnet build failed:\n${tail(result.output)}`);
    const installs = result.output.split("\n").filter(line => line.includes("Installing the frontend workspace")).length;
    if (installs !== 1) throw new Error(`${manager}: the workspace installed ${installs} times instead of once:\n${tail(result.output)}`);
    for (const file of [contracts, join(repo, `App/bin/${configuration}/net10.0/www/index.html`)])
      if (!existsSync(file)) throw new Error(`${manager}: the build wrote no ${file}.\n${tail(result.output)}`);
    result = build(repo);
    if (result.status !== 0 || frontendWork.test(result.output))
      throw new Error(`${manager}: a second build with nothing changed installed or built a frontend:\n${tail(result.output)}`);
    console.log(`FRESH_CLONE_OK|${manager}|dev-ready-${seconds.toFixed(1)}s|build|contracts|no-change-rebuild`);
  }
} finally {
  if (process.env.RUNIC_FRESH_CLONE_KEEP === "1") console.log(`Kept ${scratch}`);
  else rmSync(scratch, { recursive: true, force: true });
}
