// The dual-run golden gate of the Views generators (R7 G5, D080; R3 §5, slice 4). The .NET generator
// (tools/Runic.Application.Views.Codegen) writes each project's generated TypeScript and, beside its generated C#, the
// project's contract IR (obj/<configuration>/<framework>/runic-bridge/contract.ir.json). This gate runs the TypeScript
// emitter (@runic-artifex/views/codegen) on that IR and compares every file it writes with the file the .NET generator
// wrote, byte for byte, so that G6 can switch the build over to the emitter. It runs after a build:
//
//   bun eng/ci/views-dual-run.mjs                    every built example, the test projects and the generator goldens
//   bun eng/ci/views-dual-run.mjs --set examples     one set: examples, tests or goldens (repeatable)
//   bun eng/ci/views-dual-run.mjs --project <path>   a built project (its .csproj or directory), such as a template's
//   --emitter <module|frontend>                   the emitter: by default packages/web/views/dist/codegen/index.js;
//                                                 `frontend` takes the one the project's frontend installed
//   --configuration <name>                        the build's configuration (default $CONFIGURATION or Debug; for
//                                                 --project, the project's latest build)
//
// The .NET generator's record of its outputs (.runic-bridge-generation-cache.json beside the IR) names the TypeScript
// files it wrote and their SHA-256, so the comparison covers exactly that build's files, never a stale or hand-written
// one. The goldens set also compares the TypeScript goldens of the generator tests
// (tests/dotnet/Runic.Application.Testing.Tests/golden/*/*.ts.golden) with the emitter's output for the test assembly,
// with the golden tests' own fingerprint placeholder and nothing else normalized. Each difference is printed as a short
// diff with its file and cause; the exit code is 1 when any file differs or a project cannot be compared.
import { createHash } from "node:crypto";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { root, configuration as defaultConfiguration } from "../run.mjs";

const MANIFEST = ".runic-bridge-generation-cache.json";
const TESTING_PROJECT = "tests/dotnet/Runic.Application.Testing.Tests";
// Goldens whose name is not their module's: the repository keeps ASCII file names (DualRunGoldenTests).
const goldenModules = new Map([["dual-run/groessen.ts", "größen.ts"], ["dual-run/groessen.contract.d.ts", "größen.contract.d.ts"]]);
const corpusIndex = () => JSON.parse(readFileSync(join(root, "specs/application/contract-ir/corpus/index.json"), "utf8"));
// Examples that CI does not build (the corpus test lists the same).
const unbuiltExamples = new Set(["examples/dynamicdata"]);
const skippedDirectories = new Set(["node_modules", "bin", "dist", ".git", ".cache", "artifacts", "Frontend", "golden"]);

const slash = path => path.split(sep).join("/");
// A path below the checkout relative to it, any other (a template lane's project) absolute.
const rel = path => {
  const relativePath = slash(relative(root, path)) || ".";
  return relativePath.startsWith("../") ? slash(path) : relativePath;
};
const sha256 = bytes => createHash("sha256").update(bytes).digest("hex").toUpperCase();

/** The IRs a build wrote below a project directory: obj/<configuration>/<framework>/runic-bridge/contract.ir.json. */
function projectIrs(directory, configuration) {
  const obj = join(directory, "obj");
  if (!existsSync(obj)) return [];
  const configurations = configuration === null
    ? readdirSync(obj, { withFileTypes: true }).filter(item => item.isDirectory()).map(item => item.name) : [configuration];
  return configurations.flatMap(name => {
    const path = join(obj, name);
    if (!existsSync(path)) return [];
    return readdirSync(path, { withFileTypes: true }).filter(item => item.isDirectory())
      .map(item => join(path, item.name, "runic-bridge", "contract.ir.json")).filter(file => existsSync(file));
  });
}

/** Project directories (with a .csproj) below `directory`. */
function* projectDirectories(directory) {
  const entries = readdirSync(directory, { withFileTypes: true });
  if (entries.some(item => item.isFile() && item.name.endsWith(".csproj"))) yield directory;
  for (const item of entries)
    if (item.isDirectory() && !skippedDirectories.has(item.name) && item.name !== "obj")
      yield* projectDirectories(join(directory, item.name));
}

