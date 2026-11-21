import { appendFileSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { basename, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { root, workspace, affectedComponents, engineeringOnly } from "../run.mjs";
import { platforms, nativeLegs, wpfLegs, packagingLegs, screenReaderLegs, screenReaderPaths, webKitCdpPaths, platformRules, neutralPaths, matchesPath }
  from "./platforms.mjs";

export const managedGroups = ["platform", "assets", "navigation", "application"];
// Suites that another job runs instead of the managed job. Runic.Desktop.Tests drives browsers and
// native windows, so the native job runs it on every OS; the views job runs the Views examples' tests
// with the examples' other checks.
export const jobSuites = {
  native: ["tests/dotnet/Runic.Desktop.Tests/Runic.Desktop.Tests.csproj"],
  views: ["examples/notes-view-first/Tests/NotesViewFirst.Tests.csproj", "examples/multi-window/Tests/MultiWindow.Tests.csproj",
    "examples/document-tabs/Tests/DocumentTabs.Tests.csproj", "examples/hosted-windows/Counter/Tests/CounterTool.Tests.csproj",
    "examples/hosted-region/Check/Tests/CheckTool.Tests.csproj"],
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

// The npm packages that run in Node declare `engines.node` (the browser-only packages declare none).
// Their range admits Node 24 and 26, so the node-26 job also runs their tests on Node 26 (D031.3).
export const nodeEngines = "^24.18.0 || ^26.0.0";
export function nodePackages() {
  return webTests().map(test => test.package)
    .filter(name => JSON.parse(readFileSync(resolve(root, "packages/web", name, "package.json"), "utf8")).engines?.node !== undefined);
}

// Jobs a plan can skip. `build` (which also checks generated files and packs the package candidates), the
// `native-libraries` it packs, and `engineering` (workflow lint, contracts, Markdown links) run in every other plan; only a merge
// queue entry that reuses a passed pull request run skips them too (reusedSkip). `plan` always runs.
// `verify` accepts a skipped job only when the plan skipped it.
export const skippableJobs = ["managed", "web", "node-26", "views", "package-consumers", "wpf", "templates", "native", "native-musl", "packaging",
  "screen-readers", "webkit-cdp"];
export const reusedSkip = [...skippableJobs, "native-libraries", "build", "engineering"];

// The CI component (eng/workspace.json) owns the planner, the gates, the local CI runner and other files that only the
// always-run jobs (plan, build, engineering, verify), other workflows or the coordinator read. Its checks are the
// engineering job's (eng/ci/*.test.mjs and the contracts), so a change to it alone plans no skippable job and no
// Windows or macOS leg. A change to a leg's runner or matrix values in platforms.mjs takes the ci:all-platforms label.
export const ciComponent = "ci";

const owner = file => Object.entries(workspace.components)
  .find(([, component]) => component.paths.some(path => file === path || file.startsWith(`${path}/`)))?.[0];

// Every Windows and macOS leg, with one reason for all of them.
const everyPlatform = reason => ({ platforms: [...platforms], why: Object.fromEntries(platforms.map(platform => [platform, reason])) });

// The Windows and macOS legs that the changed files need (eng/ci/platforms.mjs), with the first
// file that needed each. Engineering-only files need none, and neither does Markdown: package READMEs
// are packed, but no Windows or macOS leg reads them. The analyzers' release tracking files
// (AnalyzerReleases.*.md) are compiler inputs, so they keep their rule. Unowned files that are not
// neutral need all.
const documentation = file => file.endsWith(".md") && !/(^|\/)AnalyzerReleases\.[^/]*\.md$/.test(file);
export function affectedPlatforms(files) {
  if (files === null || files.length === 0) return everyPlatform("the changed files are unknown");
  const why = {};
  for (const file of files.filter(file => !engineeringOnly(file) && !documentation(file) && !neutralPaths.some(path => matchesPath(file, path)))) {
    const rule = platformRules.find(rule => rule.paths.some(path => matchesPath(file, path)));
    for (const platform of rule ? rule.platforms : owner(file) ? [] : platforms) why[platform] ??= file;
  }
  return { platforms: platforms.filter(platform => why[platform]), why };
}

// Which components and platforms a change affects, judged by this checkout's component map
// and platform rules. `files` lists every path a pull request adds, changes, deletes or
// renames (both names); `null` means unknown. An empty list is treated as unknown too, so a
// failed or empty diff can never skip jobs. Any unowned path that is not engineering-only
// (see engineeringOnly in eng/run.mjs) affects every component. `screenReaders` is whether a file
// the screen reader legs read changed (screenReaderPaths), `webKitCdp` whether one the webkit-cdp job reads did
// (webKitCdpPaths).
export function affected(files) {
  const full = files === null || files.length === 0 || files.some(file => !engineeringOnly(file) && !owner(file));
  return { full, components: full ? Object.keys(workspace.components) : affectedComponents(files),
    screenReaders: full || files.some(file => screenReaderPaths.some(path => matchesPath(file, path))),
    webKitCdp: full || files.some(file => webKitCdpPaths.some(path => matchesPath(file, path))), ...affectedPlatforms(files) };
}

// The legs of a platform-dependent job: those without a platform, and those whose platform is planned.
const legs = (list, planned) => list.filter(leg => !leg.platform || planned.includes(leg.platform));

// The jobs, suites and legs to run for the affected components and platforms, as defined by
// this checkout. Without platforms (an older base planner) every leg runs, without screenReaders every
// screen reader leg of the planned platforms, and without webKitCdp the webkit-cdp job. `noLegs` says why no
// Windows or macOS leg runs when none is planned.
export function planFor({ full, components, screenReaders: readers = true, webKitCdp = true, platforms: planned = platforms,
  why = everyPlatform("the base planner plans no platforms").why, noLegs = "the changed files need none" }) {
  const has = (...names) => names.some(name => components.includes(name));
  const managed = managedGroups.filter(group =>
    managedTests(root, "linux").some(test => test.group === group && (full || has(owner(test.path)))));
  const web = webTests().filter(test => full || has(owner(`packages/web/${test.package}`))).map(test => test.package);
  // The Svelte and SvelteKit type checks and the isolated npm framework consumers.
  const frameworks = full || has("svelte");
  // Package consumers and templates cover every shipped component (every component but the CI component).
  const packages = full || components.some(name => name !== ciComponent);
  // Desktop contracts, native windows, platform services, NativeAOT Views apps and Windows administration; and Runic.Spelling's
  // private word list when its Windows x64 rule planned that leg (DEV-SEC-88), whose step checks the Windows access list.
  const native = full || has("desktop", "platform", "application", "examples", "administration-windows")
    || (has("spelling") && planned.includes("win-x64")) ? legs(nativeLegs, planned) : [];
  // The WPF hosts and their packed consumer run on Windows only.
  const wpf = full || has("navigation", "application-wpf") ? legs(wpfLegs, planned) : [];
  // The template's packaging recipes build, install and open real packages on each OS; the platform packages they
  // exercise select them through the packaging component's dependencies.
  const packaging = full || has("packaging") ? legs(packagingLegs, planned) : [];
  // NVDA and Orca read the accessibility fixture in the embedded window.
  const screenReaders = full || readers ? legs(screenReaderLegs, planned) : [];
  const run = {
    managed: managed.length > 0,
    // One job runs each planned web package's tests and the framework consumers in named steps.
    web: web.length > 0 || frameworks,
    "node-26": web.some(name => nodePackages().includes(name)),
    views: full || has("examples"),
    "package-consumers": packages,
    wpf: wpf.length > 0,
    templates: packages,
    native: native.length > 0,
    // The musl RIDs run the same native checks in Alpine.
    "native-musl": native.length > 0,
    packaging: packaging.length > 0,
    "screen-readers": screenReaders.length > 0,
    // js-debug attaches through dotnet runic dev's CDP adapter to the template's Window on GTK 3 and GTK 4 (Linux).
    "webkit-cdp": full || webKitCdp,
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
    "screen-readers": { include: screenReaders },
    platforms: platforms.filter(platform => planned.includes(platform)),
    why: platforms.filter(platform => planned.includes(platform)).map(platform => `${platform}: ${why[platform]}`).join("; ")
      || `no Windows or macOS leg: ${noLegs}`,
    skip: skippableJobs.filter(job => !run[job]),
  };
}

export const plan = files => planFor(affected(files));

// A merge queue entry whose base and tree a passed pull request run already tested runs nothing:
// the code under test is identical (the plan step in ci.yml checks the run).
export const reusedPlan = url => ({
  full: false,
  components: [],
  managed: [],
  web: [],
  frameworks: false,
  native: { include: [] },
  wpf: { include: [] },
  packaging: { include: [] },
  "screen-readers": { include: [] },
  platforms: [],
  why: `reuses ${url}, which tested the same tree on the same base`,
  skip: reusedSkip,
});

// What kind of change a diff is, judged by the base checkout's planner from `git diff --no-renames -U0 <base> HEAD`
// (stdin of `--diff-kind`), so that a change cannot classify itself:
// - `release`: why a merge queue entry is a release or train step (D077.22), or null. An entry that changes
//   eng/Versions.props (a version bump or the post-release baselines), a TrainCommit in Directory.Packages.props (a
//   train candidate, its step 6 or its feed exit) or an assembled release notes file eng/release/notes/<version>.md (a
//   release pull request) is never reused from its pull request run and runs every Windows and macOS leg, because its
//   queue run stands for the green main run that the train gate and the release take (eng/release/ci-run.mjs).
// - `versionOnly`: the diff changes nothing but versions (E2). Every changed file is one of:
//   - a release notes file (eng/release/notes/**);
//   - a PublicAPI.Unshipped.txt to PublicAPI.Shipped.txt move: per directory, the lines removed from Unshipped are the
//     lines added to Shipped, except that a `*REMOVED*<line>` entry removes <line> from Shipped, and Unshipped gains none;
//   - a deleted CompatibilitySuppressions.xml (package validation's suppressions against the old baseline);
//   - eng/dependencies/package-downloads.json, where each changed entry changes only its version, to a package
//     validation baseline version that eng/Versions.props names, and its contentHash (the Runic baseline packages; a
//     .NET runtime pack is not a baseline);
//   - a version file (versionFiles: those `bun eng/version.mjs bump` rewrites, the regenerated CLI compatibility set and
//     Markdown outside the notes) whose every changed line differs from the line it replaces only in version numbers.
//   Anything else (code, a test, a project file, an added or removed line, a third-party lock entry whose hash changes,
//   global.json, Directory.Packages.props) makes the diff not version-only.
export const versionToken = /\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?/g;
export const versionFiles = [/^eng\/workspace\.json$/, /^eng\/Versions\.props$/, /^packages\/web\/[^/]+\/package\.json$/, /^bun\.lock$/,
  /(?:^|\/)packages(?:\.[^/]+)?\.lock\.json$/, /^packages\/web\/views\/src\/version\.ts$/, /^native\/Cargo\.(?:toml|lock)$/,
  /^tools\/dotnet-runic\/metadata\/runic\.compatibility-set\.json$/, /^(?!.*(?:^|\/)AnalyzerReleases\.[^/]*\.md$).*\.md$/];
const releaseNotes = /^eng\/release\/notes\/[^/]+\.md$/;
const publicApi = /(?:^|\/)PublicAPI\.(Shipped|Unshipped)\.txt$/;
const baselineProperty = /<(Runic\w*PackageValidationBaselineVersion)>([^<]*)<\/\1>/g;

// The files of a `git diff -U0` with each hunk's removed and added lines. A deleted file has `deleted`, a binary one
// `binary`; a file whose mode alone changed has no hunks.
export function parseDiff(text) {
  const files = [];
  let file, hunk;
  const path = value => {
    const quoted = /^"(.*)"$/.exec(value);
    let name = value;
    if (quoted) try { name = JSON.parse(value); } catch { name = quoted[1]; }
    return name === "/dev/null" ? null : name.replace(/^[ab]\//, "");
  };
  for (const line of text.split("\n")) {
    if (line.startsWith("diff --git ")) {
      // `diff --git a/x b/x` names the file when no ---/+++ follows (a mode change, a binary file).
      files.push(file = { path: /^diff --git a\/.* b\/(.*)$/.exec(line)?.[1] ?? null, deleted: false, binary: false, hunks: [] });
      hunk = null;
      continue;
    }
    if (!file) continue;
    if (!hunk) {
      if (line.startsWith("deleted file mode")) file.deleted = true;
      else if (line.startsWith("Binary files ") || line === "GIT binary patch") file.binary = true;
      else if (line.startsWith("--- ")) file.path = path(line.slice(4)) ?? file.path;
      else if (line.startsWith("+++ ")) file.path = path(line.slice(4)) ?? file.path;
      else if (line.startsWith("@@")) file.hunks.push(hunk = { removed: [], added: [] });
      continue;
    }
    if (line.startsWith("@@")) file.hunks.push(hunk = { removed: [], added: [] });
    else if (line.startsWith("-")) hunk.removed.push(line.slice(1));
    else if (line.startsWith("+")) hunk.added.push(line.slice(1));
  }
  return files;
}

const sameApartFromVersions = (removed, added) => removed.replace(versionToken, "<version>") === added.replace(versionToken, "<version>");
const pairs = hunk => hunk.removed.length === hunk.added.length ? hunk.removed.map((line, index) => [line, hunk.added[index]]) : null;
const sorted = lines => [...lines].sort();
const sameLines = (a, b) => a.length === b.length && sorted(a).every((line, index) => line === sorted(b)[index]);

// `baselines`: the package validation baseline versions eng/Versions.props names before the diff (the base checkout's).
export function diffKind(text, { baselines = currentBaselines() } = {}) {
  const files = parseDiff(text);
  const changed = file => file.hunks.flatMap(hunk => [...hunk.removed, ...hunk.added]);
  const release = files.find(file => file.path === "eng/Versions.props") ? "it changes eng/Versions.props"
    : files.find(file => file.path === "Directory.Packages.props" && changed(file).some(line => /\bTrainCommit=/.test(line)))
      ? "it changes a TrainCommit in Directory.Packages.props"
      : (file => file ? `it changes the assembled release notes ${file.path}` : null)(files.find(file => releaseNotes.test(file.path ?? "")));
  const known = new Set([...baselines, ...files.filter(file => file.path === "eng/Versions.props")
    .flatMap(file => file.hunks.flatMap(hunk => hunk.added.flatMap(line => [...line.matchAll(baselineProperty)].map(match => match[2]))))]);
  const versionHunk = hunk => pairs(hunk)?.every(([removed, added]) => sameApartFromVersions(removed, added)) ?? false;
  const baselineHunk = hunk => {
    const lines = pairs(hunk);
    if (!lines) return false;
    let version = false;
    for (const [removed, added] of lines) {
      if (/^\s*"contentHash": "[^"]*",?$/.test(removed) && /^\s*"contentHash": "[^"]*",?$/.test(added)) continue;
      const versions = added.match(versionToken) ?? [];
      if (!sameApartFromVersions(removed, added) || !versions.length || !versions.every(value => known.has(value))) return false;
      version = true;
    }
    return version;
  };
  const apis = new Map();
  const fileOk = file => {
    if (!file.path) return false;
    if (file.path.startsWith("eng/release/notes/")) return true;
    if (file.binary) return false;
    const api = publicApi.exec(file.path);
    if (api) {
      const directory = file.path.slice(0, file.path.length - api[0].length);
      const entry = apis.get(directory) ?? { Shipped: { removed: [], added: [] }, Unshipped: { removed: [], added: [] } };
      for (const hunk of file.hunks) { entry[api[1]].removed.push(...hunk.removed); entry[api[1]].added.push(...hunk.added); }
      apis.set(directory, entry);
      return file.hunks.length > 0;
    }
    if (/(?:^|\/)CompatibilitySuppressions\.xml$/.test(file.path)) return file.deleted;
    if (file.deleted || !file.hunks.length) return false;
    if (file.path === "eng/dependencies/package-downloads.json") return file.hunks.every(baselineHunk);
    return versionFiles.some(pattern => pattern.test(file.path)) && file.hunks.every(versionHunk);
  };
  const moved = ({ Shipped, Unshipped }) => Unshipped.added.length === 0
    && sameLines(Shipped.added, Unshipped.removed.filter(line => !line.startsWith("*REMOVED*")))
    && sameLines(Shipped.removed, Unshipped.removed.filter(line => line.startsWith("*REMOVED*")).map(line => line.slice("*REMOVED*".length)));
  const versionOnly = files.length > 0 && files.every(fileOk) && [...apis.values()].every(moved);
  return { versionOnly, release };
}

// The package validation baselines of this checkout's eng/Versions.props.
export function currentBaselines(base = root) {
  try {
    return [...readFileSync(resolve(base, "eng/Versions.props"), "utf8").matchAll(baselineProperty)].map(match => match[2]);
  } catch { return []; }
}

// Accept the base planner's --diff-kind output; anything else (an older base planner) is neither kind.
export function parseDiffKind(text) {
  try {
    const value = JSON.parse(text);
    return { versionOnly: value?.versionOnly === true, release: typeof value?.release === "string" && value.release ? value.release : null };
  } catch { return { versionOnly: false, release: null }; }
}

// E2: a pull request whose diff is version-only (diffKind) plans no Windows or macOS leg, and the Linux packaging legs
// whatever its components. Its merge queue entry plans its legs as usual (every leg when it changes eng/Versions.props,
// a release step), and the nightly run runs them all. A ci:all-platforms label still plans every leg.
export const versionOnly = input => ({ ...input, components: input.full ? input.components : [...new Set([...input.components, "packaging"])],
  platforms: [], why: {}, noLegs: "a version-only diff (eng/ci/plan.mjs, diffKind); its merge queue entry and the nightly run run them" });

// A plan as plan.mjs writes it (outputs, and the ci-plan record): anything else is not trusted to stand for a run.
export function validPlan(value) {
  const strings = list => Array.isArray(list) && list.every(item => typeof item === "string");
  const legs = name => Array.isArray(value?.[name]?.include) && value[name].include.every(leg => leg && typeof leg === "object" && !Array.isArray(leg));
  return Boolean(value) && typeof value.full === "boolean" && strings(value.components) && strings(value.managed) && strings(value.web)
    && typeof value.frameworks === "boolean" && ["native", "wpf", "packaging", "screen-readers"].every(legs) && strings(value.platforms)
    && strings(value.skip) && value.skip.every(job => reusedSkip.includes(job));
}

// A1 (D077.25): a push to main whose commit a passed merge queue run already tested runs only what that run's plan
// (`tested`, the ci-plan record of eng/ci/covered.mjs) skipped: the suites, legs and jobs of `planned` it did not run.
// When nothing is left, it runs nothing, build and engineering included, as a reused queue entry does; otherwise build
// runs for the remaining jobs and engineering, which the queue ran on the same tree, does not. native-libraries always
// runs: only runs on main save caches (.github/actions/dependency-cache), and the native server libraries' entries
// have exact keys (a new one with every native/ change, the post-release version bump included), so without this lookup,
// which builds and saves only a missing library, every pull request and queue run would build them until the nightly.
export const coveredSkip = reusedSkip.filter(job => job !== "native-libraries");
export function coveredPlan(planned, tested, url) {
  const ran = job => !tested.skip.includes(job);
  const key = leg => JSON.stringify(leg);
  const minus = (list, job, done) => ran(job) ? list.filter(item => !done.map(key).includes(key(item))) : list;
  const managed = planned.managed.filter(group => !(ran("managed") && tested.managed.includes(group)));
  const web = planned.web.filter(name => !(ran("web") && tested.web.includes(name)));
  const frameworks = planned.frameworks && !(ran("web") && tested.frameworks);
  const legs = Object.fromEntries(["native", "wpf", "packaging", "screen-readers"]
    .map(job => [job, planned.skip.includes(job) ? [] : minus(planned[job].include, job, tested[job].include)]));
  const runs = job => !planned.skip.includes(job) && !ran(job);
  const run = {
    managed: managed.length > 0,
    web: web.length > 0 || frameworks,
    "node-26": web.some(name => nodePackages().includes(name)),
    views: runs("views"),
    "package-consumers": runs("package-consumers"),
    wpf: legs.wpf.length > 0,
    templates: runs("templates"),
    native: legs.native.length > 0,
    "native-musl": runs("native-musl"),
    packaging: legs.packaging.length > 0,
    "screen-readers": legs["screen-readers"].length > 0,
    "webkit-cdp": runs("webkit-cdp"),
  };
  const skip = skippableJobs.filter(job => !run[job]);
  const nothing = skip.length === skippableJobs.length;
  const remaining = platforms.filter(platform => Object.values(legs).flat().some(leg => leg.platform === platform));
  return {
    full: false,
    components: planned.components,
    managed,
    web,
    frameworks,
    native: { include: legs.native },
    wpf: { include: legs.wpf },
    packaging: { include: legs.packaging },
    "screen-readers": { include: legs["screen-readers"] },
    platforms: remaining,
    why: nothing ? `covered by ${url}, which passed this commit with every job and leg this push plans`
      : `covered by ${url} except ${skippableJobs.filter(job => run[job]).join(", ")}${remaining.length ? ` (legs ${remaining.join(", ")})` : ""}`,
    skip: nothing ? coveredSkip : [...skip, "engineering"],
  };
}

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
    // A base planner older than the screen reader legs gives no screenReaders: they then run; likewise webKitCdp.
    const readers = {
      ...typeof value.screenReaders === "boolean" ? { screenReaders: value.screenReaders } : {},
      ...typeof value.webKitCdp === "boolean" ? { webKitCdp: value.webKitCdp } : {},
    };
    if (Array.isArray(value.platforms) && value.platforms.every(platform => platforms.includes(platform))
      && value.platforms.every(platform => typeof value.why?.[platform] === "string"))
      return { full, components, ...readers, platforms: value.platforms, why: value.why };
    return { full, components, ...readers };
  } catch {}
  return null;
}

// Pushes to main run every Linux job, and the Windows and macOS legs their before..after diff needs.
export const everyLinuxJob = input => ({ ...input, full: true, components: Object.keys(workspace.components) });
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
    `screen-readers=${JSON.stringify(result["screen-readers"])}`,
    `platforms=${JSON.stringify(result.platforms)}`,
    `why=${JSON.stringify(result.why)}`,
    `skip=${JSON.stringify(result.skip)}`,
  ].join("\n");
}

