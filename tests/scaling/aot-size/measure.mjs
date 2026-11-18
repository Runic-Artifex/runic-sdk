// The NativeAOT size budgets (tests/scaling/README.md, "NativeAOT size"): publishes the fixtures for linux-x64 with an
// mstat file — the CS-WebUI fixture (Fixture/AotSize.csproj) with no fixture ViewModel and at each size of its aot/
// budget entries, the Runic.Desktop host (DesktopFixture) and the CS-WebUI fixture with Translations
// (TranslationsFixture) with none — reads each mstat with MstatReader and checks the aot/ entries of
// tests/scaling/budgets.json.
//   bun tests/scaling/aot-size/measure.mjs [--update] [--base <revision>] [--keep] [--rid <rid>]
// --update records the results and rewrites the aot/ entries' measured and limit, as bun run scaling --update does for
// the managed suite. --base lists the budgets that changed against that revision. --keep keeps the published
// applications and mstat files under artifacts/scaling/aot-size/ (by default only results.json stays). --rid measures
// another runtime, such as win-x64 or osx-arm64, and only reports: the budgets gate linux-x64 (D064.5, D064.25). The
// report goes to standard output and, in GitHub Actions, to the job summary.
import { spawnSync } from "node:child_process";
import { appendFileSync, copyFileSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { baseBudgets, report, suiteCases } from "../report.mjs";
import { format, updatedBudgets } from "../run.mjs";
import { viewModelsSource } from "./fixture.mjs";

const root = resolve(fileURLToPath(import.meta.url), "../../../..");
const budgetsFile = resolve(root, "tests/scaling/budgets.json");
const reader = resolve(root, "tests/scaling/aot-size/MstatReader/MstatReader.csproj");
const output = resolve(root, "artifacts/scaling/aot-size");
export const resultsFile = resolve(output, "results.json");
// The budgets' runtime; another --rid only reports.
const gatedRid = "linux-x64";
const ridIndex = process.argv.indexOf("--rid");
const rid = ridIndex >= 0 ? process.argv[ridIndex + 1] : gatedRid;

// The published fixtures: each metric names one. All three share the ViewModels that fixture.mjs writes.
export const fixtures = {
  views: { directory: "Fixture", assembly: "AotSize" },
  desktop: { directory: "DesktopFixture", assembly: "AotSizeDesktop" },
  "views-translations": { directory: "TranslationsFixture", assembly: "AotSizeTranslations" },
};

// A metric is an improvement when it is below this share of its limit (as ScalingBudgets.cs).
const improvementShare = 0.75;

// The fixture's ViewModels, and the records of #901's metric: types that instantiate a type of the Bridge's
// generated-code support API over a fixture ViewModel, also inside another generic (a Dictionary of descriptors).
const viewModel = /\bAotSize\.Models\.Vm\d+(?!\w)/;
// The generated Bridge of a fixture ViewModel and its nested types (D072.15): its methods are the per-ViewModel code.
const viewModelBridge = /\bGenerated\.Bridges\.Vm\d+Bridge(?!\w)/;
const generatedGeneric = /Runic\.Application\.Views\.Generated\.[\w.+]*<[^\t]*\bAotSize\.Models\.Vm\d+(?!\w)/;

// The mstat records that an assembly owns: its code, type nodes, frozen objects (preinitialized statics and string
// literals) and RVA field data. Blobs (metadata, stack-trace mapping, hashtables) have no owner, and
// DeduplicatedMethods records carry node ids, not sizes.
const ownedKinds = new Set(["Methods", "Types", "FrozenObjects", "RvaFields"]);

// The measurements of one build from its executable size and its mstat records (MstatReader: kind, size, owner, name).
// assemblyBytes sums the owned records per owner assembly; a generic instantiation belongs to its definition's.
export function measurement(executableBytes, records) {
  const result = { executableBytes, viewModelTypes: 0, viewModelTypeBytes: 0, generatedGenericTypes: 0, viewModelMethodBytes: 0, viewModelBridgeMethods: 0, types: 0, assemblyBytes: {} };
  for (const line of records.split("\n")) {
    const [kind, size, owner, name] = line.split("\t");
    if (owner && ownedKinds.has(kind)) result.assemblyBytes[owner] = (result.assemblyBytes[owner] ?? 0) + Number(size);
    if (kind === "Types") {
      result.types++;
      if (viewModel.test(name)) {
        result.viewModelTypes++;
        result.viewModelTypeBytes += Number(size);
      }
      if (generatedGeneric.test(name)) result.generatedGenericTypes++;
    } else if (kind === "Methods") {
      if (viewModel.test(name)) result.viewModelMethodBytes += Number(size);
      if (viewModelBridge.test(name)) result.viewModelBridgeMethods++;
    }
  }
  return result;
}

// Framework assemblies that a Runic application must not contain on linux-x64 (D072.2): the HTTP client and TLS chain
// that HttpListener and HttpClient brought in. Their budgets have limit 0 and name the regression at once.
export const frameworkSentinels = ["System.Net.Http", "System.Net.Security"];
// The Runic assemblies of the CS-WebUI fixture with a budget of their own, so that a regression names its package.
export const runicAssemblies = ["Runic.Application.Views", "Runic.Navigation", "Runic.Application.Views.CsWebUi", "Runic.Assets"];

const owned = assembly => build => build.assemblyBytes[assembly] ?? 0;

// The value of each aot/ metric: its fixture, and its value in a build with `count` ViewModels, where `baseline` is
// that fixture's build without any.
export const metrics = {
  // The executable bytes above the build without fixture ViewModels.
  "aot/bridge/executable-bytes": { fixture: "views", value: (build, baseline) => build.executableBytes - baseline.executableBytes },
  "aot/bridge/view-model-types": { fixture: "views", value: build => build.viewModelTypes },
  "aot/bridge/generated-generic-types": { fixture: "views", value: build => build.generatedGenericTypes },
  "aot/bridge/view-model-methods": { fixture: "views", value: build => build.viewModelBridgeMethods },
  "aot/executable-bytes": { fixture: "views", value: build => build.executableBytes },
  ...Object.fromEntries(frameworkSentinels.map(assembly => [`aot/framework/${assembly}`, { fixture: "views", value: owned(assembly) }])),
  ...Object.fromEntries(runicAssemblies.map(assembly => [`aot/assembly/${assembly}`, { fixture: "views", value: owned(assembly) }])),
  "aot/desktop/executable-bytes": { fixture: "desktop", value: build => build.executableBytes },
  ...Object.fromEntries(frameworkSentinels.map(assembly => [`aot/desktop/framework/${assembly}`, { fixture: "desktop", value: owned(assembly) }])),
  "aot/views-translations/executable-bytes": { fixture: "views-translations", value: build => build.executableBytes },
};

// Checks one metric as ScalingBudgets.Check does. A "total" has no growth class: only its limit at the larger size.
export function check(name, entry, values, updating = false) {
  const { class: growth = "", sizes, limit, margin = 0, slack = 0, noise = 0 } = entry;
  const [small, large] = values;
  const k = sizes[1] / sizes[0];
  const factor = { "O(1)": 1, "O(n)": k, "O(n²)": k * k, total: null }[growth];
  let failure = null;
  const notes = [];
  if (factor === undefined) failure = `${name}: unknown class '${growth}' in budgets.json`;
  else if (factor !== null) {
    // noise: how far each measurement may sit off the true line (section alignment); the factor amplifies it at the
    // smaller size, so it is added there before the factor applies, and once more at the larger size.
    const upper = (small + noise) * factor * (1 + margin) + slack + noise;
    if (large > upper) failure = `${name} grows faster than ${growth}: ${small} at ${sizes[0]}, ${large} at ${sizes[1]} (at most ${Math.round(upper * 100) / 100}).`;
    else if (factor > 1 && large < 0.5 * factor * small * (1 - margin)) notes.push(`grows slower than ${growth}`);
  }
  // The limit is recorded from one measurement (plus the margin), so a metric with section-alignment noise may sit one
  // alignment step above it on another build of the same code: the limit allows the same noise as the growth check.
  if (!failure && limit === undefined) failure = `${name} has no limit in budgets.json; run bun tests/scaling/aot-size/measure.mjs --update.`;
  else if (!failure && large > limit + noise) failure = `${name} is ${large} at ${sizes[1]}, over its limit ${limit}${noise ? ` (+ ${noise} noise)` : ""}.`;
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
    ["Method records of a fixture ViewModel's Bridge", "viewModelBridgeMethods"],
  ];
  const slope = key => Math.round((builds[high][key] - builds[low][key]) / (high - low));
  const lines = [
    "### Per bridged ViewModel", "",
    `| Measurement | ${counts.map(count => `K = ${count}`).join(" | ")} | Per ViewModel (K = ${low} → ${high}) |`,
    `| --- | ${counts.map(() => "---:").join(" | ")} | ---: |`,
    ...rows.map(([label, key]) => `| ${label} | ${counts.map(count => builds[count][key].toLocaleString("en-US")).join(" | ")} | ${slope(key).toLocaleString("en-US")} |`),
    "",
    `Executable bytes per ViewModel: ${(slope("executableBytes") / 1024).toFixed(1)} KiB. ${rid}, Release, \`PublishAot=true\`; K is the number of fixture ViewModels besides the shell.`,
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

// Publishes a fixture with `count` ViewModels and measures it.
function publish(name, count, keep) {
  const { directory: fixtureDirectory, assembly } = fixtures[name];
  const project = resolve(root, "tests/scaling/aot-size", fixtureDirectory, `${assembly}.csproj`);
  const mstat = resolve(root, "tests/scaling/aot-size", fixtureDirectory, `obj/Release/net10.0/${rid}/native/${assembly}.mstat`);
  const executable = rid.startsWith("win-") ? `${assembly}.exe` : assembly;
  const models = resolve(output, "models");
  mkdirSync(models, { recursive: true });
  writeFileSync(resolve(models, "ViewModels.g.cs"), viewModelsSource(count));
  const directory = resolve(output, name === "views" ? `k${count}` : `${name}-k${count}`);
  rmSync(directory, { recursive: true, force: true });
  rmSync(mstat, { force: true });
  console.log(`Publishing the NativeAOT size fixture ${name} with ${count} ViewModels…`);
  const milliseconds = run("dotnet", ["publish", project, "-c", "Release", "-r", rid, "--self-contained", "true",
    "-p:PublishAot=true", "-p:IlcGenerateMstatFile=true", "-p:IlcTreatWarningsAsErrors=true", `-p:AotSizeModelsDirectory=${models}`,
    "-o", resolve(directory, "bin"), "--nologo", "-v:q"]);
  const records = spawnSync("dotnet", [resolve(root, "tests/scaling/aot-size/MstatReader/bin/Release/net10.0/MstatReader.dll"), mstat],
    { cwd: root, encoding: "utf8", maxBuffer: 1 << 28 });
  if (records.status !== 0) throw new Error(`MstatReader failed on ${mstat}: ${records.stderr}`);
  const build = { ...measurement(statSync(resolve(directory, "bin", executable)).size, records.stdout), publishSeconds: Math.round(milliseconds / 1000) };
  if (keep) {
    copyFileSync(mstat, resolve(directory, `${assembly}.mstat`));
    writeFileSync(resolve(directory, "records.tsv"), records.stdout);
  } else rmSync(directory, { recursive: true, force: true });
  console.log(`  ${build.executableBytes} bytes, ${build.viewModelTypes} ViewModel types, ${build.generatedGenericTypes} Generated.*<VM> types (${build.publishSeconds} s)`);
  return build;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const option = name => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
  const updating = args.includes("--update");
  const reportOnly = rid !== gatedRid;
  if (reportOnly && args.includes("--update")) throw new Error(`--update records the ${gatedRid} budgets; ${rid} only reports.`);
  const keep = args.includes("--keep");
  const revision = option("--base");

  const budgets = JSON.parse(readFileSync(budgetsFile, "utf8"));
  const allEntries = suiteCases(budgets.cases, "aot-size");
  for (const name of Object.keys(allEntries)) if (!metrics[name]) throw new Error(`budgets.json has ${name}, which measure.mjs does not measure.`);
  for (const name of Object.keys(metrics)) if (!allEntries[name]) throw new Error(`${name} has no entry in budgets.json. Add one with its class and sizes, then run --update.`);
  // Another runtime only reports the CS-WebUI fixture: the Desktop and Translations fixtures add publishes, not a gate.
  const entries = Object.fromEntries(Object.entries(allEntries).filter(([name]) => !reportOnly || metrics[name].fixture === "views"));
  // Per fixture, the build without fixture ViewModels, then every size of its entries.
  const counts = {};
  for (const [name, entry] of Object.entries(entries)) {
    const fixture = metrics[name].fixture;
    counts[fixture] = [...new Set([0, ...(counts[fixture] ?? []), ...entry.sizes])].sort((a, b) => a - b);
  }

  rmSync(resultsFile, { force: true });
  run("dotnet", ["build", reader, "-c", "Release", "--nologo", "-v:q"]);
  const builds = Object.fromEntries(Object.entries(counts).map(([fixture, sizes]) =>
    [fixture, Object.fromEntries(sizes.map(count => [count, publish(fixture, count, keep)]))]));

  const results = {};
  const failures = [];
  for (const [name, entry] of Object.entries(entries)) {
    const { fixture, value } = metrics[name];
    const values = entry.sizes.map(size => value(builds[fixture][size], builds[fixture][0]));
    const { result, failure } = check(name, entry, values, updating);
    results[name] = result;
    if (failure) failures.push(failure);
  }
  const table = perViewModel(builds.views);
  mkdirSync(output, { recursive: true });
  writeFileSync(resultsFile, `${JSON.stringify({ schema: 2, rid, cases: results, builds, perViewModel: table.slopes }, null, 2)}\n`);

  if (reportOnly) {
    const values = ["| Metric | Values |", "| --- | ---: |",
      ...Object.entries(results).map(([name, result]) => `| \`${name}\` | ${result.values.map(value => value.toLocaleString("en-US")).join(" → ")} |`)];
    const summary = `## NativeAOT size on ${rid}\n\nNot gated: the budgets gate ${gatedRid} (D064.5).\n\n${values.join("\n")}\n\n${table.markdown}`;
    console.log(summary);
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary);
    process.exit(0);
  }
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
