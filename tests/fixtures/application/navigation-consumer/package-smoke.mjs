// Restores the navigation consumer from packed packages in a temporary copy,
// runs it with JIT, then publishes and runs it with NativeAOT on Linux.
// Usage: bun tests/fixtures/application/navigation-consumer/package-smoke.mjs [nuget-feed] [--keep]
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../../../", import.meta.url));
const fixture = fileURLToPath(new URL(".", import.meta.url));
const args = process.argv.slice(2);
const keep = args.includes("--keep");
const feed = resolve(args.find(arg => !arg.startsWith("--")) ?? join(root, "artifacts/packages/nuget"));
const { version } = JSON.parse(readFileSync(join(root, "eng/workspace.json"), "utf8"));
const packages = readFileSync(join(root, "Directory.Packages.props"), "utf8");
const dependencyInjection = packages.match(/<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="([^"]+)"/)?.[1];
assert.ok(dependencyInjection, "The Microsoft.Extensions.DependencyInjection version was not found.");
for (const id of ["Runic.Application", "Runic.Application.CsWebUi"])
  assert.ok(existsSync(join(feed, `${id}.${version}.nupkg`)), `Pack ${id} ${version} into ${feed} first.`);

// The temporary tree has no parent workspace and its own package cache, so
// only the packed Runic packages can satisfy the references.
const directory = mkdtempSync(join(tmpdir(), "runic-navigation-consumer-"));
const env = { ...process.env, NUGET_PACKAGES: join(directory, "nuget-cache"), DOTNET_CLI_HOME: join(directory, "dotnet-home") };
const properties = [`-p:RunicPackageVersion=${version}`, `-p:DependencyInjectionVersion=${dependencyInjection}`];

function run(command, commandArgs) {
  console.log(`> ${command} ${commandArgs.join(" ")}`);
  const output = execFileSync(command, commandArgs, { cwd: directory, env, encoding: "utf8", stdio: ["ignore", "pipe", "inherit"] });
  process.stdout.write(output);
  return output;
}

try {
  for (const file of ["NavigationConsumer.csproj", "Program.cs", "Shell.cs"])
    copyFileSync(join(fixture, file), join(directory, file));
  writeFileSync(join(directory, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${feed}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`);

  const jit = run("dotnet", ["run", "--project", "NavigationConsumer.csproj", "-c", "Release", ...properties]);
  assert.ok(jit.includes("NAVIGATION_CONSUMER_OK"), "The JIT navigation consumer did not pass.");
  if (process.platform === "linux") {
    const output = join(directory, "aot");
    run("dotnet", ["publish", "NavigationConsumer.csproj", "-c", "Release", "-r", "linux-x64", "--self-contained", "true",
      "-p:PublishAot=true", "-p:IlcTreatWarningsAsErrors=true", ...properties, "-o", output]);
    const aot = run(join(output, "NavigationConsumer"), []);
    assert.ok(aot.includes("NAVIGATION_CONSUMER_OK"), "The NativeAOT navigation consumer did not pass.");
  } else console.log("NativeAOT publish runs on Linux only.");
  console.log("NAVIGATION_PACKAGE_OK|restore|generate|jit" + (process.platform === "linux" ? "|nativeaot" : ""));
} finally {
  if (keep) console.log(`Kept ${directory}`);
  else rmSync(directory, { recursive: true, force: true, maxRetries: 5 });
}