function projectFile(directory) {
  const projects = readdirSync(directory).filter(name => name.endsWith(".csproj"));
  if (projects.length !== 1) throw new Error(`${rel(directory)} has ${projects.length} .csproj files; pass the project file.`);
  return join(directory, projects[0]);
}

/** A comparison unit: one built project, its IR and the directory the .NET generator wrote its TypeScript to. */
function unit(set, project, ir) {
  const framework = ir.split(/[\\/]/).at(-3);
  const configuration = ir.split(/[\\/]/).at(-4);
  return { set, project, ir, framework, configuration, name: rel(dirname(project)) };
}

function discover(sets, projects, configuration, projectConfiguration) {
  const units = [];
  const problems = [];
  if (sets.has("examples")) {
    const built = new Set();
    for (const directory of projectDirectories(join(root, "examples"))) {
      const irs = projectIrs(directory, configuration);
      if (irs.length === 0) continue;
      built.add(rel(directory));
      for (const ir of irs) units.push(unit("examples", projectFile(directory), ir));
    }
    // Every example of the IR corpus has Views, so a missing IR is a missing build, not an example without Views.
    for (const entry of corpusIndex().entries) {
      const directory = slash(dirname(entry.project));
      if (!built.has(directory) && !unbuiltExamples.has(directory))
        problems.push(`${directory}: no ${configuration} contract.ir.json; build RunicSdk.Core.slnx (bun run build) first`);
    }
  }
  if (sets.has("tests") || sets.has("goldens")) {
    for (const directory of projectDirectories(join(root, "tests"))) {
      const isTesting = rel(directory) === TESTING_PROJECT;
      if (!(isTesting ? sets.has("goldens") || sets.has("tests") : sets.has("tests"))) continue;
      for (const ir of projectIrs(directory, configuration))
        units.push(unit(isTesting ? "goldens" : "tests", projectFile(directory), ir));
    }
    if (sets.has("goldens") && !units.some(item => item.set === "goldens"))
      problems.push(`${TESTING_PROJECT}: no ${configuration} contract.ir.json; build it first`);
  }
  for (const path of projects) {
    const absolute = resolve(path);
    const project = absolute.endsWith(".csproj") ? absolute : projectFile(absolute);
    // A project built outside the workspace scripts (a template's) may have any configuration: take its latest build.
    const irs = projectIrs(dirname(project), projectConfiguration)
      .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs);
    if (irs.length === 0) problems.push(`${path}: no contract.ir.json below obj; build the project first`);
    else units.push(unit("projects", project, irs[0]));
  }
  return { units, problems };
}

/** RunicApplicationFrontendGeneratedDirectory of a unit's project, from MSBuild's evaluation. */
async function generatedDirectory(item) {
  const { stdout } = await promisify(execFile)("dotnet", ["msbuild", item.project, "-nologo",
    "-getProperty:RunicApplicationFrontendGeneratedDirectory", `-p:Configuration=${item.configuration}`, `-p:TargetFramework=${item.framework}`]);
  const directory = stdout.trim();
  if (directory === "") throw new Error("MSBuild reports no RunicApplicationFrontendGeneratedDirectory");
  return resolve(dirname(item.project), directory);
}

/** Each unit's generated directory (or the error evaluating it), four MSBuild evaluations at a time. */
async function generatedDirectories(units) {
  const results = new Array(units.length);
  let next = 0;
  const worker = async () => {
    while (next < units.length) {
      const index = next++;
      results[index] = await generatedDirectory(units[index]).then(path => ({ path }), error => ({ error }));
    }
  };
  await Promise.all(Array.from({ length: Math.min(4, units.length) }, worker));
  return results;
}

