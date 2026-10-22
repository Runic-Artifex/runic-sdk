import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { root } from "../../eng/run.mjs";

// I-035: both navigation adapters pack these targets (the .Reactive package under its own name), which warn with
// RUNICNAV002 when the resolved references contain both flavors. ReferencePath is what ResolveAssemblyReferences
// produces from direct, package and transitive references. With a Views ReactiveUI adapter, RUNICBRIDGE023 reports
// the mix instead.
const targets = resolve(root, "packages/dotnet/Runic.Navigation.ReactiveUI/buildTransitive/Runic.Navigation.ReactiveUI.targets");
let directory;
// As NuGet imports them when both packages arrive: the same file under each package's name.
let packed;

before(() => {
  directory = mkdtempSync(join(tmpdir(), "runic-navigation-targets-"));
  packed = [targets, join(directory, "Runic.Navigation.ReactiveUI.Reactive.targets")];
  copyFileSync(targets, packed[1]);
});

after(() => {
  rmSync(directory, { recursive: true, force: true });
});

function check(name, references, imports) {
  const project = join(directory, name);
  mkdirSync(project, { recursive: true });
  writeFileSync(join(project, "App.proj"), `<Project>
  <ItemGroup>
${references.map(reference => `    <ReferencePath Include="$(MSBuildProjectDirectory)/refs/${reference}.dll" />`).join("\n")}
    <ReferencePath Include="$(MSBuildProjectDirectory)/refs/ReactiveUI.dll" />
  </ItemGroup>
${packed.slice(0, imports).map(file => `  <Import Project="${file}" />`).join("\n")}
</Project>
`);
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicNavigationWarnMixedReactiveUiFlavors",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"], { cwd: project, encoding: "utf8" });
  const output = `${result.stdout}${result.stderr}`;
  assert.equal(result.status, 0, `${name}: ${output}`);
  return { output, warnings: [...new Set(output.split(/\r?\n/).filter(line => / warning RUNICNAV002:/.test(line)))] };
}

test("RUNICNAV002 warns once when both navigation flavors are referenced", () => {
  const both = ["Runic.Navigation.ReactiveUI", "Runic.Navigation.ReactiveUI.Reactive"];
  // Both packages import the same target; the later definition replaces the earlier one.
  for (const imports of [1, 2]) {
    const { output, warnings } = check(`both-${imports}`, both, imports);
    assert.equal(warnings.length, 1, output);
    assert.match(warnings[0], /references both Runic\.Navigation\.ReactiveUI, which uses ReactiveUI\.Primitives, and Runic\.Navigation\.ReactiveUI\.Reactive/);
  }
});

test("RUNICNAV002 is silent for one flavor and leaves a Views mix to RUNICBRIDGE023", () => {
  for (const [name, references] of [
    ["primitives", ["Runic.Navigation.ReactiveUI"]],
    ["reactive", ["Runic.Navigation.ReactiveUI.Reactive"]],
    ["with-views", ["Runic.Application.Views.ReactiveUI", "Runic.Navigation.ReactiveUI", "Runic.Navigation.ReactiveUI.Reactive"]],
    ["with-reactive-views", ["Runic.Application.Views.ReactiveUI.Reactive", "Runic.Navigation.ReactiveUI", "Runic.Navigation.ReactiveUI.Reactive"]],
  ]) {
    const { output, warnings } = check(name, references, 2);
    assert.equal(warnings.length, 0, `${name}: ${output}`);
  }
});
