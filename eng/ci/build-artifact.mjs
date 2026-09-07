import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, realpathSync, mkdirSync, writeFileSync } from "node:fs";
import { resolve, dirname, isAbsolute } from "node:path";
import { fileURLToPath } from "node:url";
import { root, workspace, configuration } from "../run.mjs";
import { sourceDigest } from "./source-state.mjs";

export function buildPaths(base = root, npm = workspace.npm) {
  const projects = [...readFileSync(resolve(base, "RunicSdk.Core.slnx"), "utf8").matchAll(/<Project Path="([^"]+)"/g)].map(([, path]) => path);
  const paths = [...projects.flatMap(path => ["bin", "obj"].map(output => `${dirname(path)}/${output}`)), ...npm.map(item => `${item.path}/dist`)];
  for (const path of paths)
    if (isAbsolute(path) || path.split(/[\\/]/).includes("..")) throw new Error(`Unsafe build path: ${path}`);
  return [...new Set(paths.filter(path => existsSync(resolve(base, path))))].sort();
}

export function validateBuild(actual, expected) {
  for (const key of ["schema", "revision", "source", "directory", "platform", "architecture", "configuration", "sdk"])
    if (actual[key] !== expected[key]) throw new Error(`Build artifact ${key} mismatch; rebuild for this checkout and toolchain.`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const directory = resolve(root, "artifacts/ci-build");
  const archive = resolve(directory, "build.tar.gz"), manifest = resolve(directory, "build.json");
  const metadata = { schema: "runic.ci-build/1", revision: execFileSync("git", ["rev-parse", "HEAD"], { cwd: root, encoding: "utf8" }).trim(),
    source: sourceDigest(), directory: realpathSync(root), platform: process.platform, architecture: process.arch,
    configuration, sdk: execFileSync("dotnet", ["--version"], { cwd: root, encoding: "utf8" }).trim() };
  if (process.argv[2] === "create") {
    mkdirSync(directory, { recursive: true });
    const paths = buildPaths();
    if (!paths.length) throw new Error("Build outputs are missing.");
    execFileSync("tar", ["-czf", archive, "--null", "-T", "-"], { cwd: root, input: paths.join("\0") + "\0", stdio: ["pipe", "inherit", "inherit"] });
    writeFileSync(manifest, JSON.stringify({ ...metadata, sha256: createHash("sha256").update(readFileSync(archive)).digest("hex") }, null, 2));
  } else if (process.argv[2] === "restore") {
    const actual = JSON.parse(readFileSync(manifest, "utf8"));
    validateBuild(actual, metadata);
    if (actual.sha256 !== createHash("sha256").update(readFileSync(archive)).digest("hex")) throw new Error("Build archive checksum mismatch.");
    execFileSync("tar", ["-xzf", archive], { cwd: root, stdio: "inherit" });
  } else throw new Error("Use create or restore.");
}