/** The TypeScript files the .NET generator wrote, by name, from its output record, checked against the files on disk. */
function dotnetOutputs(item, directory) {
  const manifestPath = join(dirname(item.ir), MANIFEST);
  if (!existsSync(manifestPath))
    throw new Error(`no ${MANIFEST} beside the IR; build without RUNIC_BRIDGE_CODEGEN_CACHE=0 or RUNIC_BRIDGE_CODEGEN_FORCE`);
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  const files = new Map();
  for (const output of manifest.TypeScriptOutputs ?? []) {
    const path = join(directory, output.Path);
    if (!existsSync(path)) throw new Error(`${rel(path)}, which the .NET generator recorded, does not exist`);
    const bytes = readFileSync(path);
    if (sha256(bytes) !== output.Hash) throw new Error(`${rel(path)} changed after the .NET generator wrote it; rebuild`);
    files.set(output.Path, bytes.toString("utf8"));
  }
  return files;
}

/** The emitter module: an explicit path, the workspace's build, or the one the project's frontend installed. */
async function loadEmitter(option, generated) {
  let path = option;
  if (option === "frontend") {
    path = null;
    for (let directory = generated; directory !== dirname(directory); directory = dirname(directory)) {
      const manifest = join(directory, "node_modules", "@runic-artifex", "views", "package.json");
      if (!existsSync(manifest)) continue;
      const entry = JSON.parse(readFileSync(manifest, "utf8")).exports?.["./codegen"]?.import;
      if (!entry) throw new Error(`${rel(manifest)} has no ./codegen export`);
      path = join(dirname(manifest), entry);
      break;
    }
    if (path === null) throw new Error(`no @runic-artifex/views installed above ${rel(generated)}`);
  }
  path = resolve(path ?? join(root, "packages/web/views/dist/codegen/index.js"));
  if (!existsSync(path)) throw new Error(`the emitter ${rel(path)} does not exist; build @runic-artifex/views first`);
  return import(pathToFileURL(path).href);
}

// --- Differences -------------------------------------------------------------------------------------------------

const NUMBER = /-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g;
const nonAscii = text => /[^\x00-\x7f]/.test(text);

/** Why two versions of a file differ, as far as the gate can tell from the first lines that differ. */
function cause(expected, actual, removed, added) {
  if (expected === undefined) return "written by the emitter, not by the .NET generator";
  if (actual === undefined) return "written by the .NET generator, not by the emitter";
  if (expected.replaceAll("\r\n", "\n") === actual.replaceAll("\r\n", "\n")) return "line endings";
  if (expected.trimEnd() === actual.trimEnd()) return "trailing whitespace";
  const before = removed.join("\n"), after = added.join("\n");
  if (before.replace(NUMBER, n => String(Number(n))) === after.replace(NUMBER, n => String(Number(n))))
    return "number formatting (.NET writes a double's exponent form, such as 1E+20)";
  const unescape = text => text.replace(/\\u([0-9A-Fa-f]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)));
  if (unescape(before) === unescape(after)) return "string escaping (\\uXXXX)";
  if (nonAscii(before) || nonAscii(after)) return "a non-ASCII name or text";
  if (/sourceMappingURL|"sources"|"mappings"/.test(before + after)) return "declaration map";
  return "content";
}

