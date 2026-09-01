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
const packages = ["application-bridge", "angular"];
const staging = mkdtempSync(join(tmpdir(), "runic-toolkit-npm-pack."));
mkdirSync(output, { recursive: true });
const revisionResult = spawnSync("git", ["-C", root, "rev-parse", "HEAD"], { encoding: "utf8" });
if (revisionResult.status !== 0) {
  throw new Error("Could not resolve the source revision for npm package provenance.");
}
const revision = revisionResult.stdout.trim();

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
    manifest.gitHead = revision;
    for (const field of ["dependencies", "optionalDependencies"]) {
      for (const dependency of Object.keys(manifest[field] ?? {})) {
        if (dependency.startsWith("@runic-artifex/")) manifest[field][dependency] = version;
      }
    }
    manifest.publishConfig = suppliedRegistry === "public"
      ? { access: "public", registry: "https://registry.npmjs.org" }
      : { access: "restricted", registry: "https://npm.pkg.github.com" };
    delete manifest.scripts;
    writeFileSync(packagePath, `${JSON.stringify(manifest, null, 2)}\n`);

    const result = spawnSync(
      process.platform === "win32" ? "bun.exe" : "bun",
      ["pm", "pack", "--destination", output, "--quiet"],
      { cwd: target, encoding: "utf8", stdio: "pipe" },
    );
    if (result.status !== 0) {
      if (result.error) throw result.error;
      process.stderr.write(result.stderr || result.stdout || "bun pm pack failed without diagnostic output.\n");
      process.exit(result.status ?? 1);
    }
    process.stdout.write(
      `Packed ${manifest.name}@${version} for ${suppliedRegistry} as ${basename(result.stdout.trim())}.\n`,
    );
  }
} finally {
  rmSync(staging, { recursive: true, force: true });
}
