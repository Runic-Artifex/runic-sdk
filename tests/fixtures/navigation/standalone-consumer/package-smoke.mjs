// Proves that the packed Runic.Navigation installs and runs on its own: no Runic
// build targets, generator, Node or host (W240-001 §11). Restores the consumer
// in a temporary copy from the packed feed, checks the package layout and the
// restored graph, runs it with JIT, then publishes and runs it with NativeAOT on Linux.
// Usage: bun tests/fixtures/navigation/standalone-consumer/package-smoke.mjs [nuget-feed] [--keep]
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { verifyPackageLayout } from "../../../../eng/verify-packages.mjs";

const root = fileURLToPath(new URL("../../../../", import.meta.url));
const fixture = fileURLToPath(new URL(".", import.meta.url));
const args = process.argv.slice(2);
const keep = args.includes("--keep");
const feed = resolve(args.find(arg => !arg.startsWith("--")) ?? join(root, "artifacts/packages/nuget"));
const { version } = JSON.parse(readFileSync(join(root, "eng/workspace.json"), "utf8"));
const nupkg = join(feed, `Runic.Navigation.${version}.nupkg`);
assert.ok(existsSync(nupkg), `Pack Runic.Navigation ${version} into ${feed} first.`);

// 1. The package is a plain library with only the two abstractions as dependencies.
verifyPackageLayout(nupkg, "Runic.Navigation", version);

function onPath(command) {
  for (const directory of (process.env.PATH ?? "").split(delimiter).filter(Boolean)) {
    const candidate = join(directory, command);
    if (existsSync(candidate)) return candidate;
  }
  return undefined;
}

const hostDotnet = onPath(process.platform === "win32" ? "dotnet.exe" : "dotnet");
assert.ok(hostDotnet, "dotnet is not on PATH.");

// The temporary tree has no parent workspace and its own package cache, so only
// the packed Runic.Navigation can satisfy the reference.
const directory = mkdtempSync(join(tmpdir(), "runic-navigation-standalone-"));
const properties = [`-p:RunicPackageVersion=${version}`];

// On Linux and macOS, dotnet is linked into its own directory, so a bin directory that it
// shares with Node (a Nix profile, /usr/bin) need not appear on the consumer's PATH.
// The muxer resolves the link to find its SDKs. Windows keeps dotnet's own directory,
// since creating symlinks there needs extra privileges.
let dotnet = hostDotnet;
if (process.platform !== "win32") {
  const dotnetDirectory = join(directory, "dotnet-bin");
  mkdirSync(dotnetDirectory);
  dotnet = join(dotnetDirectory, "dotnet");
  symlinkSync(hostDotnet, dotnet);
}

// 4. Restore, build and the JIT run see only the dotnet directory on PATH, so no
// Node, npm or bun can take part. The NativeAOT publish also needs a shell and the
// native toolchain, so it keeps the PATH entries that contain no Node, npm or bun.
const scripting = ["node", "npm", "bun", "node.exe", "npm.cmd", "bun.exe"];
function environment(extraPath = []) {
  const path = [dirname(dotnet), ...extraPath];
  for (const entry of path)
    for (const tool of scripting)
      assert.ok(!existsSync(join(entry, tool)), `${join(entry, tool)} is on the consumer's PATH.`);
  return {
    ...process.env,
    PATH: path.join(delimiter),
    NUGET_PACKAGES: join(directory, "nuget-cache"),
    DOTNET_CLI_HOME: join(directory, "dotnet-home"),
  };
}

function run(command, commandArgs, env) {
  console.log(`> ${command} ${commandArgs.join(" ")}`);
  const output = execFileSync(command, commandArgs, { cwd: directory, env, encoding: "utf8", stdio: ["ignore", "pipe", "inherit"] });
  process.stdout.write(output);
  return output;
}

