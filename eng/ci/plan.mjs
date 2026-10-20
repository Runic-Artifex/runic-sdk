import { readFileSync, rmSync } from "node:fs";
import { basename, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { root, workspace, affectedComponents, engineeringOnly } from "../run.mjs";
import { platforms, nativeLegs, wpfLegs, packagingLegs, platformRules, neutralPaths, matchesPath } from "./platforms.mjs";

export const managedGroups = ["platform", "assets", "navigation", "application"];
// Suites that another job runs instead of the managed job. Runic.Desktop.Tests drives browsers and
// native windows, so the native job runs it on every OS; the views job runs the Notes example's tests
// with the example's other checks.
export const jobSuites = {
  native: ["tests/dotnet/Runic.Desktop.Tests/Runic.Desktop.Tests.csproj"],
  views: ["examples/notes-view-first/Tests/NotesViewFirst.Tests.csproj", "examples/multi-window/Tests/MultiWindow.Tests.csproj"],
};
export function managedTests(base = root, platform = process.platform) {
  return [...readFileSync(resolve(base, "RunicSdk.Core.slnx"), "utf8").matchAll(/<Project Path="([^"]+)"/g)]
    .map(([, path]) => path)
    .filter(path => {
      if (!/Tests\.csproj$/.test(path) || Object.values(jobSuites).flat().includes(path)) return false;
      const project = readFileSync(resolve(base, path), "utf8");
      return /<OutputType>Exe<\/OutputType>/.test(project)
        && (platform === "win32" || !/<TargetFramework>[^<]*-windows<\/TargetFramework>/.test(project));
    })
    .map(path => ({ path, group: path.includes("Runic.Navigation.") || path.startsWith("examples/wpf-navigation/") ? "navigation"
      : path.includes("Runic.Application.") || path.startsWith("examples/wpf-hybrid-editor/") ? "application"
      : path.includes("Runic.Assets") ? "assets"
      : "platform" }));
}

// TUnit suites run on Microsoft.Testing.Platform (CONTRIBUTING.md). They reference TUnit.Engine, or
// the TUnit metapackage (the examples), which brings the same report extensions; other executables
// take no test-platform arguments. A TUnit suite writes a TRX report and Cobertura coverage to
// <directory>/<suite> (artifacts/test-results/<suite>; the managed CI job passes
// artifacts/test-results/<group> and uploads each group). MTP rejects report options
// next to --list-tests, --help and --info, so those runs write no reports.
export function testReportArguments(path, extra = [], base = root, directory = "artifacts/test-results") {
  const project = resolve(base, path);
  if (!/<PackageReference Include="TUnit(\.Engine)?"/.test(readFileSync(project, "utf8"))
    || extra.some(arg => ["--list-tests", "--help", "-h", "-?", "--info"].includes(arg))) return [];
  const name = basename(project, ".csproj");
  const results = resolve(base, directory, name);
  rmSync(results, { recursive: true, force: true });
  return ["--results-directory", results, "--report-trx", "--report-trx-filename", `${name}.trx`,
    "--coverage", "--coverage-output-format", "cobertura", "--coverage-output", `${name}.cobertura.xml`];
}

// Application fixtures. The application suite builds and runs them after its test
// executables; each one exits non-zero when its proof fails.
// An `aot` fixture pins linux-x64, so it is published with NativeAOT and run on Linux only.
export const applicationFixtures = [
  { path: "tests/fixtures/application/reactiveui-reactive-flavor/ReactiveUiReactiveFlavorProof.csproj" },
  { path: "tests/fixtures/application/reactiveui-reactive-flavor/ReactiveUiReactiveSourceGeneratorProof.csproj" },
  { path: "tests/fixtures/application/reactiveui25-aot/ReactiveUi25AotProof.csproj", aot: true },
];

// The commands that build and run the application fixtures on `platform`.
export function applicationFixtureCommands(configuration, base = root, platform = process.platform) {
  return applicationFixtures.flatMap(({ path, aot }) => {
    const project = resolve(base, path);
    if (!aot) return [["dotnet", ["run", "--project", project, "-c", configuration]]];
    if (platform !== "linux") return [];
    const name = path.split("/").at(-1).replace(/\.csproj$/, "");
    const output = resolve(base, "artifacts/application-fixtures", name);
    return [
      ["dotnet", ["publish", project, "-c", configuration, "-p:IlcTreatWarningsAsErrors=true", "-o", output]],
      [resolve(output, name), []],
    ];
  });
}

export function webTests() {
  return workspace.npm.filter(item => JSON.parse(readFileSync(resolve(root, item.path, "package.json"), "utf8")).scripts?.test)
    .map(item => ({ package: item.path.split("/").at(-1), node: item.name === "@runic-artifex/vite-plugin-runic" }));
}

// Jobs that always run: `plan`, `build` (which also checks generated files and packs the
// package candidates) and `engineering` (workflow lint, contracts, Markdown links). `verify`
// accepts a skipped job only when it is listed here and the plan skipped it.
export const skippableJobs = ["managed", "web", "views", "package-consumers", "wpf", "templates", "native", "native-musl", "packaging"];

const owner = file => Object.entries(workspace.components)
  .find(([, component]) => component.paths.some(path => file === path || file.startsWith(`${path}/`)))?.[0];

// Every Windows and macOS leg, with one reason for all of them.
const everyPlatform = reason => ({ platforms: [...platforms], why: Object.fromEntries(platforms.map(platform => [platform, reason])) });

// The Windows and macOS legs that the changed files need (eng/ci/platforms.mjs), with the first
// file that needed each. Engineering-only files need none; unowned files that are not neutral need all.
export function affectedPlatforms(files) {
  if (files === null || files.length === 0) return everyPlatform("the changed files are unknown");
  const why = {};
  for (const file of files.filter(file => !engineeringOnly(file) && !neutralPaths.some(path => matchesPath(file, path)))) {
    const rule = platformRules.find(rule => rule.paths.some(path => matchesPath(file, path)));
    for (const platform of rule ? rule.platforms : owner(file) ? [] : platforms) why[platform] ??= file;
  }
  return { platforms: platforms.filter(platform => why[platform]), why };
}

// Which components and platforms a change affects, judged by this checkout's component map
// and platform rules. `files` lists every path a pull request adds, changes, deletes or
// renames (both names); `null` means unknown. An empty list is treated as unknown too, so a
// failed or empty diff can never skip jobs. Any unowned path that is not engineering-only
// (see engineeringOnly in eng/run.mjs) affects every component.
export function affected(files) {
  const full = files === null || files.length === 0 || files.some(file => !engineeringOnly(file) && !owner(file));
  return { full, components: full ? Object.keys(workspace.components) : affectedComponents(files), ...affectedPlatforms(files) };
}

// The legs of a platform-dependent job: those without a platform, and those whose platform is planned.
const legs = (list, planned) => list.filter(leg => !leg.platform || planned.includes(leg.platform));

// The jobs, suites and legs to run for the affected components and platforms, as defined by
// this checkout. Without platforms (an older base planner) every leg runs.
export function planFor({ full, components, platforms: planned = platforms, why = everyPlatform("the base planner plans no platforms").why }) {
  const has = (...names) => names.some(name => components.includes(name));
  const managed = managedGroups.filter(group =>
    managedTests(root, "linux").some(test => test.group === group && (full || has(owner(test.path)))));
  const web = webTests().filter(test => full || has(owner(`packages/web/${test.package}`))).map(test => test.package);
  // The Svelte and SvelteKit type checks and the isolated npm framework consumers.
  const frameworks = full || has("svelte");
  // Package consumers and templates cover every shipped component.
  const packages = full || components.length > 0;
  // Desktop contracts, native windows, platform services, NativeAOT Views apps and Windows administration.
  const native = full || has("desktop", "platform", "application", "examples", "administration-windows") ? legs(nativeLegs, planned) : [];
  // The WPF hosts and their packed consumer run on Windows only.
  const wpf = full || has("navigation", "application-wpf") ? legs(wpfLegs, planned) : [];
  // The template's packaging recipes build, install and open real packages on each OS; the platform packages they
  // exercise select them through the packaging component's dependencies.
  const packaging = full || has("packaging") ? legs(packagingLegs, planned) : [];
  const run = {
    managed: managed.length > 0,
    // One job runs each planned web package's tests and the framework consumers in named steps.
    web: web.length > 0 || frameworks,
    views: full || has("examples"),
    "package-consumers": packages,
    wpf: wpf.length > 0,
    templates: packages,
    native: native.length > 0,
    // The musl RIDs run the same native checks in Alpine.
    "native-musl": native.length > 0,
    packaging: packaging.length > 0,
  };
  return {
    full,
    components: full ? Object.keys(workspace.components) : components,
    managed,
    web,
    frameworks,
    native: { include: native },
    wpf: { include: wpf },
    packaging: { include: packaging },
    platforms: platforms.filter(platform => planned.includes(platform)),
    why: platforms.filter(platform => planned.includes(platform)).map(platform => `${platform}: ${why[platform]}`).join("; ")
      || "no Windows or macOS leg: the changed files need none",
    skip: skippableJobs.filter(job => !run[job]),
  };
}

export const plan = files => planFor(affected(files));

// Accept another checkout's affected() result. Anything else (such as an older base
// planner that does not support --affected) plans every job. A result without
// platforms, from a base planner older than platform planning, plans every platform.
export function parseAffected(text) {
  try {
    const value = JSON.parse(text);
    const known = Object.keys(workspace.components);
    if (typeof value?.full !== "boolean" || !Array.isArray(value.components) || !value.components.every(name => known.includes(name)))
      return null;
    const { full, components } = value;
    if (Array.isArray(value.platforms) && value.platforms.every(platform => platforms.includes(platform))
      && value.platforms.every(platform => typeof value.why?.[platform] === "string"))
      return { full, components, platforms: value.platforms, why: value.why };
    return { full, components };
  } catch {}
  return null;
}

// Scheduled and manual runs, and pull requests labelled ci:all-platforms, run every leg.
export const allPlatforms = (input, reason) => ({ ...input, ...everyPlatform(reason) });

export function outputs(result) {
  return [
    `full=${result.full}`,
    `components=${JSON.stringify(result.components)}`,
    `managed=${JSON.stringify(result.managed)}`,
    `web=${JSON.stringify(result.web)}`,
    `frameworks=${result.frameworks}`,
    `native=${JSON.stringify(result.native)}`,
    `wpf=${JSON.stringify(result.wpf)}`,
    `packaging=${JSON.stringify(result.packaging)}`,
    `platforms=${JSON.stringify(result.platforms)}`,
    `why=${JSON.stringify(result.why)}`,
    `skip=${JSON.stringify(result.skip)}`,
  ].join("\n");
}

// CI runs `--affected` with the base branch's planner on the pull request's (or push's)
// NUL-separated changed files (stdin), then `--plan <result>` with this checkout to
// enumerate suites and legs; `--all-platforms <reason>` plans every leg. `--all <reason>` plans everything, as do no
// arguments. `--files <paths>` previews a plan locally with this checkout alone.
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [mode, ...rest] = process.argv.slice(2);
  if (mode === "--affected") {
    console.log(JSON.stringify(affected(readFileSync(0, "utf8").split("\0").filter(Boolean))));
  } else {
    const option = name => rest.includes(name) ? rest[rest.indexOf(name) + 1] ?? "" : undefined;
    let input = mode === "--plan" ? parseAffected(rest[0] ?? "") : mode === "--files" ? affected(rest) : null;
    if (input && option("--all-platforms") !== undefined) input = allPlatforms(input, option("--all-platforms") || "requested");
    const result = input ? planFor(input)
      : planFor(allPlatforms(affected(null), mode === "--all" ? rest[0] || "requested" : mode === "--plan" ? "the base planner gave no usable result" : "no diff to plan from"));
    console.error(result.full ? `Planning every job${mode === "--plan" && !input ? " (the base planner gave no usable result)" : ""}.`
      : `Components ${result.components.join(", ") || "none"}; skipping ${result.skip.join(", ") || "nothing"}.`);
    console.error(`Windows and macOS legs: ${result.why}`);
    console.log(outputs(result));
  }
}
