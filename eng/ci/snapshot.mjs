import { execFileSync } from "node:child_process";
import { cpSync, mkdirSync, lstatSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { sourceDigest } from "./source-state.mjs";

// act checks out the local directory separately for each job. Freeze it once so
// editing during a run cannot mix build inputs from different working trees.
export function snapshot(source, destination) {
  const git = (...args) => execFileSync("git", args, { cwd: source, encoding: "utf8" });
  const before = sourceDigest(source);
  execFileSync("git", ["clone", "--quiet", "--no-local", "--no-checkout", source, destination]);
  // Preserve the index's tracked-path set (including staged additions/deletions).
  // A local clone does not copy blobs reachable only from the source index.
  const index = git("rev-parse", "--path-format=absolute", "--git-path", "index").trim();
  cpSync(index, resolve(destination, ".git/index"));
  for (const path of git("diff", "--cached", "--name-only", "--diff-filter=ACMRT", "-z").split("\0").filter(Boolean)) {
    const blob = execFileSync("git", ["show", `:${path}`], { cwd: source, maxBuffer: 32 * 1024 * 1024 });
    execFileSync("git", ["hash-object", "-w", "--stdin"], { cwd: destination, input: blob });
  }
  execFileSync("git", ["remote", "set-url", "origin", git("remote", "get-url", "origin").trim()], { cwd: destination });
  const paths = new Set(git("ls-files", "-z", "--cached", "--others", "--exclude-standard").split("\0").filter(Boolean));
  for (const path of paths) {
    const from = resolve(source, path), to = resolve(destination, path);
    try { lstatSync(from); } catch (error) { if (error.code === "ENOENT") continue; throw error; }
    mkdirSync(dirname(to), { recursive: true });
    cpSync(from, to, { verbatimSymlinks: true });
  }
  if (sourceDigest(source) !== before || sourceDigest(destination) !== before)
    throw new Error("Source changed while creating the CI snapshot; run the command again.");
}
