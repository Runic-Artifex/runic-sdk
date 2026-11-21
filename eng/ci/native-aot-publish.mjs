// C1 (D077.25): publishes the native job's NativeAOT test applications at once instead of one step after another. One
// MSBuild invocation builds them all with `-m:<parallel>` nodes, so a library that several applications reference is
// built once (the same project with the same global properties is one configuration); separate `dotnet publish`
// processes would build it concurrently into the same obj directory. Every application gets the global properties a
// `dotnet publish -c Release -r <rid> --self-contained true -p:PublishAot=true -p:IlcTreatWarningsAsErrors=true
// -p:TrimmerSingleWarn=false` gives it; one that needs other properties (they would reach its references and build
// them a second time) publishes in its own step. Each output moves from the default publish directory to
// artifacts/<name>, as `-o artifacts/<name>` would have placed it. The log prefixes every line with the elapsed time
// and ends with the peak memory in use and the lowest available while it ran (ILC is memory-hungry; the parallelism
// per runner is nativeLegs' aotParallel in eng/ci/platforms.mjs).
//
//   node eng/ci/native-aot-publish.mjs --rid <rid> --parallel <n> <name>=<project> ...
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { freemem, totalmem } from "node:os";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const escape = text => text.replace(/[&<>"]/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[char]);

export function parseArguments(argv) {
  const options = { rid: null, parallel: null, apps: [] };
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === "--rid") options.rid = argv[++index];
    else if (argument === "--parallel") options.parallel = Number(argv[++index]);
    else {
      const match = /^([a-z0-9][a-z0-9-]*)=(.+)$/.exec(argument);
      if (!match) throw new Error(`Expected <name>=<project>, not '${argument}'`);
      options.apps.push({ name: match[1], project: match[2] });
    }
  }
  if (!/^[a-z]+-[a-z0-9]+$/.test(options.rid ?? "")) throw new Error("--rid <rid> is required");
  if (!Number.isInteger(options.parallel) || options.parallel < 1) throw new Error("--parallel <n> must be a positive integer");
  if (options.apps.length === 0) throw new Error("No applications to publish");
  const names = options.apps.map(app => app.name);
  if (new Set(names).size !== names.length) throw new Error(`Duplicate output name in ${names.join(", ")}`);
  return options;
}

// The project file of <project>: the file itself, or the single .csproj in the directory.
export function projectFile(project, base = root) {
  const path = resolve(base, project);
  if (path.endsWith(".csproj")) return path;
  const found = readdirSync(path).filter(file => file.endsWith(".csproj"));
  if (found.length !== 1) throw new Error(`Expected one .csproj in ${project}, found ${found.length}`);
  return join(path, found[0]);
}

// The global properties of `dotnet publish -c Release -r <rid> --self-contained true` plus the native job's NativeAOT ones.
export const publishProperties = rid => ({
  Configuration: "Release",
  RuntimeIdentifier: rid,
  _CommandLineDefinedRuntimeIdentifier: "true",
  SelfContained: "true",
  _CommandLineDefinedSelfContained: "true",
  _IsPublishing: "true",
  PublishAot: "true",
  IlcTreatWarningsAsErrors: "true",
  TrimmerSingleWarn: "false",
});

// The traversal project: Restore restores each application one after another (as each `dotnet publish` did), Publish
// publishes them in parallel and returns each one's publish directory.
export function traversal(projects) {
  const items = projects.map(path => `    <RunicAotApplication Include="${escape(path)}" />`).join("\n");
  return `<Project>
  <ItemGroup>
${items}
  </ItemGroup>
  <Target Name="Restore">
    <MSBuild Projects="@(RunicAotApplication)" Targets="Restore" BuildInParallel="false" />
  </Target>
  <Target Name="Publish">
    <MSBuild Projects="@(RunicAotApplication)" Targets="Publish" BuildInParallel="true" />
    <MSBuild Projects="@(RunicAotApplication)" Targets="_RunicAotPublishDir" BuildInParallel="true"
      Properties="CustomAfterMicrosoftCommonTargets=$(MSBuildThisFileDirectory)publish-dir.targets">
      <Output TaskParameter="TargetOutputs" ItemName="RunicAotPublishDir" />
    </MSBuild>
    <WriteLinesToFile File="$(MSBuildThisFileDirectory)publish-dirs.txt" Lines="@(RunicAotPublishDir->'%(MSBuildSourceProjectFile)|%(Identity)')" Overwrite="true" />
  </Target>
</Project>
`;
}