// The plan record a run uploads as its ci-plan artifact (CI_PLAN_RECORD names the file): what this run planned and, for a
// merge queue entry that reused a pull request run, that run. A push to main reads it from its commit's merge queue
// run (eng/ci/covered.mjs) to run only what that run skipped.
export const planRecord = (result, reused = null) => ({ schema: 1, plan: result, reused });
function record(result, reused) {
  const file = process.env.CI_PLAN_RECORD;
  if (!file) return;
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, JSON.stringify(planRecord(result, reused)));
}

// A covered push's lookup result: `{ url, plan }` with a valid plan, else null.
export function parseCovered(text) {
  try {
    const value = JSON.parse(text);
    return typeof value?.url === "string" && validPlan(value.plan) ? { url: value.url, plan: value.plan } : null;
  } catch { return null; }
}

// CI runs `--affected` with the base branch's planner on the pull request's (or push's)
// NUL-separated changed files (stdin), then `--plan <result>` with this checkout to
// enumerate suites and legs; `--all-linux` (pushes) plans every Linux job and
// `--all-platforms <reason>` every leg. `--diff-kind` (the base branch's planner, `git diff -U0` on stdin) prints
// diffKind's JSON; `--version-only` plans a version-only pull request. `--covered <json>` (a push to main, from
// eng/ci/covered.mjs) plans what the push's diff needs and keeps only what the merge queue run it names skipped; an
// unusable value plans every Linux job instead. `--all <reason>` plans everything, as do no
// arguments. `--files <paths>` previews a plan locally with this checkout alone. `--reused <url>`
// runs nothing, for a merge queue entry that reuses the passed pull request run at <url>.
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [mode, ...rest] = process.argv.slice(2);
  if (mode === "--affected") {
    console.log(JSON.stringify(affected(readFileSync(0, "utf8").split("\0").filter(Boolean))));
  } else if (mode === "--diff-kind") {
    console.log(JSON.stringify(diffKind(readFileSync(0, "utf8"))));
  } else if (mode === "--reused" && rest[0]) {
    const result = reusedPlan(rest[0]);
    console.error(`Running nothing: ${result.why}.`);
    record(result, rest[0]);
    console.log(outputs(result));
  } else {
    const option = name => rest.includes(name) ? rest[rest.indexOf(name) + 1] ?? "" : undefined;
    const covered = option("--covered") === undefined ? null : parseCovered(option("--covered"));
    let input = mode === "--plan" ? parseAffected(rest[0] ?? "") : mode === "--files" ? affected(rest) : null;
    if (input && (rest.includes("--all-linux") || (option("--covered") !== undefined && !covered))) input = everyLinuxJob(input);
    if (input && rest.includes("--version-only")) input = versionOnly(input);
    if (input && option("--all-platforms") !== undefined) input = allPlatforms(input, option("--all-platforms") || "requested");
    let result = input ? planFor(input)
      : planFor(allPlatforms(affected(null), mode === "--all" ? rest[0] || "requested" : mode === "--plan" ? "the base planner gave no usable result" : "no diff to plan from"));
    if (covered) {
      result = coveredPlan(result, covered.plan, covered.url);
      const ran = skippableJobs.filter(job => !result.skip.includes(job));
      const summary = `This commit passed merge queue run ${covered.url}. This push runs ${ran.length ? `only what that run skipped: ${ran.join(", ")}` : "nothing else"} (eng/ci/README.md, "Merge queue").`;
      console.error(summary);
      if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${summary}\n`);
    } else console.error(result.full ? `Planning every job${mode === "--plan" && !input ? " (the base planner gave no usable result)" : ""}.`
      : `Components ${result.components.join(", ") || "none"}; skipping ${result.skip.join(", ") || "nothing"}.`);
    console.error(`Windows and macOS legs: ${result.why}`);
    record(result, null);
    console.log(outputs(result));
  }
}
