#!/usr/bin/env node
// Checks that the two workflow files run the same pipeline:
//   .github/workflows/ci.yml   GitHub Actions and Gitea Actions (the stock artifact actions)
//   .forgejo/workflows/ci.yml  Forgejo Actions (Forgejo's forks of the artifact actions)
// Apart from the comment at the top, the files must have the same lines, except that
//   - an artifact step uses actions/upload-artifact or actions/download-artifact in the first file and
//     forgejo/upload-artifact or forgejo/download-artifact at the same place in the second, and
//   - a runs-on line names the same repository variable in both, with its own default label.
// Every action is pinned to a full commit SHA with its version in a comment. Then actionlint lints both files when it
// is on PATH (--actionlint requires it).
//
//   node ci/check-workflows.mjs [--actionlint] [directory]
//
// The directory is the repository root, the folder above ci/ by default. Exit code 0 when the files are in step.
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const githubPath = ".github/workflows/ci.yml";
export const forgejoPath = ".forgejo/workflows/ci.yml";

const uses = /^(\s*-?\s*)uses:\s*(\S+?)@(\S+)(.*)$/;
const pinned = /^[0-9a-f]{40}$/;
const version = /^\s+#\s*v\d/;
const artifact = /^(?:actions|forgejo|https:\/\/code\.forgejo\.org\/forgejo)\/(upload|download)-artifact$/;
const runsOn = /^(\s*runs-on:\s*)\$\{\{\s*vars\.([A-Za-z_][A-Za-z0-9_]*)\s*\|\|\s*'([^']+)'\s*\}\}\s*$/;

// The lines after the comment at the top (comment and blank lines before the first other line), with their numbers.
function body(text) {
  const lines = text.replace(/\r\n/g, "\n").replace(/\s+$/, "").split("\n");
  let start = 0;
  while (start < lines.length && /^\s*(#.*)?$/.test(lines[start])) start++;
  return lines.slice(start).map((line, index) => ({ line, number: start + index + 1 }));
}

// The problems of one file's `uses:` lines: an action without a commit SHA and a version comment, or an artifact
// action of the other CI system.
function actionProblems(file, lines, system) {
  const problems = [];
  for (const { line, number } of lines) {
    const match = uses.exec(line);
    if (!match) continue;
    const [, , action, ref, rest] = match;
    if (action.startsWith("./") || action.startsWith("docker://")) continue;
    if (!pinned.test(ref) || !version.test(rest))
      problems.push(`${file}:${number}: pin ${action} to a full commit SHA with its version in a comment, such as @<sha> # v1.2.3`);
    const kind = artifact.exec(action);
    if (!kind) continue;
    if (system === "github" && !action.startsWith("actions/"))
      problems.push(`${file}:${number}: GitHub and Gitea use the stock actions/${kind[1]}-artifact, not ${action}`);
    if (system === "forgejo" && action.startsWith("actions/"))
      problems.push(`${file}:${number}: Forgejo needs forgejo/${kind[1]}-artifact: actions/${kind[1]}-artifact v4 and later fail there with "not currently supported on GHES"`);
  }
  return problems;
}

// What a line means once the allowed differences are taken out: the artifact action's kind, and a runs-on line's
// variable without its default.
function normalized(line) {
  const step = uses.exec(line);
  const kind = step && artifact.exec(step[2]);
  if (kind) return { text: `${step[1]}uses: <${kind[1]}-artifact>`, artifact: true };
  const runner = runsOn.exec(line);
  if (runner) return { text: `${runner[1]}\${{ vars.${runner[2]} || <label> }}`, label: runner[3] };
  return { text: line };
}

/**
 * Compares the GitHub/Gitea workflow with the Forgejo one. Returns the problems (empty when the files are in step) and
 * counts of the allowed differences.
 */
export function compareWorkflows(githubText, forgejoText, { github = githubPath, forgejo = forgejoPath } = {}) {
  const left = body(githubText), right = body(forgejoText);
  const problems = [...actionProblems(github, left, "github"), ...actionProblems(forgejo, right, "forgejo")];
  let artifacts = 0, labels = 0;
  const length = Math.max(left.length, right.length);
  for (let index = 0; index < length; index++) {
    const a = left[index], b = right[index];
    if (!a || !b) {
      const extra = a ?? b;
      problems.push(`${a ? github : forgejo}:${extra.number}: only this file has this line: ${extra.line.trim()}`);
      break;
    }
    const x = normalized(a.line), y = normalized(b.line);
    if (x.text !== y.text) {
      problems.push(`${github}:${a.number} and ${forgejo}:${b.number} differ:\n  ${a.line.trim()}\n  ${b.line.trim()}`);
      // Report the first difference only: after an added or removed line every later one differs.
      break;
    }
    if (x.artifact) artifacts++;
    if (x.label !== undefined && x.label !== y.label) labels++;
  }
  if (problems.length === 0 && artifacts === 0) problems.push(`${github}: no upload-artifact or download-artifact step`);
  return { problems, artifacts, labels, lines: left.length };
}

function main(args) {
  const requireActionlint = args.includes("--actionlint");
  const directory = resolve(args.find(arg => !arg.startsWith("--")) ?? join(dirname(fileURLToPath(import.meta.url)), ".."));
  const read = path => readFileSync(join(directory, path), "utf8");
  const result = compareWorkflows(read(githubPath), read(forgejoPath));
  if (result.problems.length > 0) {
    for (const problem of result.problems) console.error(problem);
    return 1;
  }
  console.log(`${githubPath} and ${forgejoPath} are in step: ${result.lines} lines, of which only the ${result.artifacts} artifact actions and ${result.labels} default runner labels differ.`);
  const lint = spawnSync("actionlint", [githubPath, forgejoPath], { cwd: directory, stdio: "inherit" });
  if (lint.error?.code === "ENOENT") {
    if (requireActionlint) {
      console.error("actionlint is not on PATH.");
      return 1;
    }
    console.log("actionlint is not on PATH; skipped.");
    return 0;
  }
  return lint.status === 0 ? 0 : 1;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = main(process.argv.slice(2));
