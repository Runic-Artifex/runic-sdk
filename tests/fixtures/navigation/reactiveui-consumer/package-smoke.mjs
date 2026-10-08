// Proves that the packed Runic.Navigation.ReactiveUI and Runic.Navigation.ReactiveUI.Reactive
// install and run without Runic.Application (W240-001 §9, §11). For each flavor it checks the
// package layout, restores the consumer in a temporary copy from the packed feed, asserts that
// the restored graph holds Runic.Navigation and the adapter and no Runic.Application* package,
// that NuGet imports no Runic build assets, and runs the scenario.
// Usage: bun tests/fixtures/navigation/reactiveui-consumer/package-smoke.mjs [nuget-feed] [--keep]
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { delimiter, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { nupkgDependencies } from "../../../../eng/nupkg.mjs";
import { verifyPackageLayout } from "../../../../eng/verify-packages.mjs";

const root = fileURLToPath(new URL("../../../../", import.meta.url));
const fixture = fileURLToPath(new URL(".", import.meta.url));
const args = process.argv.slice(2);
const keep = args.includes("--keep");
const feed = resolve(args.find(arg => !arg.startsWith("--")) ?? join(root, "artifacts/packages/nuget"));
const { version } = JSON.parse(readFileSync(join(root, "eng/workspace.json"), "utf8"));
const flavors = [
  { id: "Runic.Navigation.ReactiveUI", property: "Primitives" },
  { id: "Runic.Navigation.ReactiveUI.Reactive", property: "Reactive" },
];

function onPath(command) {
  for (const directory of (process.env.PATH ?? "").split(delimiter).filter(Boolean)) {
    const candidate = join(directory, command);
    if (existsSync(candidate)) return candidate;
  }
  return undefined;
}
const dotnet = onPath(process.platform === "win32" ? "dotnet.exe" : "dotnet");
assert.ok(dotnet, "dotnet is not on PATH.");

for (const flavor of flavors) {
  const nupkg = join(feed, `${flavor.id}.${version}.nupkg`);
  assert.ok(existsSync(nupkg), `Pack ${flavor.id} ${version} into ${feed} first.`);
  // 1. Layout, and a dependency list without any Runic.Application package.
  verifyPackageLayout(nupkg, flavor.id, version);
  const dependencies = nupkgDependencies(nupkg);
  assert.ok(!dependencies.some(id => /^runic\.application/i.test(id)), `${flavor.id} depends on ${dependencies.join(", ")}`);

  const directory = mkdtempSync(join(tmpdir(), "runic-navigation-reactiveui-"));
  const container = /<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="([^"]+)"/.exec(readFileSync(join(root, "Directory.Packages.props"), "utf8"))?.[1];
  assert.ok(container, "Directory.Packages.props has no Microsoft.Extensions.DependencyInjection version.");
  const properties = [`-p:RunicPackageVersion=${version}`, `-p:RunicContainerVersion=${container}`, `-p:RunicNavigationFlavor=${flavor.property}`];
  const env = {
    ...process.env,
    NUGET_PACKAGES: join(directory, "nuget-cache"),
    DOTNET_CLI_HOME: join(directory, "dotnet-home"),
  };
  const run = (commandArgs) => {
    console.log(`> dotnet ${commandArgs.join(" ")}`);
    const output = execFileSync(dotnet, commandArgs, { cwd: directory, env, encoding: "utf8", stdio: ["ignore", "pipe", "inherit"] });
    process.stdout.write(output);
    return output;
  };
  try {
    for (const file of ["ReactiveUiConsumer.csproj", "Program.cs"]) copyFileSync(join(fixture, file), join(directory, file));
    // The repository's global.json pins the SDK; the temporary tree must use it too.
    copyFileSync(join(root, "global.json"), join(directory, "global.json"));
    writeFileSync(join(directory, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${feed}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`);
    run(["restore", "ReactiveUiConsumer.csproj", ...properties]);

    // 2. Only Runic.Navigation and the adapter are Runic libraries in the restored graph.
    const assets = JSON.parse(readFileSync(join(directory, "obj/project.assets.json"), "utf8"));
    const runic = Object.keys(assets.libraries).filter(library => /^runic\./i.test(library)).sort();
    assert.deepEqual(runic, [`Runic.Navigation/${version}`, `${flavor.id}/${version}`].sort(),
      `Unexpected Runic libraries for ${flavor.id}: ${runic.join(", ")}`);

    // 3. NuGet imports no Runic build assets.
    for (const file of readdirSync(join(directory, "obj")).filter(name => /\.nuget\.g\.(props|targets)$/.test(name))) {
      const imports = [...readFileSync(join(directory, "obj", file), "utf8").matchAll(/<Import\s+Project="([^"]+)"/g)].map(([, path]) => path);
      assert.ok(!imports.some(path => /runic\./i.test(path)), `${file} imports a Runic package: ${imports.join(", ")}`);
    }

    // 4. The scenario runs.
    const output = run(["run", "--no-restore", "--project", "ReactiveUiConsumer.csproj", "-c", "Release", ...properties]);
    assert.ok(output.includes("NAVIGATION_REACTIVEUI_CONSUMER_OK"), `The ${flavor.id} consumer did not pass.`);
    console.log(`NAVIGATION_REACTIVEUI_PACKAGE_OK|${flavor.id}|layout|no-application|jit`);
  } finally {
    if (keep) console.log(`Kept ${directory}`);
    else rmSync(directory, { recursive: true, force: true, maxRetries: 5 });
  }
}
