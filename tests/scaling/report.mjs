// The scaling suite's report (tests/scaling/README.md): turns artifacts/scaling/results.json into a Markdown table of
// every metric, with the known costs, the improvements and every budget that changed against a base revision.
//   bun tests/scaling/report.mjs [--base <revision>] [--require-all]
// --base compares tests/scaling/budgets.json with that revision's (fetched when the checkout does not have it).
// --require-all fails when a budget entry has no result, so that an entry without a case is found. The report goes to
// standard output and, in GitHub Actions, to the job summary.
import { execFileSync } from "node:child_process";
import { appendFileSync, existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(fileURLToPath(import.meta.url), "../../..");
export const budgetsPath = "tests/scaling/budgets.json";
export const resultsPath = resolve(root, "artifacts/scaling/results.json");

const cell = value => value === undefined || value === null ? "" : String(value).replaceAll("|", "\\|");
const pair = values => Array.isArray(values) ? values.join(" → ") : cell(values);

// The entries whose limit, class, sizes or margin differ between two budget files, and the added and removed ones.
export function budgetChanges(base, current) {
  const changes = [];
  const fields = ["class", "sizes", "limit", "margin"];
  for (const [name, entry] of Object.entries(current)) {
    const before = base[name];
    if (!before) {
      changes.push({ name, change: "added", before: "", after: fields.map(field => `${field} ${pair(entry[field])}`).filter(text => !/ $/.test(text)).join(", ") });
      continue;
    }
    for (const field of fields)
      if (JSON.stringify(before[field]) !== JSON.stringify(entry[field]))
        changes.push({ name, change: field, before: pair(before[field]), after: pair(entry[field]) });
  }
  for (const name of Object.keys(base)) if (!current[name]) changes.push({ name, change: "removed", before: pair(base[name].limit), after: "" });
  return changes;
}

// The report's Markdown and the problems that fail it.
export function report({ results, budgets, base = null, requireAll = false }) {
  const problems = [];
  const lines = ["## Scaling budgets", ""];
  lines.push("| Metric | Class | Sizes | Measured | Limit | Status |", "| --- | --- | --- | --- | --- | --- |");
  for (const [name, result] of Object.entries(results)) {
    const status = result.status === "ok" ? "ok" : result.status === "improved" ? `improved: ${result.message}` : `**${result.status}**: ${result.message ?? ""}`;
    lines.push(`| \`${name}\` | ${cell(result.class)} | ${pair(result.sizes)} | ${pair(result.values)} | ${cell(result.limit)} | ${cell(status)} |`);
  }
  const missing = Object.keys(budgets).filter(name => !results[name]);
  if (requireAll && missing.length) problems.push(`budget entries without a result (a case was removed or renamed): ${missing.join(", ")}`);
  const unknown = Object.keys(results).filter(name => !budgets[name]);
  if (unknown.length) problems.push(`results without a budget entry: ${unknown.join(", ")}`);

  const known = Object.entries(budgets).filter(([, entry]) => entry.known);
  lines.push("", "### Known costs", "");
  if (known.length) for (const [name, entry] of known)
    lines.push(`- \`${name}\`: ${entry.known} (${entry.class}${entry.target ? `, target ${entry.target}` : ""}; ${entry.issue ?? "no issue"})`);
  else lines.push("None.");

  const improved = Object.entries(results).filter(([, result]) => result.status === "improved");
  lines.push("", "### Improvements", "");
  if (improved.length) {
    for (const [name, result] of improved) lines.push(`- \`${name}\`: ${result.message}`);
    lines.push("", "Lower these budgets with `bun run scaling --update` (D064.3: an improvement does not fail CI).");
  } else lines.push("None.");

  lines.push("", "### Budget changes against the base", "");
  if (!base) lines.push("No base revision to compare with.");
  else {
    const changes = budgetChanges(base, budgets);
    if (changes.length) {
      lines.push("| Metric | Change | Base | This revision |", "| --- | --- | --- | --- |");
      for (const change of changes) lines.push(`| \`${change.name}\` | ${change.change} | ${cell(change.before)} | ${cell(change.after)} |`);
      lines.push("", "A raised limit needs a sentence under \"Budget changes\" in the pull request.");
    } else lines.push("None.");
  }
  if (problems.length) lines.push("", "### Problems", "", ...problems.map(problem => `- ${problem}`));
  return { markdown: `${lines.join("\n")}\n`, problems };
}

// The cases of budgets.json at revision, or null when the revision has none (the suite is new there).
export function baseBudgets(revision) {
  const show = () => execFileSync("git", ["show", `${revision}:${budgetsPath}`], { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  const has = () => {
    try { execFileSync("git", ["cat-file", "-e", `${revision}^{commit}`], { cwd: root, stdio: "ignore" }); return true; } catch { return false; }
  };
  // A shallow CI checkout has only the tested commit.
  if (!has()) execFileSync("git", ["fetch", "--no-tags", "--depth=1", "origin", revision], { cwd: root, stdio: "inherit" });
  try { return JSON.parse(show()).cases; }
  catch (error) {
    if (/does not exist|exists on disk, but not in/.test(String(error.stderr ?? ""))) return null;
    throw error;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const option = name => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
  if (!existsSync(resultsPath)) {
    console.error(`${resultsPath} does not exist: run the scaling suite first (bun run scaling).`);
    process.exit(1);
  }
  const revision = option("--base");
  const base = revision ? baseBudgets(revision) ?? {} : null;
  const { markdown, problems } = report({
    results: JSON.parse(readFileSync(resultsPath, "utf8")).cases,
    budgets: JSON.parse(readFileSync(resolve(root, budgetsPath), "utf8")).cases,
    base,
    requireAll: args.includes("--require-all"),
  });
  console.log(markdown);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, markdown);
  if (problems.length) {
    for (const problem of problems) console.error(problem);
    process.exit(1);
  }
}
