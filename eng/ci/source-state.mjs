import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { lstatSync, readFileSync, readlinkSync, mkdirSync, writeFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { root } from "../run.mjs";

export function sourceDigest(base = root) {
  const paths = [...new Set(execFileSync("git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard"],
    { cwd: base, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 }).split("\0").filter(Boolean))].sort();
  const hash = createHash("sha256");
  for (const path of paths) {
    hash.update(`${path}\0`);
    try {
      const file = resolve(base, path), stat = lstatSync(file);
      if (!stat.isFile() && !stat.isSymbolicLink()) throw new Error(`Unsupported source entry: ${path}`);
      hash.update(stat.isSymbolicLink() ? `link:${readlinkSync(file)}` : createHash("sha256").update(readFileSync(file)).digest("hex"));
      hash.update(`:${stat.mode & 0o111}\0`);
    } catch (error) { if (error.code !== "ENOENT") throw error; hash.update("missing\0"); }
  }
  return hash.digest("hex");
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const path = resolve(root, ".cache/ci-source-state.json");
  if (process.argv[2] === "capture") {
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, JSON.stringify({ sha256: sourceDigest() }));
  } else if (process.argv[2] === "check") {
    if (JSON.parse(readFileSync(path, "utf8")).sha256 !== sourceDigest())
      throw new Error("Verification changed source files or lockfiles. Inspect git diff and newly generated files.");
    console.log("Source and lockfiles match the start of this job.");
  } else throw new Error("Use capture or check.");
}
