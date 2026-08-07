#!/usr/bin/env node

import { cpSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";
import { spawnSync } from "node:child_process";

const [, , version, suppliedOutput, suppliedRegistry = "github"] = process.argv;
if (version === undefined || suppliedOutput === undefined) {
  console.error("Usage: node eng/pack-npm.mjs <package-version> <output-directory> [github|public]");
  process.exit(2);
}
if (!/^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$/u.test(version)) {
  console.error("Package version must be SemVer-compatible.");
  process.exit(2);
}
if (!new Set(["github", "public"]).has(suppliedRegistry)) {
  console.error("Registry target must be 'github' or 'public'.");
  process.exit(2);
}

const root = resolve(import.meta.dirname, "..");
const output = resolve(suppliedOutput);
const commitResult = spawnSync("git", ["-C", root, "rev-parse", "HEAD"], { encoding: "utf8" });
if (commitResult.status !== 0 || !/^[0-9a-f]{40}$/iu.test(commitResult.stdout.trim())) {
  console.error("Could not resolve the source Git commit.");
  process.exit(1);
}
const repositoryCommit = commitResult.stdout.trim();
const packages = ["application-bridge"];
const staging = mkdtempSync(join(tmpdir(), "runic-toolkit-npm-pack."));
mkdirSync(output, { recursive: true });

try {
  for (const directory of packages) {
    const source = join(root, "web", "packages", directory);
    const target = join(staging, directory);
    cpSync(source, target, {
      recursive: true,
      filter: (path) => !/(?:^|\/)(?:node_modules|test)(?:\/|$)/u.test(path),
    });

    const packagePath = join(target, "package.json");
    const manifest = JSON.parse(readFileSync(packagePath, "utf8"));
    manifest.version = version;
    manifest.gitHead = repositoryCommit;
    if (suppliedRegistry === "public") {
      manifest.publishConfig = {
        access: "public",
        registry: "https://registry.npmjs.org",
      };
    }
    delete manifest.scripts;
    writeFileSync(packagePath, `${JSON.stringify(manifest, null, 2)}\n`);

    const result = spawnSync(
      "npm",
      ["pack", "--ignore-scripts", "--pack-destination", output],
      { cwd: target, encoding: "utf8", stdio: "pipe" },
    );
    if (result.status !== 0) {
      process.stderr.write(result.stderr);
      process.exit(result.status ?? 1);
    }
    process.stdout.write(
      `Packed ${manifest.name}@${version} for ${suppliedRegistry} as ${basename(result.stdout.trim())}.\n`,
    );
  }
} finally {
  rmSync(staging, { recursive: true, force: true });
}
