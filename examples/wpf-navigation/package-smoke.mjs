// Builds and tests the WPF navigation example against the packed packages, as a WPF team would consume
// them (W240-007). It copies NotesNavigation/ and Tests/ to a temporary directory, restores
// Runic.Navigation.Wpf from the packed feed with package source mapping, asserts that the restored graph
// holds Runic.Navigation and Runic.Navigation.Wpf as its only Runic packages, builds, and on Windows runs
// the headless scenario tests. Other platforms stop after the build, because WPF runs only on Windows.
// Usage: bun examples/wpf-navigation/package-smoke.mjs [nuget-feed] [--keep]
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { copyFileSync, cpSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, delimiter, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url));
const example = fileURLToPath(new URL(".", import.meta.url));
const args = process.argv.slice(2);
const keep = args.includes("--keep");
const feed = resolve(args.find(arg => !arg.startsWith("--")) ?? join(root, "artifacts/packages/nuget"));
const { version } = JSON.parse(readFileSync(join(root, "eng/workspace.json"), "utf8"));

function onPath(command) {
  for (const directory of (process.env.PATH ?? "").split(delimiter).filter(Boolean)) {
    const candidate = join(directory, command);
    if (existsSync(candidate)) return candidate;
  }
  return undefined;
}
const dotnet = onPath(process.platform === "win32" ? "dotnet.exe" : "dotnet");
assert.ok(dotnet, "dotnet is not on PATH.");
for (const id of ["Runic.Navigation", "Runic.Navigation.Wpf"])
  assert.ok(existsSync(join(feed, `${id}.${version}.nupkg`)), `Pack ${id} ${version} into ${feed} first.`);

// The example's other packages keep the repository's pinned versions.
const packages = readFileSync(join(root, "Directory.Packages.props"), "utf8");
const pinned = id => {
  const match = new RegExp(`<PackageVersion Include="${id.replaceAll(".", "\\.")}" Version="([^"]+)"`).exec(packages)?.[1];
  assert.ok(match, `Directory.Packages.props has no ${id} version.`);
  return match;
};
const versions = {
  "CommunityToolkit.Mvvm": pinned("CommunityToolkit.Mvvm"),
  "Microsoft.Extensions.DependencyInjection": pinned("Microsoft.Extensions.DependencyInjection"),
  "Runic.Navigation.Wpf": version,
};

const directory = mkdtempSync(join(tmpdir(), "runic-wpf-navigation-example-"));
const properties = [`-p:RunicPackageVersion=${version}`];
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
  // The same relative layout, without build outputs or the repository's lock files.
  for (const project of ["NotesNavigation", "Tests"])
    cpSync(join(example, project), join(directory, project), {
      recursive: true,
      filter: source => !["bin", "obj", "packages.lock.json"].includes(basename(source)),
    });
  // The repository's global.json pins the SDK; the temporary tree must use it too.
  copyFileSync(join(root, "global.json"), join(directory, "global.json"));
  // Stops MSBuild from importing anything above the temporary directory.
  writeFileSync(join(directory, "Directory.Build.props"), "<Project />\n");
  writeFileSync(join(directory, "Directory.Build.targets"), "<Project />\n");
  writeFileSync(join(directory, "Directory.Packages.props"), `<Project>
  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
  <ItemGroup>
${Object.entries(versions).map(([id, v]) => `    <PackageVersion Include="${id}" Version="${v}" />`).join("\n")}
  </ItemGroup>
</Project>
`);
  writeFileSync(join(directory, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${feed}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`);
  const tests = join("Tests", "NotesNavigation.Tests.csproj");
  run(["restore", tests, ...properties]);

  // Only Runic.Navigation and Runic.Navigation.Wpf are Runic libraries, for the app and its tests.
  for (const project of ["NotesNavigation", "Tests"]) {
    const assets = JSON.parse(readFileSync(join(directory, project, "obj/project.assets.json"), "utf8"));
    const runic = Object.keys(assets.libraries)
      .filter(library => /^runic\./i.test(library) && assets.libraries[library].type === "package").sort();
    assert.deepEqual(runic, [`Runic.Navigation/${version}`, `Runic.Navigation.Wpf/${version}`].sort(),
      `${project} restored unexpected Runic packages: ${runic.join(", ")}`);
  }

  run(["build", "--no-restore", tests, "-c", "Release", ...properties]);
  if (process.platform === "win32") {
    const output = run(["run", "--no-build", "--project", tests, "-c", "Release", ...properties]);
    assert.ok(output.includes("Notes navigation scenarios passed."), "The WPF navigation example scenarios did not pass.");
    console.log("WPF_NAVIGATION_EXAMPLE_OK|only-navigation|scenarios");
  } else {
    console.log("WPF_NAVIGATION_EXAMPLE_OK|only-navigation|build (WPF runs on Windows only)");
  }
} finally {
  if (keep) console.log(`Kept ${directory}`);
  else rmSync(directory, { recursive: true, force: true, maxRetries: 5 });
}
