import { execFileSync } from "node:child_process";
import { appendFileSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
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
//
// `context` (workflowContext) lets ci.yml edits, the composite actions and CI scripts the jobs call, and the release and
// pack scripts plan only the jobs they change (R2, scopedJobs). Without it they plan everything, as before.
export function affected(files, context = null) {
  const scoped = context && files?.length ? scopedJobs(files, context) : null;
  if (scoped) {
    const rest = files.filter(file => !scoped.files.includes(file));
    const unscoped = unscopedAffected(files);
    const result = rest.length ? unscopedAffected(rest)
      : { full: false, components: [], screenReaders: false, webKitCdp: false, platforms: [], why: {} };
    if (!result.full) return { ...result, jobs: scoped.jobs, unscoped };
  }
  return unscopedAffected(files);
}
function unscopedAffected(files) {
  const full = files === null || files.length === 0 || files.some(file => !engineeringOnly(file) && !owner(file));
  return { full, components: full ? Object.keys(workspace.components) : affectedComponents(files),
    screenReaders: full || files.some(file => screenReaderPaths.some(path => matchesPath(file, path))),
    webKitCdp: full || files.some(file => webKitCdpPaths.some(path => matchesPath(file, path))), ...affectedPlatforms(files) };
}

// R2 (D077.26): job-scoped planning. A pull request or merge queue entry that edits ci.yml runs the jobs whose
// definition changed instead of every job. The base checkout's planner compares the base's ci.yml (its own) with the
// head's (the working directory's) job by job, ignoring comment lines and blank lines; a job's definition is its block
// (body, matrix, `needs`, conditions) and the composite actions and CI scripts it names. Everything is planned (the
// file stays a full cause) when:
// - anything outside the job blocks changed (`name`, `on`, `permissions`, `concurrency`, `env`, `defaults`, ...);
// - a job was added, removed or renamed (verify's `needs` changes too);
// - a shared job changed: `plan`, `verify`, or a job another job needs (`native-libraries`, `build`); a changed job
//   also plans the skippable jobs it needs;
// - the two files cannot be compared (no head or base file, a file that does not parse, or identical text although
//   the diff lists ci.yml);
// - the diff also changes a release or train input (eng/Versions.props, Directory.Packages.props, assembled release
//   notes): such a merge queue run stands for the green main run (D077.22) and runs everything as before.
// A changed file under .github/actions/<name>/ plans the jobs that name that action, directly or through another
// action; an unowned CI script under eng/ci/ the jobs that name it, directly or through the actions and CI scripts
// that name it. Such a file stays a full cause when a shared job uses it, when no job uses it, or when any other file
// names it (an MSBuild file, a test support file, eng/run.mjs, ...), since the planner cannot attribute those uses.
// The release and pack scripts (packScripts) plan the jobs that download the packed packages (packageArtifact).
// A planned job runs whole: every managed group or web package it runs, and every leg the changed file's platform
// rules allow (affectedPlatforms of that file: a ci.yml job edit every leg, a neutral script the Linux legs only).
// The other changed files plan as usual. Pushes to main ignore the scoping (`unscoped`), and the nightly and
// manual runs have no diff to scope.
export const workflowPath = ".github/workflows/ci.yml";
export const packScripts = ["eng/release", "eng/verify-packages.mjs", "eng/version.mjs"];
// The artifact of packed packages that the build job uploads; the jobs that download it test the packages.
export const packageArtifact = "runic-sdk-${{ github.run_id }}";
export const releaseInputs = file => file === "eng/Versions.props" || file === "Directory.Packages.props" || releaseNotes.test(file);

// The normalized parts of a workflow: the text outside `jobs:` and each job's block, without comment lines, blank
// lines and trailing spaces outside block scalars (`run: |`), and each job's `needs`. Null when it does not have the
// expected shape (top-level keys at column 0, job keys indented by two spaces).
export function workflowParts(text) {
  if (typeof text !== "string" || text.includes("\t")) return null;
  const outside = [], jobs = new Map(), needs = new Map();
  let section = null, job = null, scalar = null;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.replace(/\s+$/, "");
    const indent = line.length - line.trimStart().length;
    if (scalar !== null) {
      if (line === "") continue;
      if (indent > scalar) { (job ? jobs.get(job) : outside).push(line); continue; }
      scalar = null;
    }
    if (line === "" || line.trimStart().startsWith("#")) continue;
    if (indent === 0) {
      section = /^([A-Za-z_][\w-]*):/.exec(line)?.[1] ?? null;
      if (section === null) return null;
      job = null;
      outside.push(line);
    } else if (section === "jobs" && indent === 2) {
      const name = /^ {2}([A-Za-z_][\w-]*):\s*(#.*)?$/.exec(line)?.[1];
      if (!name || jobs.has(name)) return null;
      jobs.set(job = name, [line]);
    } else if (section === "jobs") {
      if (!job) return null;
      jobs.get(job).push(line);
    } else if (section !== null) outside.push(line);
    else return null;
    // A block scalar's lines are indented more than its key: `key: |`, `- key: >-`, `- |`.
    if (/(?:^|:\s|-\s)\s*[|>][+-]?\d?[+-]?(?:\s+#.*)?$/.test(line.trimStart()))
      scalar = indent + (/^(?:-\s+)+/.exec(line.trimStart())?.[0].length ?? 0);
  }
  if (!jobs.size) return null;
  for (const [name, lines] of jobs) {
    const index = lines.findIndex(line => /^ {4}needs:/.test(line));
    if (index < 0) { needs.set(name, []); continue; }
    const value = lines[index].replace(/^ {4}needs:\s*/, "");
    const following = [];
    for (const line of lines.slice(index + 1)) { if (!/^ {6}- /.test(line)) break; following.push(line.replace(/^ {6}- /, "")); }
    const list = value.startsWith("[") && value.endsWith("]") ? value.slice(1, -1).split(",").map(item => item.trim()).filter(Boolean)
      : value ? [value] : following;
    if (!list.length || !list.every(item => /^[A-Za-z_][\w-]*$/.test(item.replace(/^["']|["']$/g, "")))) return null;
    needs.set(name, list.map(item => item.replace(/^["']|["']$/g, "")));
  }
  return { outside: outside.join("\n"), jobs: new Map([...jobs].map(([name, lines]) => [name, lines.join("\n")])), needs };
}

// The jobs of base and head that a change cannot scope: plan, verify, and the jobs another job (but verify) needs.
const sharedJobs = parts => new Set(["plan", "verify", ...[...parts.needs].filter(([name]) => name !== "verify").flatMap(([, list]) => list)]);

// The jobs and files of a diff that plan job by job (see above), or null when none do. `context`: { base, head } (the
// two ci.yml texts) and referrers(key), the tracked files of base and head whose text contains `key`.
export function scopedJobs(files, context) {
  if (files.some(releaseInputs)) return null;
  const base = workflowParts(context.base), head = workflowParts(context.head);
  if (!base || !head) return null;
  const shared = new Set([...sharedJobs(base), ...sharedJobs(head)]);
  const jobs = {};
  const add = (names, file) => {
    const { platforms: planned, why } = affectedPlatforms([file]);
    for (const name of names.filter(name => skippableJobs.includes(name))) {
      const entry = jobs[name] ??= { platforms: [], why: {} };
      for (const platform of planned) if (!entry.platforms.includes(platform)) { entry.platforms.push(platform); entry.why[platform] = why[platform]; }
      entry.platforms = platforms.filter(platform => entry.platforms.includes(platform));
      entry.file ??= file;
    }
  };
  const scoped = [];
  // ci.yml: the jobs whose block differs.
  if (files.includes(workflowPath)) {
    const names = [...new Set([...base.jobs.keys(), ...head.jobs.keys()])];
    const changed = names.filter(name => base.jobs.get(name) !== head.jobs.get(name));
    const sameJobs = names.every(name => base.jobs.has(name) && head.jobs.has(name));
    // A changed job also plans the jobs it needs, so that it does not wait on a skipped one.
    const planned = [...new Set([...changed, ...changed.flatMap(name => head.needs.get(name) ?? [])])];
    if (context.base !== context.head && base.outside === head.outside && sameJobs && !changed.some(name => shared.has(name))) {
      for (const name of planned.filter(name => skippableJobs.includes(name))) {
        jobs[name] ??= { platforms: [], why: {} };
        jobs[name].platforms = [...platforms];
        for (const platform of platforms) jobs[name].why[platform] ??= `${workflowPath} (job ${name})`;
        jobs[name].file ??= workflowPath;
      }
      scoped.push(workflowPath);
    }
  }
  // Composite actions and CI scripts: the jobs that name them, through the actions and scripts that name them.
  const mentioning = (key, parts) => [...parts.jobs].filter(([, block]) => block.includes(key)).map(([name]) => name);
  const keyOf = file => /^\.github\/actions\/[^/]+/.exec(file)?.[0] ?? (file.startsWith("eng/ci/") ? basename(file) : null);
  const users = (file, seen = new Set()) => {
    const key = keyOf(file);
    if (!key || seen.has(key)) return seen.has(key) ? [] : null;
    seen.add(key);
    if (base.outside.includes(key) || head.outside.includes(key)) return null;
    const names = new Set([...mentioning(key, base), ...mentioning(key, head)]);
    const referrers = context.referrers(key);
    if (!Array.isArray(referrers)) return null;
    for (const referrer of referrers) {
      if (referrer === workflowPath || keyOf(referrer) === key || engineeringOnly(referrer) || owner(referrer) === ciComponent) continue;
      if (!/^(\.github\/actions\/|eng\/ci\/)/.test(referrer)) return null;
      const more = users(referrer, seen);
      if (more === null) return null;
      for (const name of more) names.add(name);
    }
    return [...names];
  };
  for (const file of files.filter(file => file !== workflowPath && !engineeringOnly(file) && !owner(file))) {
    if (packScripts.some(path => matchesPath(file, path))) {
      const names = [...new Set([...mentioning(packageArtifact, base), ...mentioning(packageArtifact, head)])].filter(name => name !== "build");
      if (!names.length || names.some(name => shared.has(name))) continue;
      add(names, file);
      scoped.push(file);
    } else if (/^(\.github\/actions\/|eng\/ci\/)/.test(file)) {
      const names = users(file);
      if (!names?.length || names.some(name => shared.has(name))) continue;
      add(names, file);
      scoped.push(file);
    }
  }
  return scoped.length ? { files: scoped, jobs: Object.fromEntries(Object.entries(jobs).map(([name, { platforms: list, why, file }]) =>
    [name, { platforms: list, why: Object.fromEntries(list.map(platform => [platform, why[platform]])), file }])) } : null;
}

// The context of the plan step: the base planner's own checkout (`base`) against the head checkout (`head`, the working
// directory). Null when they are the same directory, so a run without a separate base checkout never scopes.
export function checkoutContext(base = root, head = process.cwd()) {
  const real = path => { try { return realpathSync(path); } catch { return null; } };
  if (!real(base) || !real(head) || real(base) === real(head)) return null;
  const read = directory => { try { return readFileSync(resolve(directory, workflowPath), "utf8"); } catch { return null; } };
  return { base: read(base), head: read(head), referrers: key => {
    const found = [base, head].map(directory => grep(directory, key));
    return found.includes(null) ? null : [...new Set(found.flat())];
  } };
}
// For a local --files preview: the `base` revision (the merge base with origin/main by default) against the `head`
// revision, or the working tree without one.
export function revisionContext(base, head = null, directory = root) {
  try {
    const git = (...args) => execFileSync("git", args, { cwd: directory, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });
    const commits = [base, head].map(revision => revision && git("rev-parse", "--verify", `${revision}^{commit}`).trim());
    const text = commit => commit ? git("show", `${commit}:${workflowPath}`) : readFileSync(resolve(directory, workflowPath), "utf8");
    const found = (key, commit) => grep(directory, key, commit)?.map(line => commit ? line.slice(commit.length + 1) : line) ?? null;
    return { base: text(commits[0]), head: text(commits[1]), referrers: key => {
      const lists = commits.map(commit => found(key, commit));
      return lists.includes(null) ? null : [...new Set(lists.flat())];
    } };
  } catch { return null; }
}
function grep(directory, key, revision = null) {
  try {
    return execFileSync("git", ["grep", "-l", "-F", "-e", key, ...(revision ? [revision] : [])], { cwd: directory, encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"] }).split("\n").filter(Boolean);
  } catch (error) {
    // git grep exits 1 when nothing matches; anything else leaves the file unattributed.
    return error.status === 1 ? [] : null;
  }
}

// The legs of a platform-dependent job: those without a platform, and those whose platform is planned.
const platformLegs = (list, planned) => list.filter(leg => !leg.platform || planned.includes(leg.platform));

// The jobs, suites and legs to run for the affected components and platforms, as defined by
// this checkout. Without platforms (an older base planner) every leg runs, without screenReaders every
// screen reader leg of the planned platforms, and without webKitCdp the webkit-cdp job. `noLegs` says why no
// Windows or macOS leg runs when none is planned. `jobs` (scopedJobs) are jobs a ci.yml, action or script change planned:
// each runs whole, with its legs of the Windows and macOS platforms listed for it.
export function planFor({ full, components, screenReaders: readers = true, webKitCdp = true, platforms: planned = platforms,
  why = everyPlatform("the base planner plans no platforms").why, noLegs = "the changed files need none", jobs = {} }) {
  const has = (...names) => names.some(name => components.includes(name));
  const forced = name => Object.hasOwn(jobs, name);
  const managed = managedGroups.filter(group => forced("managed")
    || managedTests(root, "linux").some(test => test.group === group && (full || has(owner(test.path)))));
  // node-26 runs the planned web packages that run in Node, so a planned node-26 job plans them for web too.
  const web = webTests().filter(test => full || forced("web") || (forced("node-26") && nodePackages().includes(test.package))
    || has(owner(`packages/web/${test.package}`))).map(test => test.package);
  // The Svelte and SvelteKit type checks and the isolated npm framework consumers.
  const frameworks = full || forced("web") || has("svelte");
  // A planned job's legs: those of its own platforms, and those the components plan.
  const legs = (list, job, componentLegs) => forced(job)
    ? list.filter(leg => !leg.platform || jobs[job].platforms.includes(leg.platform) || componentLegs.includes(leg)) : componentLegs;
  // Package consumers and templates cover every shipped component (every component but the CI component).
  const packages = full || components.some(name => name !== ciComponent);
  // Desktop contracts, native windows, platform services, NativeAOT Views apps and Windows administration; and Runic.Spelling's
  // private word list when its Windows x64 rule planned that leg (DEV-SEC-88), whose step checks the Windows access list.
  const nativeChecks = full || has("desktop", "platform", "application", "examples", "administration-windows")
    || (has("spelling") && planned.includes("win-x64"));
  const native = legs(nativeLegs, "native", nativeChecks ? platformLegs(nativeLegs, planned) : []);
  // The WPF hosts and their packed consumer run on Windows only.
  const wpf = legs(wpfLegs, "wpf", full || has("navigation", "application-wpf") ? platformLegs(wpfLegs, planned) : []);
  // The template's packaging recipes build, install and open real packages on each OS; the platform packages they
  // exercise select them through the packaging component's dependencies.
  const packaging = legs(packagingLegs, "packaging", full || has("packaging") ? platformLegs(packagingLegs, planned) : []);
  // NVDA and Orca read the accessibility fixture in the embedded window.
  const screenReaders = legs(screenReaderLegs, "screen-readers", full || readers ? platformLegs(screenReaderLegs, planned) : []);
  const run = {
    managed: managed.length > 0,
    // One job runs each planned web package's tests and the framework consumers in named steps.
    web: web.length > 0 || frameworks,
    "node-26": web.some(name => nodePackages().includes(name)),
    views: full || has("examples") || forced("views"),
    "package-consumers": packages || forced("package-consumers"),
    wpf: wpf.length > 0,
    templates: packages || forced("templates"),
    native: native.length > 0,
    // The musl RIDs run the same native checks in Alpine.
    "native-musl": nativeChecks || forced("native-musl"),
    packaging: packaging.length > 0,
    "screen-readers": screenReaders.length > 0,
    // js-debug attaches through dotnet runic dev's CDP adapter to the template's Window on GTK 3 and GTK 4 (Linux).
    "webkit-cdp": full || webKitCdp || forced("webkit-cdp"),
  };
  // The Windows and macOS legs that run: those the files plan, and those of the planned jobs.
  const ran = [...native, ...wpf, ...packaging, ...screenReaders].map(leg => leg.platform).filter(Boolean);
  const reasons = { ...Object.fromEntries(Object.entries(jobs).flatMap(([, entry]) => Object.entries(entry.why)).reverse()), ...why };
  const legPlatforms = platforms.filter(platform => planned.includes(platform) || ran.includes(platform));
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
    platforms: legPlatforms,
    why: legPlatforms.map(platform => `${platform}: ${planned.includes(platform) ? why[platform] : reasons[platform]}`).join("; ")
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

function mergeBase() {
  try {
    return execFileSync("git", ["merge-base", "HEAD", "origin/main"], { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim() || null;
  } catch { return null; }
}

// Accept another checkout's affected() result. Anything else (such as an older base
// planner that does not support --affected) plans every job. A result without
// platforms, from a base planner older than platform planning, plans every platform.
export function parseAffected(text) {
  try {
    return affectedValue(JSON.parse(text));
  } catch {}
  return null;
}
// A scoped result (R2) carries the jobs it planned and the unscoped result for pushes. A malformed `jobs` or `unscoped`
// makes the whole result unusable, which plans everything.
function affectedValue(value, nested = false) {
  const scoped = value?.jobs !== undefined || value?.unscoped !== undefined;
  if (scoped) {
    if (nested || !value.jobs || typeof value.jobs !== "object" || Array.isArray(value.jobs)) return null;
    const jobs = Object.entries(value.jobs);
    if (!jobs.every(([name, entry]) => skippableJobs.includes(name) && Array.isArray(entry?.platforms)
      && entry.platforms.every(platform => platforms.includes(platform) && typeof entry.why?.[platform] === "string"))) return null;
    const unscoped = affectedValue(value.unscoped, true);
    const rest = affectedValue({ ...value, jobs: undefined, unscoped: undefined }, true);
    if (!unscoped || !rest) return null;
    return { ...rest, jobs: Object.fromEntries(jobs.map(([name, entry]) => [name, { platforms: entry.platforms,
      why: Object.fromEntries(entry.platforms.map(platform => [platform, entry.why[platform]])), file: typeof entry.file === "string" ? entry.file : name }])), unscoped };
  }
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
}

// Pushes to main, and release and train merge queue entries, plan without R2's job scoping: as before, every job and
// leg that the edited ci.yml, action or script needs (the nightly run has no diff to scope).
export const unscoped = input => input?.unscoped ?? input;

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
    console.log(JSON.stringify(affected(readFileSync(0, "utf8").split("\0").filter(Boolean), checkoutContext())));
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
    // --files [--base <revision>] [--head <revision>] <paths>: base defaults to the merge base with origin/main, head to
    // the working tree.
    const files = mode === "--files" ? rest.filter((arg, index) => !["--base", "--head"].includes(arg) && !["--base", "--head"].includes(rest[index - 1])) : null;
    const revision = mode === "--files" ? option("--base") ?? mergeBase() : null;
    let input = mode === "--plan" ? parseAffected(rest[0] ?? "")
      : mode === "--files" ? affected(files, revision ? revisionContext(revision, option("--head") ?? null) : null) : null;
    const push = rest.includes("--all-linux") || option("--covered") !== undefined;
    const release = option("--all-platforms")?.startsWith("a release or train step");
    if (input && (push || release)) input = unscoped(input);
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
    if (input?.jobs && !covered) {
      const scoped = Object.entries(input.jobs).filter(([name]) => !result.skip.includes(name)).map(([name, entry]) => `${name} (${entry.file})`);
      const summary = `Job-scoped (eng/ci/README.md, "Workflow edits"): this change plans ${scoped.length ? scoped.join(", ") : "no job"} for its workflow, action and script edits, not every job.`;
      console.error(summary);
      if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${summary}\n`);
    }
    console.error(`Windows and macOS legs: ${result.why}`);
    record(result, null);
    console.log(outputs(result));
  }
}
