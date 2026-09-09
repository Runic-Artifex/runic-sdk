import assert from "node:assert/strict";
import { copyFileSync, mkdirSync, readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { readNpmCandidates, verifyTemplateLock } from "./template-locks.mjs";

const [content, npmDirectory, output] = process.argv.slice(2);
assert(content && npmDirectory && output,
  "Usage: stage-template-locks.mjs <template-content> <final-npm-archives> <staging-directory>");
assert.notEqual(resolve(content), resolve(output), "Template locks must be staged outside source content");
const repository = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const workspace = JSON.parse(readFileSync(join(repository, "eng/workspace.json"), "utf8"));
const archives = workspace.npm.map(p => join(npmDirectory,
  `${p.name.replace("@", "").replace("/", "-")}-${workspace.version}.tgz`));
const candidates = readNpmCandidates(archives);
for (const framework of ["react", "vue", "svelte", "angular"]) {
  for (const name of ["package-lock.json", "pnpm-lock.yaml", "bun.lock"]) {
    const staged = join(output, framework, "Frontend", name);
    mkdirSync(dirname(staged), { recursive: true });
    copyFileSync(join(content, framework, "Frontend", name), staged);
    execFileSync(process.execPath, [join(repository, "eng/release/stamp-template-lock.mjs"), staged, ...archives],
      { stdio: "inherit" });
    verifyTemplateLock(readFileSync(staged, "utf8"), name, candidates);
  }
}
console.log("Staged all 12 template locks against final npm archive bytes.");
