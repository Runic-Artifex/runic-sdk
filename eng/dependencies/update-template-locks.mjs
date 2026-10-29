#!/usr/bin/env bun
// Regenerate maintained starter locks against this checkout's package candidates.
// Never publishes. Final archive hashes are stamped into staged locks at packaging.
import { spawn } from "node:child_process";
import { copyFile, mkdtemp, readFile, writeFile, mkdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { root, workspace, run } from "../run.mjs";
import { readToolchain } from "../toolchain.mjs";
import { nodeCompatibility } from "../node-compatibility.mjs";
import { templateFrontendDirectory, templateFrontends } from "../release/template-locks.mjs";

const toolchain = readToolchain(root);
// D038: third-party packages resolve to versions published at least a day ago. The Runic packages come from the
// candidate registry, which lists no publish times; the age check skips them.
const minimumReleaseAge = 24 * 60 * 60;
const publishedBefore = new Date(Date.now() - minimumReleaseAge * 1000).toISOString();
const runicPackages = workspace.npm.map(entry => entry.name);
const temporary = await mkdtemp(join(tmpdir(), "runic-template-locks-"));
let registry;
try {
  const archives = [];
  for (const entry of workspace.npm) {
    run("bun", ["pm", "pack", "--destination", temporary], join(root, entry.path));
    archives.push(join(temporary, `${entry.name.replace('@', '').replace('/', '-')}-${workspace.version}.tgz`));
  }
  const ready = join(temporary, "registry.url");
  registry = spawn(process.execPath, [join(root, "tests/templates/template-npm-registry.mjs"), ready, ...archives], { stdio: "inherit" });
  let address;
  const deadline = Date.now() + 10_000;
  while (!address && Date.now() < deadline && registry.exitCode === null) {
    address = await readFile(ready, "utf8").then(value => value.trim()).catch(() => undefined);
    if (!address) await new Promise(resolve => setTimeout(resolve, 25));
  }
  if (!address) throw new Error("Candidate registry did not become ready.");
  for (const framework of templateFrontends) {
    const source = join(root, "tools/Runic.Application.Templates/content", templateFrontendDirectory(framework));
    const manifest = JSON.parse(await readFile(join(source, "package.json"), "utf8"));
    for (const section of ["dependencies", "devDependencies"]) {
      for (const name of Object.keys(manifest[section] ?? {})) {
        if (name.startsWith("@runic-artifex/")) manifest[section][name] = workspace.version;
      }
    }
    for (const [manager, filename, args] of [
      ["bun", "bun.lock", ["install", "--lockfile-only", "--ignore-scripts", `--minimum-release-age=${minimumReleaseAge}`,
        ...runicPackages.map(name => `--minimum-release-age-excludes=${name}`)]],
      ["npm", "package-lock.json", ["install", "--package-lock-only", "--ignore-scripts", "--no-audit", "--no-fund", `--before=${publishedBefore}`]],
      ["pnpm", "pnpm-lock.yaml", ["install", "--lockfile-only", "--ignore-scripts", `--config.minimumReleaseAge=${minimumReleaseAge / 60}`,
        "--config.minimumReleaseAgeExclude=@runic-artifex/*"]],
    ]) {
      const directory = join(temporary, framework, manager);
      await mkdir(directory, { recursive: true });
      manifest.packageManager = `${manager}@${toolchain[manager]}`;
      await writeFile(join(directory, "package.json"), JSON.stringify(manifest, null, 2) + "\n");
      await writeFile(join(directory, ".npmrc"), `@runic-artifex:registry=${address}\n`);
      // pnpm reads overrides only from pnpm-workspace.yaml.
      if (manager === "pnpm") await copyFile(join(source, "pnpm-workspace.yaml"), join(directory, "pnpm-workspace.yaml")).catch(error => { if (error.code !== "ENOENT") throw error; });
      run(manager, args, directory, nodeCompatibility().env);
      let lock = await readFile(join(directory, filename), "utf8");
      for (const entry of workspace.npm) {
        const basename = entry.name.slice(entry.name.indexOf('/') + 1);
        const local = `${address}/archives/${entry.name.replace('@', '').replace('/', '-')}-${workspace.version}.tgz`;
        if (manager === "bun") lock = lock.replaceAll(`"${local}"`, '""');
        else if (manager === "pnpm") lock = lock.replaceAll(`, tarball: ${local}`, "");
        else lock = lock.replaceAll(local, `https://registry.npmjs.org/${entry.name}/-/${basename}-${workspace.version}.tgz`);
      }
      if (lock.includes(address) || lock.includes(temporary)) throw new Error(`Local path escaped into ${framework}/${filename}.`);
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
