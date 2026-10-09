// Usage: bun package-smoke.mjs [nuget-feed] [npm-feed] [--serve] [--build-only] [--keep]
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { cp, lstat, mkdtemp, readFile, readdir, realpath, rm, writeFile } from "node:fs/promises";
import { homedir, tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { launchChromium, tail, waitFor, waitForExit } from "../../../../examples/shared/smoke.mjs";

const sdk = fileURLToPath(new URL("../../../../", import.meta.url));
const fixture = fileURLToPath(new URL(".", import.meta.url));
const args = process.argv.slice(2);
const positional = args.filter(arg => !arg.startsWith("--"));
const nugetFeed = resolve(positional[0] ?? join(sdk, "artifacts/packages/nuget"));
const npmFeed = resolve(positional[1] ?? join(sdk, "artifacts/packages/npm"));
const { version } = JSON.parse(await readFile(join(sdk, "eng/workspace.json"), "utf8"));
const dependencyInjection = (await readFile(join(sdk, "Directory.Packages.props"), "utf8"))
  .match(/<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="([^"]+)"/)?.[1];
assert.ok(dependencyInjection, "Dependency injection version was not found.");
const archives = ["views", "svelte"].map(name => join(npmFeed, `runic-artifex-${name}-${version}.tgz`));
for (const file of [join(nugetFeed, `Runic.Application.Desktop.${version}.nupkg`),
  join(nugetFeed, `Runic.Application.ReactiveUI.${version}.nupkg`), ...archives])
  await readFile(file); // There is deliberately no workspace-package fallback.

const directory = await mkdtemp(join(tmpdir(), "runic-operations-consumer-"));
// Reuse third-party caches across retries, but refresh task-owned Runic entries:
// a local candidate can have the same version as a previous packed build.
const cache = resolve(process.env.RUNIC_OPERATIONS_CONSUMER_CACHE ?? join(homedir(), ".cache/runic-package-consumers/operations"));
assert.ok(!isInside(sdk, cache) && !isInside(sdk, directory), "Consumer sources and cache must be outside the SDK.");
const env = { ...process.env, NUGET_PACKAGES: join(cache, "nuget"), DOTNET_CLI_HOME: join(cache, "dotnet") };
const candidateEntries = (await readdir(nugetFeed)).filter(name => name.endsWith(`.${version}.nupkg`))
  .map(name => join(env.NUGET_PACKAGES, name.slice(0, -(`.${version}.nupkg`.length)).toLowerCase(), version.toLowerCase()));
const properties = [`-p:RunicPackageVersion=${version}`, `-p:DependencyInjectionVersion=${dependencyInjection}`];
let host, browser, stdout = "", stderr = "";

function run(command, commandArgs, cwd = directory, allowFailure = false) {
  console.log(`> ${command} ${commandArgs.join(" ")}`);
  return new Promise((done, reject) => {
    const child = spawn(command, commandArgs, { cwd, env, stdio: ["ignore", "pipe", "pipe"] });
    let output = "";
    child.stdout.on("data", chunk => { output += chunk; });
    child.stderr.on("data", chunk => { output += chunk; });
    child.on("error", reject);
    child.on("close", code => {
      process.stdout.write(output);
      if (code === 0 || allowFailure) done({ code, output });
      else reject(new Error(`${command} exited ${code}:\n${tail(output)}`));
    });
  });
}

