import { execFileSync } from "node:child_process";
import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  checkApplicationBridge,
  generateApplicationBridge,
} from "../../packages/web/application-bridge-tooling/dist/esm/index.js";

const root = resolve(fileURLToPath(new URL("../..", import.meta.url)));
const configuration = process.env.CONFIGURATION ?? "Debug";
const check = process.argv.includes("--check");
const dotnet = process.env.DOTNET_HOST_PATH ?? "dotnet";
execFileSync(
  dotnet,
  [
    "build",
    "tools/Runic.Application.Bridge.Inspector/Runic.Application.Bridge.Inspector.csproj",
    "--configuration",
    configuration,
    "--nologo",
    "-v:q",
  ],
  { cwd: root, stdio: "inherit" },
);
process.env.RUNIC_BRIDGE_INSPECTOR = resolve(
  root,
  `tools/Runic.Application.Bridge.Inspector/bin/${configuration}/net10.0/Runic.Application.Bridge.Inspector.dll`,
);
execFileSync(
  dotnet,
  [
    "restore",
    "tests/fixtures/application/CounterMembers/CounterMembers.csproj",
    "--nologo",
    "-v:q",
  ],
  { cwd: root, stdio: "inherit" },
);
const options = {
  authority: "csharp",
  root,
  project: "tests/fixtures/application/CounterMembers/CounterMembers.csproj",
  ir: "tests/fixtures/application/CounterMembers/Contract/bridge.ir.json",
  facade:
    "tests/fixtures/application/CounterMembers/Frontend/src/application.bridge.generated.ts",
};
const result = await (
  check ? checkApplicationBridge : generateApplicationBridge
)(options);
for (const template of ["angular", "react", "svelte", "vue"]) {
  const directory = resolve(
    root,
    "tools/Runic.Application.Templates/content",
    template,
  );
  for (const [file, expected] of [
    ["Contract/bridge.ir.json", result.irText],
    ["Frontend/src/application.bridge.generated.ts", result.facadeText],
  ]) {
    const path = resolve(directory, file);
    if ((await readFile(path, "utf8")) === expected) continue;
    if (check) throw new Error(`Stale member-based template artifact: ${path}`);
    await writeFile(path, expected);
  }
  for (const file of ["CounterState.cs", "CounterCommands.cs"]) {
    if (
      (await readFile(resolve(directory, file), "utf8")) !==
      (await readFile(
        resolve(root, "tools/Runic.Application.Templates/content/react", file),
        "utf8",
      ))
    )
      throw new Error(`Template behavior differs: ${template}/${file}`);
  }
}
console.log(
  `${check ? "Verified" : "Generated"} C# member-based bridge artifacts for all four templates.`,
);
