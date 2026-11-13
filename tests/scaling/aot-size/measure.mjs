// The NativeAOT size budgets (tests/scaling/README.md, "NativeAOT size"): publishes the fixture (Fixture/AotSize.csproj)
// for linux-x64 with an mstat file, with no fixture ViewModel and at each size of the aot/ budget entries, reads each
// mstat with MstatReader and checks the aot/ entries of tests/scaling/budgets.json.
//   bun tests/scaling/aot-size/measure.mjs [--update] [--base <revision>] [--keep]
// --update records the results and rewrites the aot/ entries' measured and limit, as bun run scaling --update does for
// the managed suite. --base lists the budgets that changed against that revision. --keep keeps the published
// applications and mstat files under artifacts/scaling/aot-size/ (by default only results.json stays). The report goes
// to standard output and, in GitHub Actions, to the job summary.
import { spawnSync } from "node:child_process";
import { appendFileSync, copyFileSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { baseBudgets, report, suiteCases } from "../report.mjs";
import { format, updatedBudgets } from "../run.mjs";
import { viewModelsSource } from "./fixture.mjs";

const root = resolve(fileURLToPath(import.meta.url), "../../../..");
const budgetsFile = resolve(root, "tests/scaling/budgets.json");
const fixture = resolve(root, "tests/scaling/aot-size/Fixture/AotSize.csproj");
const reader = resolve(root, "tests/scaling/aot-size/MstatReader/MstatReader.csproj");
const output = resolve(root, "artifacts/scaling/aot-size");
export const resultsFile = resolve(output, "results.json");
const rid = "linux-x64";
const mstat = resolve(root, `tests/scaling/aot-size/Fixture/obj/Release/net10.0/${rid}/native/AotSize.mstat`);

// A metric is an improvement when it is below this share of its limit (as ScalingBudgets.cs).
const improvementShare = 0.75;

// The fixture's ViewModels, and the records of #901's metric: types that instantiate a type of the Bridge's
// generated-code support API over a fixture ViewModel, also inside another generic (a Dictionary of descriptors).
const viewModel = /\bAotSize\.Models\.Vm\d+(?!\w)/;
const generatedGeneric = /Runic\.Application\.Views\.Generated\.[\w.+]*<[^\t]*\bAotSize\.Models\.Vm\d+(?!\w)/;

// The measurements of one build from its executable size and its mstat records (MstatReader: kind, size, name).
export function measurement(executableBytes, records) {
  const result = { executableBytes, viewModelTypes: 0, viewModelTypeBytes: 0, generatedGenericTypes: 0, viewModelMethodBytes: 0, types: 0 };
  for (const line of records.split("\n")) {
    const [kind, size, name] = line.split("\t");
    if (kind === "Types") {
      result.types++;
      if (viewModel.test(name)) {
        result.viewModelTypes++;
        result.viewModelTypeBytes += Number(size);
      }
      if (generatedGeneric.test(name)) result.generatedGenericTypes++;
    } else if (kind === "Methods" && viewModel.test(name)) result.viewModelMethodBytes += Number(size);
  }
  return result;
}

// The value of each aot/ metric in a build with `count` ViewModels; `baseline` is the build without any.
export const metrics = {
  // The executable bytes above the build without fixture ViewModels.
  "aot/bridge/executable-bytes": (build, baseline) => build.executableBytes - baseline.executableBytes,
  "aot/bridge/view-model-types": build => build.viewModelTypes,
  "aot/bridge/generated-generic-types": build => build.generatedGenericTypes,
  "aot/executable-bytes": build => build.executableBytes,
};

// Checks one metric as ScalingBudgets.Check does. A "total" has no growth class: only its limit at the larger size.
export function check(name, entry, values, updating = false) {
  const { class: growth = "", sizes, limit, margin = 0, slack = 0 } = entry;
  const [small, large] = values;
  const k = sizes[1] / sizes[0];
  const factor = { "O(1)": 1, "O(n)": k, "O(n²)": k * k, total: null }[growth];
  let failure = null;
  const notes = [];
  if (factor === undefined) failure = `${name}: unknown class '${growth}' in budgets.json`;
  else if (factor !== null) {
    const upper = small * factor * (1 + margin) + slack;
    if (large > upper) failure = `${name} grows faster than ${growth}: ${small} at ${sizes[0]}, ${large} at ${sizes[1]} (at most ${Math.round(upper * 100) / 100}).`;
    else if (factor > 1 && large < 0.5 * factor * small * (1 - margin)) notes.push(`grows slower than ${growth}`);
  }
  if (!failure && limit === undefined) failure = `${name} has no limit in budgets.json; run bun tests/scaling/aot-size/measure.mjs --update.`;
  else if (!failure && large > limit) failure = `${name} is ${large} at ${sizes[1]}, over its limit ${limit}.`;
  else if (limit !== undefined && large < improvementShare * limit) notes.push(`below ${improvementShare * 100} % of its limit ${limit}`);
  const status = updating ? "recorded" : failure ? "failed" : notes.length ? "improved" : "ok";
  return {
    result: { class: growth, sizes, values, limit: limit ?? null, margin: margin || null, status, message: failure ?? (notes.join("; ") || null) },
    failure: updating && factor !== undefined ? null : failure,
  };
}

// The per-ViewModel table: each measurement at every size and its slope between the two largest sizes.
export function perViewModel(builds) {
  const counts = Object.keys(builds).map(Number).sort((a, b) => a - b);
  const [low, high] = counts.slice(-2);
  const rows = [
    ["Executable bytes", "executableBytes"],
    ["Type records naming a fixture ViewModel", "viewModelTypes"],
    ["`Generated.*<VM>` type records (#901's metric)", "generatedGenericTypes"],
    ["Type node bytes naming a fixture ViewModel", "viewModelTypeBytes"],
    ["Method node bytes naming a fixture ViewModel", "viewModelMethodBytes"],
  ];
  const slope = key => Math.round((builds[high][key] - builds[low][key]) / (high - low));
  const lines = [
    "### Per bridged ViewModel", "",
    `| Measurement | ${counts.map(count => `K = ${count}`).join(" | ")} | Per ViewModel (K = ${low} → ${high}) |`,
    `| --- | ${counts.map(() => "---:").join(" | ")} | ---: |`,
    ...rows.map(([label, key]) => `| ${label} | ${counts.map(count => builds[count][key].toLocaleString("en-US")).join(" | ")} | ${slope(key).toLocaleString("en-US")} |`),
    "",
    `Executable bytes per ViewModel: ${(slope("executableBytes") / 1024).toFixed(1)} KiB. linux-x64, Release, \`PublishAot=true\`; K is the number of fixture ViewModels besides the shell.`,
  ];
  return { markdown: `${lines.join("\n")}\n`, slopes: Object.fromEntries(rows.map(([, key]) => [key, slope(key)])) };
}

function run(command, args) {
  const started = Date.now();
  const result = spawnSync(command, args, { cwd: root, stdio: ["ignore", "inherit", "inherit"] });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} ${args.join(" ")} exited with ${result.status}.`);
  return Date.now() - started;
}

// Publishes the fixture with `count` ViewModels and measures it.
function publish(count, keep) {
  const models = resolve(output, "models");
  mkdirSync(models, { recursive: true });
  writeFileSync(resolve(models, "ViewModels.g.cs"), viewModelsSource(count));
  const directory = resolve(output, `k${count}`);
  rmSync(directory, { recursive: true, force: true });
  rmSync(mstat, { force: true });
  console.log(`Publishing the NativeAOT size fixture with ${count} ViewModels…`);
  const milliseconds = run("dotnet", ["publish", fixture, "-c", "Release", "-r", rid, "--self-contained", "true",
    "-p:PublishAot=true", "-p:IlcGenerateMstatFile=true", "-p:IlcTreatWarningsAsErrors=true", `-p:AotSizeModelsDirectory=${models}`,
    "-o", resolve(directory, "bin"), "--nologo", "-v:q"]);
  const records = spawnSync("dotnet", [resolve(root, "tests/scaling/aot-size/MstatReader/bin/Release/net10.0/MstatReader.dll"), mstat],
    { cwd: root, encoding: "utf8", maxBuffer: 1 << 28 });
  if (records.status !== 0) throw new Error(`MstatReader failed on ${mstat}: ${records.stderr}`);
  const build = { ...measurement(statSync(resolve(directory, "bin/AotSize")).size, records.stdout), publishSeconds: Math.round(milliseconds / 1000) };
  if (keep) {
    copyFileSync(mstat, resolve(directory, "AotSize.mstat"));
    writeFileSync(resolve(directory, "records.tsv"), records.stdout);
  } else rmSync(directory, { recursive: true, force: true });
  console.log(`  ${build.executableBytes} bytes, ${build.viewModelTypes} ViewModel types, ${build.generatedGenericTypes} Generated.*<VM> types (${build.publishSeconds} s)`);
  return build;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const option = name => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
  const updating = args.includes("--update");
  const keep = args.includes("--keep");
  const revision = option("--base");

  const budgets = JSON.parse(readFileSync(budgetsFile, "utf8"));
  const entries = suiteCases(budgets.cases, "aot-size");
  for (const name of Object.keys(entries)) if (!metrics[name]) throw new Error(`budgets.json has ${name}, which measure.mjs does not measure.`);
  for (const name of Object.keys(metrics)) if (!entries[name]) throw new Error(`${name} has no entry in budgets.json. Add one with its class and sizes, then run --update.`);
  // The build without fixture ViewModels, then every size of the entries.
  const counts = [...new Set([0, ...Object.values(entries).flatMap(entry => entry.sizes)])].sort((a, b) => a - b);

  rmSync(resultsFile, { force: true });
  run("dotnet", ["build", reader, "-c", "Release", "--nologo", "-v:q"]);
  const builds = Object.fromEntries(counts.map(count => [count, publish(count, keep)]));

  const results = {};
  const failures = [];
  for (const [name, entry] of Object.entries(entries)) {
    const values = entry.sizes.map(size => metrics[name](builds[size], builds[0]));
    const { result, failure } = check(name, entry, values, updating);
    results[name] = result;
    if (failure) failures.push(failure);
  }
  const table = perViewModel(builds);
  mkdirSync(output, { recursive: true });
  writeFileSync(resultsFile, `${JSON.stringify({ schema: 1, cases: results, builds, perViewModel: table.slopes }, null, 2)}\n`);

  if (updating && failures.length === 0) {
    writeFileSync(budgetsFile, format(updatedBudgets(budgets, results)));
    console.log(`Updated ${budgetsFile} from ${Object.keys(results).length} results.`);
  }
  const current = suiteCases(JSON.parse(readFileSync(budgetsFile, "utf8")).cases, "aot-size");
  const base = revision ? suiteCases(baseBudgets(revision) ?? {}, "aot-size") : null;
  const { markdown, problems } = report({ results, budgets: current, base, requireAll: true, title: "NativeAOT size budgets" });
  const summary = `${markdown}\n${table.markdown}`;
  console.log(summary);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary);
  for (const problem of [...failures, ...problems]) console.error(problem);
  process.exit(failures.length || problems.length ? 1 : 0);
}
