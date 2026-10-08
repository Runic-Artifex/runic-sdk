// Proves that the packed Runic.Navigation.Wpf installs and runs in a plain WPF app
// (W240-001 §8, §11). It checks the package layout, restores the consumer in a temporary
// copy from the packed feed, asserts that the restored graph holds Runic.Navigation and
// Runic.Navigation.Wpf as its only Runic packages and that NuGet imports no Runic build
// assets, builds it, and on Windows runs it with --smoke: show the window, push, Back, exit 0.
// Other platforms stop after the build, because WPF runs only on Windows.
// Usage: bun tests/fixtures/navigation/wpf-consumer/package-smoke.mjs [nuget-feed] [--keep]
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
const id = "Runic.Navigation.Wpf";

function onPath(command) {
  for (const directory of (process.env.PATH ?? "").split(delimiter).filter(Boolean)) {
    const candidate = join(directory, command);
    if (existsSync(candidate)) return candidate;
  }
  return undefined;
}
const dotnet = onPath(process.platform === "win32" ? "dotnet.exe" : "dotnet");
assert.ok(dotnet, "dotnet is not on PATH.");

const nupkg = join(feed, `${id}.${version}.nupkg`);
assert.ok(existsSync(nupkg), `Pack ${id} ${version} into ${feed} first.`);
// 1. Layout, and Runic.Navigation as the only Runic dependency.
verifyPackageLayout(nupkg, id, version);
const runicDependencies = nupkgDependencies(nupkg).filter(name => /^runic\./i.test(name));
assert.deepEqual(runicDependencies, ["Runic.Navigation"], `${id} depends on ${runicDependencies.join(", ")}`);

const directory = mkdtempSync(join(tmpdir(), "runic-navigation-wpf-"));
const container = /<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="([^"]+)"/.exec(readFileSync(join(root, "Directory.Packages.props"), "utf8"))?.[1];
assert.ok(container, "Directory.Packages.props has no Microsoft.Extensions.DependencyInjection version.");
const properties = [`-p:RunicPackageVersion=${version}`, `-p:RunicContainerVersion=${container}`];
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
  for (const file of ["WpfConsumer.csproj", "Program.cs", "MainWindow.xaml", "MainWindow.xaml.cs"])
    copyFileSync(join(fixture, file), join(directory, file));
  // The repository's global.json pins the SDK; the temporary tree must use it too.
  copyFileSync(join(root, "global.json"), join(directory, "global.json"));
  writeFileSync(join(directory, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${feed}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`);
  run(["restore", "WpfConsumer.csproj", ...properties]);

  // 2. Only Runic.Navigation and Runic.Navigation.Wpf are Runic libraries in the restored graph.
  const assets = JSON.parse(readFileSync(join(directory, "obj/project.assets.json"), "utf8"));
  const runic = Object.keys(assets.libraries).filter(library => /^runic\./i.test(library)).sort();
  assert.deepEqual(runic, [`Runic.Navigation/${version}`, `${id}/${version}`].sort(), `Unexpected Runic libraries: ${runic.join(", ")}`);

  // 3. NuGet imports no Runic build assets.
  for (const file of readdirSync(join(directory, "obj")).filter(name => /\.nuget\.g\.(props|targets)$/.test(name))) {
    const imports = [...readFileSync(join(directory, "obj", file), "utf8").matchAll(/<Import\s+Project="([^"]+)"/g)].map(([, path]) => path);
    assert.ok(!imports.some(path => /runic\./i.test(path)), `${file} imports a Runic package: ${imports.join(", ")}`);
  }

  // 4. The XAML (rn: namespace) compiles against the package, and on Windows the smoke runs.
  run(["build", "--no-restore", "WpfConsumer.csproj", "-c", "Release", ...properties]);
  if (process.platform === "win32") {
    const output = run(["run", "--no-build", "--project", "WpfConsumer.csproj", "-c", "Release", ...properties, "--", "--smoke"]);
    assert.ok(output.includes("WPF_NAVIGATION_CONSUMER_OK"), "The WPF consumer did not pass.");
    console.log("NAVIGATION_WPF_PACKAGE_OK|layout|only-navigation|smoke");
  } else {
    console.log("NAVIGATION_WPF_PACKAGE_OK|layout|only-navigation|build (WPF runs on Windows only)");
  }
} finally {
  if (keep) console.log(`Kept ${directory}`);
  else rmSync(directory, { recursive: true, force: true, maxRetries: 5 });
}