/** Line hunks between two texts: a longest common subsequence of the lines between their common prefix and suffix. */
function hunks(expected, actual, label, context = 2, limit = 3) {
  const a = expected.split("\n"), b = actual.split("\n");
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length, endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) { endA--; endB--; }
  const midA = a.slice(start, endA), midB = b.slice(start, endB);
  // Edit script: [kind, lineA, lineB] with kind " ", "-" or "+"; the common prefix and suffix give the hunks context.
  const script = Array.from({ length: start }, (_, index) => [" ", index, index]);
  if (midA.length * midB.length <= 4_000_000) {
    const lcs = Array.from({ length: midA.length + 1 }, () => new Uint32Array(midB.length + 1));
    for (let i = midA.length - 1; i >= 0; i--)
      for (let j = midB.length - 1; j >= 0; j--)
        lcs[i][j] = midA[i] === midB[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
    let i = 0, j = 0;
    while (i < midA.length || j < midB.length) {
      if (i < midA.length && j < midB.length && midA[i] === midB[j]) { script.push([" ", start + i, start + j]); i++; j++; }
      else if (i < midA.length && (j === midB.length || lcs[i + 1][j] >= lcs[i][j + 1])) { script.push(["-", start + i, start + j]); i++; }
      else { script.push(["+", start + i, start + j]); j++; }
    }
  } else {
    for (let i = 0; i < midA.length; i++) script.push(["-", start + i, start]);
    for (let j = 0; j < midB.length; j++) script.push(["+", endA, start + j]);
  }
  for (let index = 0; index < a.length - endA; index++) script.push([" ", endA + index, endB + index]);
  const groups = [];
  let current = null;
  script.forEach(([kind], index) => {
    if (kind === " ") return;
    if (current && index - current.end <= context * 2) current.end = index;
    else groups.push(current = { begin: index, end: index });
  });
  const lines = (kind, index) => {
    const [, ia, ib] = script[index];
    return kind === "+" ? b[ib] : a[ia];
  };
  const shown = [];
  for (const group of groups.slice(0, limit)) {
    const first = script[group.begin];
    const text = [`@@ ${label} line ${first[1] + 1}, emitter line ${first[2] + 1} @@`];
    for (let index = Math.max(0, group.begin - context); index <= Math.min(script.length - 1, group.end + context); index++) {
      const [kind] = script[index];
      text.push(`${kind === "-" ? `- ${label.padEnd(8)}` : kind === "+" ? "+ emitter " : "          "}${clip(lines(kind, index))}`);
    }
    const changed = script.slice(group.begin, group.end + 1);
    shown.push({ text, removed: changed.filter(([kind]) => kind === "-").map(([, ia]) => a[ia]),
      added: changed.filter(([kind]) => kind === "+").map(([, , ib]) => b[ib]) });
  }
  return { shown, more: Math.max(0, groups.length - limit) };
}

const clip = line => (line.length > 160 ? line.slice(0, 157) + "..." : line);

export function compareFile(name, expected, actual, label = ".NET") {
  if (expected === actual) return null;
  if (expected === undefined || actual === undefined) return { name, cause: cause(expected, actual, [], []), text: [] };
  const { shown, more } = hunks(expected, actual, label);
  const first = shown[0] ?? { removed: [], added: [] };
  const text = shown.flatMap(item => item.text);
  if (shown.length === 0) text.push("(the files differ only in their final line break)");
  if (more > 0) text.push(`... ${more} more hunk${more === 1 ? "" : "s"}`);
  return { name, cause: cause(expected, actual, first.removed, first.added), text };
}

// --- Main --------------------------------------------------------------------------------------------------------

function parseArguments(argv) {
  const options = { sets: new Set(), projects: [], emitter: null, configuration: defaultConfiguration, configurationGiven: false };
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    const value = () => {
      if (index + 1 >= argv.length) throw new Error(`${argument} needs a value`);
      return argv[++index];
    };
    if (argument === "--set") {
      const set = value();
      if (!["examples", "tests", "goldens"].includes(set)) throw new Error(`unknown set ${set}; use examples, tests or goldens`);
      options.sets.add(set);
    } else if (argument === "--project") options.projects.push(value());
    else if (argument === "--emitter") options.emitter = value();
    else if (argument === "--configuration") { options.configuration = value(); options.configurationGiven = true; }
    else if (argument === "--help" || argument === "-h") options.help = true;
    else throw new Error(`unknown argument ${argument}`);
  }
  if (options.sets.size === 0 && options.projects.length === 0) for (const set of ["examples", "tests", "goldens"]) options.sets.add(set);
  return options;
}

/** Whether a build of `configuration` wrote the generator tests' IR, which every set needs. */
export const built = (configuration = defaultConfiguration) => projectIrs(join(root, TESTING_PROJECT), configuration).length > 0;

