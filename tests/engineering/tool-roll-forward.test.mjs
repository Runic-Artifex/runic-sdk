import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";
import { root, workspace } from "../../eng/run.mjs";

// #614: the tools that a Runic app build or its developer runs target net10.0. Without roll-forward a machine with
// only a newer runtime (for example the .NET 11 SDK alone) fails with exit 150. MSBuild starts BridgeCodegen and the
// assets packer as `dotnet <tool>.dll`; dotnet-runic and runic-create start through the .NET tool shim or `dnx`.
// Every path reads rollForward from the tool's runtimeconfig.json, which the SDK writes from RollForward.
const tools = [
  "tools/Runic.Application.Views.Codegen/Runic.Application.Views.Codegen.csproj",
  "tools/Runic.Assets.Packer/Runic.Assets.Packer.csproj",
  "tools/Runic.Create/Runic.Create.csproj",
  "tools/dotnet-runic/Runic.Application.Tool.csproj",
];

test("the shipped tools are .NET tools or the executables whose runtimeconfig a package carries", () => {
  const shipped = new Set();
  for (const { project } of workspace.nuget) {
    const text = readFileSync(resolve(root, project), "utf8");
    if (/<PackAsTool>true<\/PackAsTool>/.test(text)) shipped.add(project);
    for (const [, directory] of text.matchAll(/Include="\.\.\/\.\.\/\.\.\/(tools\/[^/"]+)\/bin\/[^"]*\.runtimeconfig\.json"/g)) {
      const csproj = tools.find(tool => tool.startsWith(`${directory}/`));
      assert.ok(csproj, `${project} packs the runtimeconfig of ${directory}, which is not in the roll-forward list`);
      shipped.add(csproj);
    }
  }
  assert.deepEqual([...shipped].sort(), [...tools].sort());
});

for (const tool of tools) {
  test(`${tool} rolls forward to a newer major runtime`, { timeout: 120000 }, () => {
    const output = execFileSync("dotnet", ["msbuild", resolve(root, tool), "-nologo", "-noAutoResponse",
      "-getProperty:RollForward", "-getProperty:TargetFramework", "-getProperty:OutputType"],
    { cwd: root, encoding: "utf8" });
    const { Properties: properties } = JSON.parse(output);
    assert.equal(properties.OutputType, "Exe");
    assert.equal(properties.TargetFramework, "net10.0");
    assert.equal(properties.RollForward, "Major");
  });
}
