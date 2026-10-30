import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { root, workspace, affectedComponents } from "./run.mjs";
import { dotnetBuildArguments, packageConsumerStrategy, packageLayouts, resolveMsbuildPathValue } from "./verify-packages.mjs";

const json = (path) => JSON.parse(readFileSync(resolve(root, path), "utf8"));
test("workspace defines the complete public SDK package inventory", () => {
  const names = [...workspace.npm, ...workspace.nuget].map(p => p.name);
  assert.equal(workspace.nuget.length, 31);
  assert.equal(workspace.npm.length, 8);
  assert.equal(new Set(names).size, names.length);
  for (const p of workspace.npm) assert.ok(p.name.startsWith("@runic-artifex/"), p.name);
  for (const p of workspace.nuget) {
    assert.match(p.name, /^(?:Runic\.|dotnet-runic(?:$|-))/);
    assert.doesNotMatch(p.name, /(?:Generators?|Inspector|Packer|Compiler)$/,
      "Embedded implementation tools must not become public packages");
  }
  for (const p of workspace.npm) {
    const manifest = json(`${p.path}/package.json`);
    assert.equal(manifest.name, p.name);
    assert.equal(manifest.version, workspace.version);
    for (const [name, version] of Object.entries(manifest.peerDependencies ?? {})) {
      if (workspace.npm.some(peer => peer.name === name))
        assert.equal(version, workspace.version, `${p.name}: stale internal peer ${name}`);
    }
  }
  for (const p of workspace.nuget)
    assert.ok(existsSync(resolve(root, p.project)));
});
test("SDK package consumers use cross-platform target and execution strategies", () => {
  for (const packageEntry of workspace.nuget.filter(entry => !entry.name.endsWith(".Wpf"))) {
    for (const platform of ["linux", "win32"]) {
      assert.deepEqual(packageConsumerStrategy(packageEntry, platform), {
        targetFramework: "net10.0",
        execute: true,
        enableWindowsTargeting: false,
        useWpf: false,
        canaryType: undefined,
      }, `${packageEntry.name}: ${platform}`);
    }
  }
});
test("the WPF navigation consumer builds everywhere and runs on Windows", () => {
  const packageEntry = workspace.nuget.find(entry => entry.name === "Runic.Navigation.Wpf");
  for (const platform of ["linux", "win32"]) {
    assert.deepEqual(packageConsumerStrategy(packageEntry, platform), {
      targetFramework: "net10.0-windows",
      execute: platform === "win32",
      enableWindowsTargeting: true,
      useWpf: true,
      canaryType: "Runic.Navigation.Wpf.NavigationHost",
    }, platform);
  }
});
test("the embedded WPF View consumer builds everywhere and runs on Windows", () => {
  const packageEntry = workspace.nuget.find(entry => entry.name === "Runic.Application.Views.Wpf");
  for (const platform of ["linux", "win32"]) {
    assert.deepEqual(packageConsumerStrategy(packageEntry, platform), {
      targetFramework: "net10.0-windows",
      execute: platform === "win32",
      enableWindowsTargeting: true,
      useWpf: true,
      canaryType: "Runic.Application.Views.Wpf.RunicWebView",
    }, platform);
  }
});
test("package verification honors non-Debug build output paths", () => {
  assert.deepEqual(dotnetBuildArguments("Consumer.csproj", "Release", ["--nologo"]),
    ["build", "Consumer.csproj", "--configuration", "Release", "--nologo"]);
  const projectDirectory = resolve("temporary-runic-package-consumer");
  assert.equal(resolveMsbuildPathValue(projectDirectory, "obj\\Release/net10.0/"),
    resolve(projectDirectory, "obj/Release/net10.0"));
  const verifier = readFileSync(resolve(root, "eng/verify-packages.mjs"), "utf8");
  assert.doesNotMatch(verifier, /["']Debug["']/, "package verification must not hardcode the local default configuration");
});
test("active npm consumers resolve internal dependencies from the workspace", () => {
  const paths = json("package.json").workspaces;
  const names = new Set(workspace.npm.map((p) => p.name));
  for (const path of paths) {
    assert.ok(
      !existsSync(resolve(root, path, "bun.lock")),
      `${path} owns a competing lockfile`,
    );
    const manifest = json(`${path}/package.json`);
    for (const [name, version] of Object.entries({
      ...manifest.dependencies,
      ...manifest.devDependencies,
    })) {
      if (names.has(name))
        assert.equal(version, "workspace:*", `${path}: ${name}`);
    }
  }
});
test("component dependency graph is closed and acyclic", () => {
  const visiting = new Set();
  const complete = new Set();
  function visit(name) {
    assert.ok(workspace.components[name], `unknown component ${name}`);
    assert.ok(!visiting.has(name), `dependency cycle at ${name}`);
    if (complete.has(name)) return;
    visiting.add(name);
    for (const dependency of workspace.components[name].dependsOn)
      visit(dependency);
    visiting.delete(name);
    complete.add(name);
  }
  for (const name of Object.keys(workspace.components)) visit(name);
});

test("SDK artifacts have exactly one component owner", () => {
  for (const artifact of [...workspace.npm, ...workspace.nuget]) {
    const path = artifact.path ?? artifact.project;
    const owners = Object.entries(workspace.components).filter(
      ([, component]) =>
        component.paths.some(
          (prefix) => path === prefix || path.startsWith(`${prefix}/`),
        ),
    );
    assert.equal(owners.length, 1, `${path}: ${owners.map(([name]) => name)}`);
  }
});
test("affected detection follows component code and its dependents", () => {
  assert.deepEqual(
    affectedComponents([
      "packages/dotnet/Runic.Desktop/DesktopSurface.cs",
    ]).sort(),
    ["desktop", "assets", "platform", "application", "application-wpf", "views-effect", "vite", "svelte", "templates", "packaging", "examples"].sort(),
  );
  assert.deepEqual(
    affectedComponents(["packages/web/svelte/src/index.ts"]).sort(),
    ["svelte", "templates", "examples"].sort(),
  );
  assert.deepEqual(
    affectedComponents(["packages/web/views-effect/src/view.ts"]).sort(),
    ["views-effect", "examples"].sort(),
  );
  assert.deepEqual(
    affectedComponents(["eng/build/desktop.props"]).sort(),
    affectedComponents([
      "packages/dotnet/Runic.Desktop/DesktopSurface.cs",
    ]).sort(),
  );
  assert.deepEqual(
    affectedComponents(["Directory.Build.props"]).sort(),
    Object.keys(workspace.components).sort(),
  );
});

test("development workspaces and workflows use the SDK layout", () => {
  for (const path of json("package.json").workspaces) {
    assert.ok(
      /^(packages\/web\/|apps\/)/.test(
        path,
      ),
      `unexpected development workspace: ${path}`,
    );
  }
  const files = spawnSync("git", ["ls-files"], { cwd: root, encoding: "utf8" });
  assert.equal(files.status, 0);
  for (const path of files.stdout.trim().split("\n")) {
    if (!existsSync(resolve(root, path))) continue;
    assert.ok(
      !path.startsWith("packages/runic-"),
      `retired package root: ${path}`,
    );
    assert.ok(
      !path.includes("/.github/workflows/"),
      `nested active CI: ${path}`,
    );
  }
});

test("solution projects use the maintained SDK layout", () => {
  const solution = readFileSync(resolve(root, "RunicSdk.slnx"), "utf8");
  for (const [, path] of solution.matchAll(/<Project Path="([^"]+)"/g)) {
    assert.match(path, /^(?:packages\/dotnet|tools|tests|examples|apps)\//);
    const project = readFileSync(resolve(root, path), "utf8");
    assert.doesNotMatch(
      project,
      /packages[\\/]runic-/,
      path,
    );
  }
});
test("package layouts describe shipped NuGet packages", () => {
  const shipped = new Set(workspace.nuget.map(p => p.name));
  for (const name of Object.keys(packageLayouts)) assert.ok(shipped.has(name), name);
  assert.ok(packageLayouts["Runic.Navigation"].files.includes("lib/net10.0/Runic.Navigation.dll"));
  assert.ok(packageLayouts["Runic.Application.Views"].includes.includes("tools/net10.0/Runic.Navigation.dll"));
  assert.deepEqual(packageLayouts["Runic.Application.Views"].exactDependencies, ["Runic.Navigation"]);
});

// SemVer 2 precedence: release > prerelease; prerelease identifiers compare numerically or by text.
function compareVersions(left, right) {
  const parse = version => {
    const [core, prerelease] = version.split("+")[0].split(/-(.*)/s);
    return { core: core.split(".").map(Number), prerelease: prerelease ? prerelease.split(".") : [] };
  };
  const a = parse(left), b = parse(right);
  for (let i = 0; i < 3; i++) if (a.core[i] !== b.core[i]) return a.core[i] - b.core[i];
  if (!a.prerelease.length || !b.prerelease.length) return b.prerelease.length - a.prerelease.length;
  for (let i = 0; i < Math.max(a.prerelease.length, b.prerelease.length); i++) {
    const [x, y] = [a.prerelease[i], b.prerelease[i]];
    if (x === undefined) return -1;
    if (y === undefined) return 1;
    const [nx, ny] = [/^\d+$/.test(x), /^\d+$/.test(y)];
    if (nx && ny && Number(x) !== Number(y)) return Number(x) - Number(y);
    if (nx !== ny) return nx ? -1 : 1;
    if (x !== y) return x < y ? -1 : 1;
  }
  return 0;
}

test("compareVersions orders releases and prereleases", () => {
  assert.ok(compareVersions("0.7.0-preview.4", "0.7.0-preview.3") > 0);
  assert.ok(compareVersions("0.7.0-preview.10", "0.7.0-preview.9") > 0);
  assert.ok(compareVersions("0.7.0", "0.7.0-preview.9") > 0);
  assert.ok(compareVersions("0.6.1", "0.7.0-preview.1") < 0);
  assert.equal(compareVersions("0.7.0-preview.3", "0.7.0-preview.3"), 0);
});

// A package opts out of package validation only until its first release. Once the
// validation baseline reaches that release, the opt-out would silently skip API
// compatibility checks, so it must be removed (eng/release/README.md step 6).
test("validation baseline opt-outs are only on packages newer than the baseline", () => {
  const baseline = readFileSync(resolve(root, "eng/Versions.props"), "utf8")
    .match(/<RunicPackageValidationBaselineVersion>([^<]+)</)?.[1]?.trim();
  assert.ok(baseline, "RunicPackageValidationBaselineVersion was not found");
  for (const p of workspace.nuget) {
    const project = readFileSync(resolve(root, p.project), "utf8");
    if (!/<RunicPackageValidationBaselineMissing>\s*true\s*</i.test(project)) continue;
    const first = project.match(/<RunicPackageFirstReleaseVersion>([^<]+)</)?.[1]?.trim();
    assert.ok(first, `${p.name} sets RunicPackageValidationBaselineMissing without RunicPackageFirstReleaseVersion`);
    assert.ok(compareVersions(first, baseline) > 0,
      `${p.name} was first released in ${first}, at or below the validation baseline ${baseline}; remove RunicPackageValidationBaselineMissing and RunicPackageFirstReleaseVersion`);
  }
});

test("the ReactiveUI navigation adapters ship without Runic.Application.Views", () => {
  for (const [name, reactive] of [["Runic.Navigation.ReactiveUI", false], ["Runic.Navigation.ReactiveUI.Reactive", true]]) {
    const layout = packageLayouts[name];
    assert.ok(layout.files.includes(`lib/net10.0/${name}.dll`), name);
    assert.ok(layout.requires.includes("Runic.Navigation"), name);
    assert.ok(layout.forbidden.some(pattern => pattern.test("Runic.Application.Views")), name);
    assert.ok(layout.requires.includes(reactive ? "ReactiveUI.Reactive" : "ReactiveUI"), name);
    const project = readFileSync(resolve(root, `packages/dotnet/${name}/${name}.csproj`), "utf8");
    assert.doesNotMatch(project, /Runic\.Application/, name);
  }
});

// eng/release/README.md, "Third-party dependencies": every third-party package whose types a shipped
// API exposes is bounded to its tested major, and the ReactiveUI adapters reference each one directly.
test("exposed third-party packages are bounded to their tested major and referenced directly", () => {
  const props = readFileSync(resolve(root, "Directory.Packages.props"), "utf8");
  const versionOf = name => props.match(new RegExp(`<PackageVersion Include="${name.replaceAll(".", "\\.")}" Version="([^"]+)"`))?.[1];
  const exposed = {
    "Runic.Application.Views.ReactiveUI": ["ReactiveUI", "ReactiveUI.Core", "ReactiveUI.Binding", "ReactiveUI.Primitives"],
    "Runic.Application.Views.ReactiveUI.Reactive": ["ReactiveUI.Reactive", "ReactiveUI.Core", "ReactiveUI.Binding.Reactive", "System.Reactive"],
    "Runic.Navigation.ReactiveUI": ["ReactiveUI", "ReactiveUI.Binding", "ReactiveUI.Disposables", "ReactiveUI.Primitives"],
    "Runic.Navigation.ReactiveUI.Reactive": ["ReactiveUI.Reactive", "ReactiveUI.Binding.Reactive", "ReactiveUI.Primitives.Reactive", "System.Reactive"],
    "Runic.Application.Views.CsWebUi": ["CsWebUi"],
    "Runic.Application.Testing": ["Microsoft.Extensions.TimeProvider.Testing"],
  };
  for (const [project, packages] of Object.entries(exposed)) {
    const file = readFileSync(resolve(root, `packages/dotnet/${project}/${project}.csproj`), "utf8");
    for (const name of packages) {
      assert.match(file, new RegExp(`<PackageReference Include="${name.replaceAll(".", "\\.")}" />`), `${project} references ${name}`);
      const range = versionOf(name);
      const match = /^\[(\d+)\.[^,\]]+, (\d+)\)$/.exec(range ?? "");
      assert.ok(match, `${name} has a bounded range in Directory.Packages.props, not ${range}`);
      assert.equal(Number(match[2]), Number(match[1]) + 1, `${name} is bounded to its major: ${range}`);
    }
  }
  const effect = json("packages/web/views-effect/package.json").peerDependencies.effect;
  assert.match(effect, /^\^\d+\./, "views-effect bounds effect to its major");
});

// The DynamicData fork pin (dynamicData.forkRevision, which the DynamicData consumer workflow builds from source) and
// the RunicArtifex.DynamicData packages that lock files resolve, such as the one ReactiveUI.Validation brings, are one
// release: every lock resolves dynamicData.packageVersion, and the workflow requires the fork's tag
// v{packageVersion} to point at forkRevision.
test("the DynamicData fork pin matches the locked RunicArtifex.DynamicData version", () => {
  const { dynamicData } = workspace;
  assert.match(dynamicData.forkRevision, /^[0-9a-f]{40}$/);
  assert.match(dynamicData.packageVersion, /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/);
  const locks = spawnSync("git", ["ls-files", "*packages.lock.json"], { cwd: root, encoding: "utf8" }).stdout.split("\n").filter(Boolean);
  let resolved = 0;
  for (const path of locks) {
    for (const [framework, packages] of Object.entries(json(path).dependencies ?? {}))
      for (const [name, entry] of Object.entries(packages))
        if (name === "RunicArtifex.DynamicData" || name === "RunicArtifex.DynamicData.Reactive") {
          resolved++;
          assert.equal(entry.resolved, dynamicData.packageVersion,
            `${path} (${framework}) resolves ${name} ${entry.resolved}, but eng/workspace.json pins the fork at ${dynamicData.packageVersion}`);
        }
  }
  assert.ok(resolved > 0, "No lock file resolves RunicArtifex.DynamicData; check the pin another way.");
});

// D053.16: the experimental native Desktop server (X1) is unpublished infrastructure until 0.15. eng/run.mjs pack and
// the publish checks take their packages from workspace.nuget, so staying out of it keeps the package out of the
// release artifact; .github/workflows/native-server.yml packs it on its own.
test("the experimental native Desktop server is not in the release inventory", () => {
  assert.ok(existsSync(resolve(root, "packages/dotnet/Runic.Desktop.NativeServer/Runic.Desktop.NativeServer.csproj")));
  assert.ok(!workspace.nuget.some(p => p.name === "Runic.Desktop.NativeServer"));
  const solutions = ["RunicSdk.slnx", "RunicSdk.Core.slnx"].map(file => readFileSync(resolve(root, file), "utf8"));
  for (const solution of solutions) assert.doesNotMatch(solution, /Runic\.Desktop\.NativeServer|Runic\.Desktop\.ServerBenchmarks/);
});
