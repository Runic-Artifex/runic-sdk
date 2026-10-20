import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { root } from "../../eng/run.mjs";

// The Runic.Assets targets pack RunicAssetsDist with the packer. A stand-in packer records the arguments it receives.
const buildTransitive = resolve(root, "packages/dotnet/Runic.Assets/buildTransitive");
let directory;

before(() => {
  directory = mkdtempSync(join(tmpdir(), "runic-assets-targets-"));
});

after(() => {
  rmSync(directory, { recursive: true, force: true });
});

function pack(name, properties) {
  const project = join(directory, name);
  mkdirSync(join(project, "dist"), { recursive: true });
  writeFileSync(join(project, "dist/index.html"), "<!doctype html>");
  writeFileSync(join(project, "packer.mjs"), `import { writeFileSync } from "node:fs";
writeFileSync(${JSON.stringify(join(project, "arguments.json"))}, JSON.stringify(process.argv.slice(2)));
`);
  // As NuGet does: the package's props before the project's properties, its targets after them.
  writeFileSync(join(project, "App.proj"), `<Project>
  <Import Project="${join(buildTransitive, "Runic.Assets.props")}" />
  <PropertyGroup>
    <IntermediateOutputPath>obj/</IntermediateOutputPath>
    <RunicAssetsDist>$(MSBuildProjectDirectory)/dist</RunicAssetsDist>
    <RunicAssetsPackerPath>$(MSBuildProjectDirectory)/packer.mjs</RunicAssetsPackerPath>
    <DotNetHostPath>${process.execPath}</DotNetHostPath>
${properties}
  </PropertyGroup>
  <Import Project="${join(buildTransitive, "Runic.Assets.targets")}" />
</Project>
`);
  const result = spawnSync("dotnet", ["msbuild", join(project, "App.proj"), "-t:RunicAssetsPackFileSystem",
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m"], { cwd: project, encoding: "utf8" });
  assert.equal(result.status, 0, result.stdout + result.stderr);
  const argumentsList = JSON.parse(readFileSync(join(project, "arguments.json"), "utf8"));
  const index = argumentsList.indexOf("--exclude");
  return index < 0 ? null : argumentsList[index + 1];
}

// W319-003 msbuild backlog: the default was set in the targets, after the project file, so an empty value was
// replaced again and the branch without --exclude never ran.
test("RunicAssetsDistExclude defaults to runic-assets.zip, and a project can replace or empty it", () => {
  assert.equal(pack("default", ""), "runic-assets.zip");
  assert.equal(pack("replaced", "<RunicAssetsDistExclude>stats.html</RunicAssetsDistExclude>"), "stats.html");
  assert.equal(pack("emptied", "<RunicAssetsDistExclude></RunicAssetsDistExclude>"), null);
});
