#!/usr/bin/env bun
// Regenerate maintained starter locks against this checkout's package candidates.
// Never publishes. Final archive hashes are stamped into staged locks at packaging.
//
//   bun eng/dependencies/update-template-locks.mjs [directory...]
//
// Directories, such as runic-app/frontends/react, limit the run to those starters, so that a change to one starter's
// dependencies leaves the other locks as they are; without them every starter's locks are regenerated.
import { spawn } from "node:child_process";
import { copyFile, mkdtemp, readFile, writeFile, mkdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, join } from "node:path";
import { root, workspace, run } from "../run.mjs";
import { readToolchain } from "../toolchain.mjs";
import { nodeCompatibility } from "../node-compatibility.mjs";
import { isIndependentRunicPackage, templateLockDirectories } from "../release/template-locks.mjs";
import { trainFeedNpm } from "../ci/train-feed.mjs";
import { firstPartyScope, minimumReleaseAge, publishedBefore } from "./release-age.mjs";

const toolchain = readToolchain(root);
// D038 (release-age.mjs): third-party packages resolve to versions published at least a day ago. The Runic packages
// come from the candidate registry, which lists no publish times; the age check skips them. The independently released
// Runic packages (the Translations Vite plugin of the --translations variants) are first-party too: the registry passes
// them through from npm, or from the train feed, without publish times.
const runicPackages = workspace.npm.map(entry => entry.name);
const independentArchives = Object.values(trainFeedNpm()).map(item => item.file);
// The archives the registry serves, so that each lock names npm's URL instead of the registry's: the candidates, and the
// train feed's npm candidates of the independently released packages (their tarballs are the ones npm gets).
const servedArchives = [
  ...workspace.npm.map(entry => ({ name: entry.name, version: workspace.version, file: `${entry.name.replace('@', '').replace('/', '-')}-${workspace.version}.tgz` })),
  ...Object.entries(trainFeedNpm()).map(([name, item]) => ({ name, version: item.version, file: basename(item.file) })),
];
function independentRunicPackages(manifest) {
  return Object.keys({ ...manifest.dependencies, ...manifest.devDependencies }).filter(isIndependentRunicPackage);
}
const selected = process.argv.slice(2).map(argument => argument.replace(/\/+$/u, ""));
const directories = selected.length === 0 ? templateLockDirectories
  : templateLockDirectories.filter(entry => selected.includes(entry.directory));
for (const directory of selected) {
  if (!templateLockDirectories.some(entry => entry.directory === directory)) {
    throw new Error(`${directory} is not a starter with locks: ${templateLockDirectories.map(entry => entry.directory).join(", ")}`);
  }
}
const temporary = await mkdtemp(join(tmpdir(), "runic-template-locks-"));
let registry;
try {
  const archives = [];
  for (const entry of workspace.npm) {
    run("bun", ["pm", "pack", "--destination", temporary], join(root, entry.path));
    archives.push(join(temporary, `${entry.name.replace('@', '').replace('/', '-')}-${workspace.version}.tgz`));
  }
  const ready = join(temporary, "registry.url");
  registry = spawn(process.execPath, [join(root, "tests/templates/template-npm-registry.mjs"), ready, ...archives, ...independentArchives], { stdio: "inherit" });
  let address;
  const deadline = Date.now() + 10_000;
  while (!address && Date.now() < deadline && registry.exitCode === null) {
    address = await readFile(ready, "utf8").then(value => value.trim()).catch(() => undefined);
    if (!address) await new Promise(resolve => setTimeout(resolve, 25));
  }
  if (!address) throw new Error("Candidate registry did not become ready.");
  for (const { directory: templateDirectory, pnpmWorkspace } of directories) {
    const source = join(root, "tools/Runic.Application.Templates/content", templateDirectory);
    const manifest = JSON.parse(await readFile(join(source, "package.json"), "utf8"));
    for (const section of ["dependencies", "devDependencies"]) {
      for (const name of Object.keys(manifest[section] ?? {})) {
        // An independently released package keeps the published version the template pins.
        if (name.startsWith("@runic-artifex/") && !isIndependentRunicPackage(name)) manifest[section][name] = workspace.version;
      }
    }
    for (const [manager, filename, args] of [
      ["bun", "bun.lock", ["install", "--lockfile-only", "--ignore-scripts", `--minimum-release-age=${minimumReleaseAge}`,
        ...[...runicPackages, ...independentRunicPackages(manifest)].map(name => `--minimum-release-age-excludes=${name}`)]],
      ["npm", "package-lock.json", ["install", "--package-lock-only", "--ignore-scripts", "--no-audit", "--no-fund", `--before=${publishedBefore()}`]],
      ["pnpm", "pnpm-lock.yaml", ["install", "--lockfile-only", "--ignore-scripts", `--config.minimumReleaseAge=${minimumReleaseAge / 60}`,
        `--config.minimumReleaseAgeExclude=${firstPartyScope}`]],
    ]) {
      const directory = join(temporary, templateDirectory, manager);
      await mkdir(directory, { recursive: true });
      manifest.packageManager = `${manager}@${toolchain[manager]}`;
      await writeFile(join(directory, "package.json"), JSON.stringify(manifest, null, 2) + "\n");
      await writeFile(join(directory, ".npmrc"), `@runic-artifex:registry=${address}\n`);
      // pnpm reads overrides only from pnpm-workspace.yaml.
      if (manager === "pnpm") await copyFile(join(root, "tools/Runic.Application.Templates/content", pnpmWorkspace), join(directory, "pnpm-workspace.yaml")).catch(error => { if (error.code !== "ENOENT") throw error; });
      run(manager, args, directory, nodeCompatibility().env);
      let lock = await readFile(join(directory, filename), "utf8");
      for (const entry of servedArchives) {
        const local = `${address}/archives/${entry.file}`;
        if (manager === "bun") lock = lock.replaceAll(`"${local}"`, '""');
        else if (manager === "pnpm") lock = lock.replaceAll(`, tarball: ${local}`, "");
        else lock = lock.replaceAll(local, `https://registry.npmjs.org/${entry.name}/-/${entry.name.slice(entry.name.indexOf('/') + 1)}-${entry.version}.tgz`);
      }
      if (lock.includes(address) || lock.includes(temporary)) throw new Error(`Local path escaped into ${templateDirectory}/${filename}.`);
      await writeFile(join(source, filename), lock);
    }
  }
} finally {
  if (registry && registry.exitCode === null) {
    registry.kill("SIGTERM");
    await new Promise(resolve => registry.once("exit", resolve));
  }
  await rm(temporary, { recursive: true, force: true });
}
