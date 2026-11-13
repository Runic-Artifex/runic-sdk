// bun run scaling [--update] [-- <suite arguments>]
// Runs the scaling suite (tests/dotnet/Runic.Application.Scaling.Tests) against tests/scaling/budgets.json and prints
// the report (report.mjs). --update records the results without failing on a limit or a class, then rewrites each
// measured entry's "measured" and "limit" (its class and sizes stay as written). Review the diff before committing it.
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { report, resultsPath, suiteCases } from "./report.mjs";

const root = resolve(fileURLToPath(import.meta.url), "../../..");
const suite = resolve(root, "tests/dotnet/Runic.Application.Scaling.Tests");
const budgetsFile = resolve(root, "tests/scaling/budgets.json");

// The budgets after an update: each measured entry records its values, and its limit becomes the larger size's value,
// plus the entry's margin for allocated bytes.
export function updatedBudgets(budgets, results) {
  const cases = {};
  for (const [name, entry] of Object.entries(budgets.cases)) {
    const result = results[name];
    if (!result) {
      cases[name] = entry;
      continue;
    }
    const [, large] = result.values;
    const limit = entry.margin ? Math.ceil(large * (1 + entry.margin)) : large;
    const { measured: _measured, limit: _limit, ...rest } = entry;
    cases[name] = { class: rest.class, sizes: rest.sizes, limit, measured: result.values, ...rest };
  }
  return { ...budgets, cases };
}

// Two-space JSON with arrays of numbers on one line, as budgets.json is written.
export const format = value => `${JSON.stringify(value, null, 2).replace(/\[\s*(-?[\d.]+(?:,\s*-?[\d.]+)*)\s*\]/g,
  (_, items) => `[${items.split(/,\s*/).join(", ")}]`)}\n`;

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const separator = args.indexOf("--");
  const own = separator >= 0 ? args.slice(0, separator) : args;
  const extra = separator >= 0 ? args.slice(separator + 1) : [];
  const update = own.includes("--update");
  const configuration = process.env.CONFIGURATION || "Release";
  const run = spawnSync("dotnet", ["run", "--project", suite, "-c", configuration, ...(extra.length ? ["--", ...extra] : [])], {
    cwd: root, stdio: "inherit", env: { ...process.env, ...(update ? { RUNIC_SCALING_UPDATE: "1" } : {}) },
  });
  if (run.error) throw run.error;
  let results;
  try { results = JSON.parse(readFileSync(resultsPath, "utf8")).cases; }
  catch {
    console.error(`The suite wrote no results (${resultsPath}).`);
    process.exit(run.status || 1);
  }
  if (update && run.status === 0) {
    const budgets = JSON.parse(readFileSync(budgetsFile, "utf8"));
    writeFileSync(budgetsFile, format(updatedBudgets(budgets, results)));
    console.log(`Updated ${budgetsFile} from ${Object.keys(results).length} results.`);
  }
  const { markdown, problems } = report({
    results, budgets: suiteCases(JSON.parse(readFileSync(budgetsFile, "utf8")).cases, "managed"), requireAll: extra.length === 0,
  });
  console.log(markdown);
  for (const problem of problems) console.error(problem);
  process.exit(run.status || (problems.length ? 1 : 0));
}