try {
  await Promise.all(candidateEntries.map(entry => rm(entry, { recursive: true, force: true })));
  for (const file of ["OperationsConsumer.csproj", "OperationsWindow.cs", "OperationsViewModel.cs", "OptIn.cs", "Program.cs"])
    await cp(join(fixture, file), join(directory, file));
  for (const folder of ["SharedContracts", "Frontend"])
    await cp(join(fixture, folder), join(directory, folder), { recursive: true,
      filter: path => !/[\/](node_modules|bin|obj|dist|generated)([\/]|$)/.test(path) });
  await cp(join(sdk, "global.json"), join(directory, "global.json"));
  await writeFile(join(directory, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${xml(nugetFeed)}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`);
  const frontend = join(directory, "Frontend");
  const manifest = JSON.parse(await readFile(join(frontend, "package.json"), "utf8"));
  for (const [index, name] of ["views", "svelte"].entries()) manifest.dependencies[`@runic-artifex/${name}`] = `file:${archives[index]}`;
  await writeFile(join(frontend, "package.json"), JSON.stringify(manifest, null, 2));

  await run("dotnet", ["build", "SharedContracts/SharedContracts.csproj", "-c", "Release", "-o", "contracts"]);
  const contractsAssets = JSON.parse(await readFile(join(directory, "SharedContracts/obj/project.assets.json"), "utf8"));
  assert.equal(Object.keys(contractsAssets.libraries).some(name => name.startsWith("Runic.")), false,
    "The shared DTO library acquired a Runic dependency.");
  // Opt-in is a deliberate compatibility boundary. Default generation retains
  // the existing diagnostic instead of silently changing shared contracts.
  const baseline = await run("dotnet", ["build", "OperationsConsumer.csproj", "-c", "Release", ...properties,
    "-p:EnableJsonIgnoreOptIn=false", "-p:RunicBridgeBuildFrontend=false"], directory, true);
  assert.notEqual(baseline.code, 0, "Generation unexpectedly accepted the computed DTO without opt-in.");
  assert.match(baseline.output, /RUNICBRIDGE003/);
  await run("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", "--package-lock=false"], frontend);
  for (const name of ["views", "svelte"]) {
    const installed = join(frontend, "node_modules/@runic-artifex", name);
    assert.equal((await lstat(installed)).isSymbolicLink(), false, `Installed ${name} is a workspace symlink.`);
    assert.ok(isInside(directory, await realpath(installed)), `Installed ${name} points outside the consumer.`);
    const packed = JSON.parse(await readFile(join(installed, "package.json"), "utf8"));
    assert.equal(packed.version, version);
    assert.ok(!Object.values(packed.dependencies ?? {}).some(value => String(value).startsWith("workspace:")));
  }
  await run("dotnet", ["build", "OperationsConsumer.csproj", "-c", "Release", ...properties]);
  await run("bun", ["run", "--bun", "check"], frontend);
  const generated = await readFile(join(frontend, "src/generated/operations.ts"), "utf8");
  assert.doesNotMatch(generated, /\bisFiltered\b/);
  assert.match(generated, /\bhint\b/);
  assert.match(generated, /\brootVisible\b/);
  const assets = JSON.parse(await readFile(join(directory, "obj/project.assets.json"), "utf8"));
  assert.equal(Object.values(assets.libraries).some(library => library.type === "project"), false,
    "A ProjectReference crossed the package boundary.");
  const imports = await run("dotnet", ["msbuild", "OperationsConsumer.csproj", "-nologo", ...properties,
    "-getProperty:DirectoryBuildPropsPath,DirectoryBuildTargetsPath,DirectoryPackagesPropsPath", "-getItem:ProjectReference"]);
  assert.doesNotMatch(imports.output, /(?:Directory\.Build\.(?:props|targets)|Directory\.Packages\.props|forgeconnect-sdk)/,
    "The external consumer inherited repository build inputs.");
  assert.deepEqual(JSON.parse(imports.output).Items.ProjectReference, []);
  console.log("OPERATIONS_PACKAGE_BUILD_OK|external|packed-nuget|packed-npm|bootstrap|shared-dto|svelte-check");
  if (args.includes("--build-only")) process.exitCode = 0;
  else {
    host = spawn("dotnet", [join(directory, "bin/Release/net10.0/OperationsConsumer.dll")],
      { cwd: directory, env, stdio: [args.includes("--serve") ? "inherit" : "pipe", "pipe", "pipe"] });
    host.stdout.on("data", chunk => { stdout += chunk; process.stdout.write(chunk); });
    host.stderr.on("data", chunk => { stderr += chunk; process.stderr.write(chunk); });
    const detail = async () => `${browser ? await browser.diagnostics() : "browser not started"}\nhost stdout:\n${tail(stdout)}\nhost stderr:\n${tail(stderr)}`;
    const wait = (condition, label) => waitFor(() => {
      if (host.exitCode !== null && host.exitCode !== 0) throw new Error(`Host exited ${host.exitCode}: ${stderr}`);
      return condition();
    }, { label, detail, timeout: 30_000 });
    const url = await wait(() => stdout.match(/OPERATIONS_CONSUMER_URL=(https?:\/\/[^\s]+)/)?.[1], "consumer URL");
    if (args.includes("--serve")) {
      // Local verification uses the desktop/T3 preview surface. The coordinator
      // opens this URL, evaluates runJourneys(), then sends close and release.
      console.log(`Open ${url}; await window.runJourneys(); then send close and release on stdin.`);
      await new Promise((done, reject) => host.once("close", code => code === 0 ? done() : reject(new Error(`Host exited ${code}: ${stderr}`))));
    } else {
      browser = await launchChromium(url, { profilePrefix: "runic-operations-consumer-" });
      await wait(() => browser.evaluate("window.operationsReady === true"), "generated client connection");
      const journeys = await browser.evaluate("window.runJourneys()", { timeout: 120_000 });
      assert.equal(journeys.length, 8, "Not every browser journey completed.");
      console.log(`OPERATIONS_BROWSER_OK|${journeys.join("|")}`);
      host.stdin.write("close\n");
      await wait(() => stdout.includes("OPERATIONS_CONTENT_CLOSE_COMPLETED") && stdout.includes("OPERATIONS_DISPOSE_PENDING"), "scope disposal awaiting actual accepted recovery");
      host.stdin.write("release\n");
      await wait(() => stdout.includes("OPERATIONS_ACCEPTED_WORK_DRAINED"), "actual accepted recovery drain");
      assert.ok(await waitForExit(host, 10_000), "Consumer did not stop after draining.");
      assert.equal(host.exitCode, 0, `Consumer failed: ${stderr}`);
    }
    assert.match(stdout, /OPERATIONS_ACCEPTED_WORK_DRAINED/);
    console.log("OPERATIONS_PACKAGE_OK|responsive-callbacks|latest-selection|session-replace|scope-drain");
  }
} finally {
  try { await browser?.close(); }
  finally {
    if (host && host.exitCode === null && host.signalCode === null) {
      host.kill("SIGTERM");
      if (!await waitForExit(host, 3_000)) host.kill("SIGKILL");
    }
    await Promise.all(candidateEntries.map(entry => rm(entry, { recursive: true, force: true })));
    if (args.includes("--keep")) console.log(`Kept ${directory}`);
    else await rm(directory, { recursive: true, force: true, maxRetries: 5 });
  }
}

function isInside(parent, path) { const result = relative(parent, path); return result === "" || (!result.startsWith("..") && !result.startsWith("/")); }
function xml(value) { return value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;"); }
