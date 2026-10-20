import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, realpathSync, mkdirSync, writeFileSync } from "node:fs";
import { availableParallelism } from "node:os";
import { resolve, dirname, isAbsolute } from "node:path";
import { fileURLToPath } from "node:url";
import { root, workspace, configuration } from "../run.mjs";
import { sourceDigest } from "./source-state.mjs";

export function buildPaths(base = root, npm = workspace.npm) {
  const projects = [...readFileSync(resolve(base, "RunicSdk.Core.slnx"), "utf8").matchAll(/<Project Path="([^"]+)"/g)].map(([, path]) => path);
  const viewsExamples = ["first-window", "first-window-desktop", "status-icon", "multi-window", "notes-view-first", "notes-reactive-views"];
  const paths = [
    ...projects.flatMap(path => ["bin", "obj"].map(output => `${dirname(path)}/${output}`)),
    ...npm.map(item => `${item.path}/dist`),
    ...viewsExamples.flatMap(name => ["Frontend/dist", "Frontend/src/generated"].map(output => `examples/${name}/${output}`)),
  ];
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
  // zstd on every core: gzip took about 100 s on the build job and 18 s in each job that restores it.
  const archive = resolve(directory, "build.tar.zst"), manifest = resolve(directory, "build.json");
  const zstd = { ...process.env, ZSTD_CLEVEL: "3", ZSTD_NBTHREADS: String(availableParallelism()) };
  const metadata = { schema: "runic.ci-build/1", revision: execFileSync("git", ["rev-parse", "HEAD"], { cwd: root, encoding: "utf8" }).trim(),
    source: sourceDigest(), directory: realpathSync(root), platform: process.platform, architecture: process.arch,
    configuration, sdk: execFileSync("dotnet", ["--version"], { cwd: root, encoding: "utf8" }).trim() };
  if (process.argv[2] === "create") {
    mkdirSync(directory, { recursive: true });
    const paths = buildPaths();
    if (!paths.length) throw new Error("Build outputs are missing.");
    execFileSync("tar", ["--zstd", "-cf", archive, "--null", "-T", "-"], { cwd: root, env: zstd, input: paths.join("\0") + "\0", stdio: ["pipe", "inherit", "inherit"] });
    writeFileSync(manifest, JSON.stringify({ ...metadata, sha256: createHash("sha256").update(readFileSync(archive)).digest("hex") }, null, 2));
  } else if (process.argv[2] === "restore") {
    const actual = JSON.parse(readFileSync(manifest, "utf8"));
    validateBuild(actual, metadata);
    if (actual.sha256 !== createHash("sha256").update(readFileSync(archive)).digest("hex")) throw new Error("Build archive checksum mismatch.");
    execFileSync("tar", ["--zstd", "-xf", archive], { cwd: root, env: zstd, stdio: "inherit" });
  } else throw new Error("Use create or restore.");
}