// Prints the project's PublishDir without building: a separate evaluation (the extra property) that runs no build target.
const publishDirTargets = `<Project>
  <Target Name="_RunicAotPublishDir" Returns="$([System.IO.Path]::GetFullPath('$(PublishDir)'))" />
</Project>
`;

export const msbuildArguments = (file, rid, parallel) => ["msbuild", file, "-restore", "-t:Publish", `-m:${parallel}`, "-nologo",
  "-v:m", "-clp:Summary", ...Object.entries(publishProperties(rid)).map(([name, value]) => `-p:${name}=${value}`)];

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { rid, parallel, apps } = parseArguments(process.argv.slice(2));
  const work = resolve(root, "artifacts/native-aot-publish");
  rmSync(work, { recursive: true, force: true });
  mkdirSync(work, { recursive: true });
  const projects = apps.map(app => ({ ...app, file: projectFile(app.project) }));
  const file = join(work, "publish.proj");
  writeFileSync(file, traversal(projects.map(app => app.file)));
  writeFileSync(join(work, "publish-dir.targets"), publishDirTargets);
  const started = Date.now();
  const elapsed = () => `[${((Date.now() - started) / 1000).toFixed(0).padStart(4)} s]`;
  console.log(`Publishing ${projects.map(app => `${app.name} (${basename(app.file)})`).join(", ")} for ${rid}, ${parallel} at once.`);
  // Peak memory in use and lowest available, sampled every second. On macOS free memory excludes reclaimable pages, so
  // "in use" is an upper bound there.
  let peak = 0, lowest = Infinity;
  const sample = () => { const free = freemem(); peak = Math.max(peak, totalmem() - free); lowest = Math.min(lowest, free); };
  sample();
  const sampler = setInterval(sample, 1000);
  const child = spawn("dotnet", msbuildArguments(file, rid, parallel), { cwd: root, stdio: ["ignore", "pipe", "pipe"] });
  for (const stream of [child.stdout, child.stderr]) {
    let rest = "";
    stream.setEncoding("utf8");
    stream.on("data", chunk => {
      const lines = (rest + chunk).split(/\r?\n/);
      rest = lines.pop();
      for (const line of lines) console.log(`${elapsed()} ${line}`);
    });
    stream.on("end", () => { if (rest) console.log(`${elapsed()} ${rest}`); });
  }
  const code = await new Promise(done => child.on("close", (status, signal) => done(status ?? (signal ? 1 : 0))));
  clearInterval(sampler);
  const gb = bytes => `${(bytes / 2 ** 30).toFixed(1)} GB`;
  console.log(`${elapsed()} dotnet msbuild ended with exit code ${code}; memory in use peaked at ${gb(peak)} of ${gb(totalmem())}, lowest available ${gb(lowest)}.`);
  if (code !== 0) process.exit(code);
  const dirs = new Map(readFileSync(join(work, "publish-dirs.txt"), "utf8").split(/\r?\n/).filter(Boolean)
    .map(line => line.split("|")).map(([project, dir]) => [resolve(project), dir]));
  for (const app of projects) {
    const from = dirs.get(resolve(app.file));
    if (!from || !existsSync(from)) throw new Error(`No publish directory for ${app.project} (${from ?? "not reported"})`);
    const to = resolve(root, "artifacts", app.name);
    rmSync(to, { recursive: true, force: true });
    mkdirSync(dirname(to), { recursive: true });
    renameSync(from, to);
    console.log(`${app.project} -> artifacts/${app.name}`);
  }
}