try {
  for (const file of ["StandaloneConsumer.csproj", "Program.cs"])
    copyFileSync(join(fixture, file), join(directory, file));
  // The repository's global.json pins the SDK; the temporary tree must use it too.
  copyFileSync(join(root, "global.json"), join(directory, "global.json"));
  // Empty MSBuild and central package files stop the import search at the temporary
  // tree, so files in a parent of the temporary directory cannot take part.
  writeFileSync(join(directory, "Directory.Build.props"), "<Project />\n");
  writeFileSync(join(directory, "Directory.Build.targets"), "<Project />\n");
  writeFileSync(join(directory, "Directory.Packages.props"), "<Project />\n");
  writeFileSync(join(directory, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${feed}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`);

  const env = environment();
  run(dotnet, ["restore", "StandaloneConsumer.csproj", ...properties], env);

  // 2. Runic.Navigation is the only Runic library in the restored graph.
  const assets = JSON.parse(readFileSync(join(directory, "obj/project.assets.json"), "utf8"));
  const runic = Object.keys(assets.libraries).filter(library => /^runic\./i.test(library));
  assert.deepEqual(runic, [`Runic.Navigation/${version}`], `Unexpected Runic libraries: ${runic.join(", ")}`);

  // 3. NuGet imports no Runic build assets, and no Bridge build property is defined.
  for (const file of readdirSync(join(directory, "obj")).filter(name => /\.nuget\.g\.(props|targets)$/.test(name))) {
    const imports = [...readFileSync(join(directory, "obj", file), "utf8").matchAll(/<Import\s+Project="([^"]+)"/g)].map(([, path]) => path);
    assert.ok(!imports.some(path => /runic\./i.test(path)), `${file} imports a Runic package: ${imports.join(", ")}`);
  }
  const bridge = run(dotnet, ["msbuild", "StandaloneConsumer.csproj", "-getProperty:RunicBridgeBuildEnabled", ...properties], env).trim();
  assert.equal(bridge, "", "RunicBridgeBuildEnabled is defined in a standalone navigation consumer.");

  // 5. JIT and NativeAOT runs of the scenarios.
  const jit = run(dotnet, ["run", "--no-restore", "--project", "StandaloneConsumer.csproj", "-c", "Release", ...properties], env);
  assert.ok(jit.includes("STANDALONE_NAVIGATION_OK"), "The JIT standalone navigation consumer did not pass.");
  if (process.platform === "linux") {
    const native = (process.env.PATH ?? "").split(delimiter).filter(Boolean)
      .filter(entry => entry !== dirname(hostDotnet) && !scripting.some(tool => existsSync(join(entry, tool))));
    // When the shell or compiler shares a directory with Node, link it into its own.
    const toolchain = join(directory, "toolchain");
    mkdirSync(toolchain);
    for (const tool of ["sh", "clang", "objcopy"])
      if (!native.some(entry => existsSync(join(entry, tool))) && onPath(tool)) symlinkSync(onPath(tool), join(toolchain, tool));
    const aotEnv = environment([toolchain, ...native]);
    const output = join(directory, "aot");
    const rid = `linux-${process.arch}`;
    run(dotnet, ["publish", "StandaloneConsumer.csproj", "-c", "Release", "-r", rid, "--self-contained", "true",
      "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", ...properties, "-o", output], aotEnv);
    const aot = run(join(output, "StandaloneConsumer"), [], environment());
    assert.ok(aot.includes("STANDALONE_NAVIGATION_OK"), "The NativeAOT standalone navigation consumer did not pass.");
  } else console.log("NativeAOT publish runs on Linux only.");
  console.log("NAVIGATION_STANDALONE_PACKAGE_OK|layout|graph|no-build-assets|no-node|jit" + (process.platform === "linux" ? "|nativeaot" : ""));
} finally {
  if (keep) console.log(`Kept ${directory}`);
  else rmSync(directory, { recursive: true, force: true, maxRetries: 5 });
}