export async function main(argv = process.argv.slice(2), log = console.log) {
  const options = parseArguments(argv);
  if (options.help) {
    log(readFileSync(fileURLToPath(import.meta.url), "utf8").split("\n").filter(line => line.startsWith("//")).slice(0, 14)
      .map(line => line.slice(3)).join("\n"));
    return 0;
  }
  const { units, problems } = discover(options.sets, options.projects, options.configuration,
    options.configurationGiven ? options.configuration : null);
  const totals = new Map();
  const total = set => totals.get(set) ?? totals.set(set, { projects: 0, files: 0, identical: 0, different: 0, goldens: 0, goldensIdentical: 0 }).get(set);
  let failed = problems.length > 0;
  for (const problem of problems) log(`✗ ${problem}`);

  const directories = await generatedDirectories(units);
  for (const [index, item] of units.entries()) {
    const counts = total(item.set);
    counts.projects++;
    let generated, expected, files;
    try {
      if (directories[index].error) throw directories[index].error;
      generated = directories[index].path;
      expected = dotnetOutputs(item, generated);
      const emitter = await loadEmitter(options.emitter, generated);
      const ir = JSON.parse(readFileSync(item.ir, "utf8"));
      files = emitter.generate(ir, { outputDirectory: generated, irDirectory: dirname(item.ir) });
    } catch (error) {
      failed = true;
      const code = error?.code && /^RUNICBRIDGE/.test(error.code) ? ` (${error.code}${error.source ? ` at ${error.source}` : ""})` : "";
      log(`✗ ${item.name} [${item.configuration}/${item.framework}]: ${error?.message ?? error}${code}`);
      continue;
    }
    const names = [...new Set([...expected.keys(), ...files.keys()])].sort();
    const differences = names.map(name => compareFile(name, expected.get(name), files.get(name))).filter(Boolean);
    counts.files += names.length;
    counts.different += differences.length;
    counts.identical += names.length - differences.length;

    // The TypeScript goldens of the generator tests, from the emitter's output for the whole test assembly.
    const goldenDifferences = [];
    if (item.set === "goldens") {
      const goldens = join(root, TESTING_PROJECT, "golden");
      for (const set of readdirSync(goldens).sort()) {
        for (const name of readdirSync(join(goldens, set)).filter(file => /\.ts\.golden$/.test(file)).sort()) {
          const file = goldenModules.get(`${set}/${name.slice(0, -".golden".length)}`) ?? name.slice(0, -".golden".length);
          const golden = readFileSync(join(goldens, set, name), "utf8").replaceAll("\r\n", "\n");
          const placeholder = text => text?.replace(/[0-9A-F]{64}/g, "<fingerprint>");
          const emitted = placeholder(files.get(file));
          counts.goldens++;
          const difference = compareFile(`golden/${set}/${name}`, golden, emitted, "golden");
          if (!difference) { counts.goldensIdentical++; continue; }
          // The golden tests compare the .NET generator's output with the golden: when it differs too, the golden is stale.
          if (emitted !== undefined && placeholder(expected.get(file)) === emitted)
            difference.cause = "the golden is stale: the .NET generator's output differs from it as well (run the golden tests with RUNIC_UPDATE_GOLDEN=1)";
          goldenDifferences.push(difference);
        }
      }
    }

    const all = [...differences, ...goldenDifferences];
    const summary = `${names.length} files${item.set === "goldens" ? ` and ${counts.goldens} goldens` : ""}`;
    if (all.length === 0) {
      log(`✓ ${item.name} [${item.configuration}/${item.framework}]: ${summary} identical (${rel(generated)})`);
      continue;
    }
    failed = true;
    log(`✗ ${item.name} [${item.configuration}/${item.framework}]: ${all.length} of ${summary} differ (${rel(generated)})`);
    for (const difference of all) {
      log(`  ${difference.name}: ${difference.cause}`);
      for (const line of difference.text) log(`    ${line}`);
    }
  }

  log("");
  log("set        projects  files  identical  different  goldens");
  for (const [set, counts] of totals)
    log(`${set.padEnd(10)} ${String(counts.projects).padStart(8)} ${String(counts.files).padStart(6)} ${String(counts.identical).padStart(10)} ${String(counts.different).padStart(10)}  ${counts.goldens > 0 ? `${counts.goldensIdentical}/${counts.goldens}` : "-"}`);
  if (units.length === 0 && !failed) {
    log("No built project was found.");
    return 1;
  }
  log(failed ? "The emitter's output differs from the .NET generator's, or a project could not be compared." : "The emitter reproduces the .NET generator's output byte for byte.");
  return failed ? 1 : 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().then(code => process.exit(code), error => {
    console.error(error?.message ?? error);
    process.exit(2);
  });
}
