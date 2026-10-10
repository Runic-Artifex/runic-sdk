// Proves that WPF applications generate their Runic bridge from the packed packages
// alone, with no RunicBridgeBuildEnabled, on every platform (EnableWindowsTargeting):
//  1. Single: one UseWPF project that references only Runic.Application.Wpf, declares a
//     RunicView and calls AddRunicViews(). Its local XAML type makes WPF compile a
//     temporary *_wpftmp assembly, which must not generate again. The client lands in
//     Frontend/src/generated and the built frontend in www/.
//  2. Split: the View lives in a WPF-free model project; the WPF application copies
//     the model's built frontend to its own www/.
//  3. Without a Frontend folder, the single project fails with one RUNICBRIDGE014.
//  4. A library that reaches Runic.Application only through the model project and
//     declares a View gets one RUNICBRIDGE015 warning.
// Usage: bun tests/fixtures/application/wpf-bridge-consumer/package-smoke.mjs [nuget-feed] [--keep]
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../../../", import.meta.url));
const fixture = fileURLToPath(new URL(".", import.meta.url));
const args = process.argv.slice(2);
const keep = args.includes("--keep");
const feed = resolve(args.find(arg => !arg.startsWith("--")) ?? join(root, "artifacts/packages/nuget"));
const { version } = JSON.parse(readFileSync(join(root, "eng/workspace.json"), "utf8"));
const container = /<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="([^"]+)"/.exec(readFileSync(join(root, "Directory.Packages.props"), "utf8"))?.[1];
assert.ok(container, "Directory.Packages.props has no Microsoft.Extensions.DependencyInjection version.");
for (const id of ["Runic.Application", "Runic.Application.Desktop", "Runic.Application.Wpf"])
  assert.ok(existsSync(join(feed, `${id}.${version}.nupkg`)), `Pack ${id} ${version} into ${feed} first.`);

const directory = mkdtempSync(join(tmpdir(), "runic-wpf-bridge-"));
const env = {
  ...process.env,
  NUGET_PACKAGES: join(directory, "nuget-cache"),
  DOTNET_CLI_HOME: join(directory, "dotnet-home"),
};
// MSBuild repeats each diagnostic in its summary; the summary counts are exact.
function build(project, { fail = false } = {}) {
  const commandArgs = ["build", project, "-c", "Release", "--nologo",
    `-p:RunicPackageVersion=${version}`, `-p:RunicContainerVersion=${container}`];
  console.log(`> dotnet ${commandArgs.join(" ")}`);
  const result = spawnSync("dotnet", commandArgs, { cwd: directory, env, encoding: "utf8" });
  const output = `${result.stdout}${result.stderr}`;
  process.stdout.write(output);
  assert.equal(result.status !== 0, fail, `${project} ${fail ? "built unexpectedly" : "failed"}`);
  return output;
}
const summary = (output, kind) => Number(new RegExp(`^ *(\\d+) ${kind}\\(s\\)`, "m").exec(output)?.[1]);
const count = (output, pattern) => output.split("\n").filter(line => pattern.test(line)).length;
const exists = path => assert.ok(existsSync(join(directory, path)), `${path} is missing`);

try {
  cpSync(fixture, directory, { recursive: true, filter: source => !/[\\/](bin|obj|node_modules|dist|generated)$/.test(source) });
  // The repository's global.json pins the SDK; the temporary tree must use it too.
  cpSync(join(root, "global.json"), join(directory, "global.json"));
  writeFileSync(join(directory, "NuGet.config"), `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${feed}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`);

  // 1. Single project: generation is on through the adapter, without duplicate members.
  const single = build("Single/WpfSingle.csproj");
  assert.equal(summary(single, "Warning"), 0, "The single project reported warnings.");
  exists("Single/Frontend/src/generated/editor.ts");
  exists("Single/obj/Release/net10.0-windows/runic-bridge/RunicBridgeComposition.g.cs");
  exists("Single/bin/Release/net10.0-windows/www/index.html");
  exists("Single/bin/Release/net10.0-windows/www/assets/app.js");

  // 2. Split layout: the model generates, the WPF application copies its frontend.
  assert.equal(summary(build("Split/App/SplitApp.csproj"), "Warning"), 0, "The split layout reported warnings.");
  exists("Split/Frontend/src/generated/editor.ts");
  assert.ok(!existsSync(join(directory, "Split/Model/Frontend")), "The model project wrote a stray Frontend folder.");
  exists("Split/App/bin/Release/net10.0-windows/www/index.html");
  exists("Split/App/bin/Release/net10.0-windows/www/assets/app.js");

  // 3. A missing frontend is one coded error, not MSB6003.
  cpSync(join(directory, "Single"), join(directory, "NoFrontend"), { recursive: true, filter: source => !/[\\/](bin|obj|Frontend)$/.test(source) });
  const missing = build("NoFrontend/WpfSingle.csproj", { fail: true });
  assert.equal(summary(missing, "Error"), 1, "Expected exactly one error.");
  assert.ok(count(missing, /error RUNICBRIDGE014:.*RunicBridgeFrontendDir/) > 0, "Expected RUNICBRIDGE014.");
  assert.equal(count(missing, /MSB6003|MSB3073/), 0, "A frontend command ran without a frontend.");

  // 4. Generation off by default with a declared View is one coded warning.
  const library = build("Split/Library/SplitLibrary.csproj");
  assert.equal(summary(library, "Warning"), 1, "Expected exactly one warning.");
  assert.ok(count(library, /warning RUNICBRIDGE015:.*PreviewView\.cs.*RunicBridgeBuildEnabled=true/) > 0, "Expected RUNICBRIDGE015.");
  console.log("WPF_BRIDGE_PACKAGE_OK|single|split|missing-frontend|generation-off");
} finally {
  if (keep) console.log(`Kept ${directory}`);
  else rmSync(directory, { recursive: true, force: true, maxRetries: 5 });
}
