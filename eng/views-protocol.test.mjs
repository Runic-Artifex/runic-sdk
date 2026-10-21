import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import test from "node:test";
import { root } from "./run.mjs";

// Views protocol 3 (specs/application/README.md): .NET answers a released View `released`.
// `disconnected` is client-only (the Bridge is not connected), so no .NET wire code sends it.
const wirePackages = [
  "Runic.Application.Views",
  "Runic.Application.Views.CsWebUi",
  "Runic.Application.Views.Desktop",
  "Runic.Application.Views.Wpf",
  "Runic.Desktop",
];

function* sources(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.name === "bin" || entry.name === "obj") continue;
    const path = join(directory, entry.name);
    if (entry.isDirectory()) yield* sources(path);
    else if (entry.name.endsWith(".cs")) yield path;
  }
}

test("no .NET Views, Desktop or CS-WebUI code sends the wire token disconnected", () => {
  const offenders = [];
  for (const name of wirePackages)
    for (const path of sources(resolve(root, "packages/dotnet", name))) {
      const lines = readFileSync(path, "utf8").split("\n");
      lines.forEach((line, index) => {
        // A C# string literal, or one inside a JSON literal (\"disconnected\").
        if (/\\?"disconnected\\?"/.test(line)) offenders.push(`${relative(root, path)}:${index + 1}`);
      });
    }
  assert.deepEqual(offenders, []);
});

test("the .NET runtime, the npm runtime and the specification name one protocol version", () => {
  const dotnet = readFileSync(resolve(root, "packages/dotnet/Runic.Application.Views/Generated/BridgeRuntime.cs"), "utf8")
    .match(/public const int Version = (\d+);/)?.[1];
  const npm = readFileSync(resolve(root, "packages/web/views/src/version.ts"), "utf8").match(/export const bridgeProtocol = (\d+);/)?.[1];
  const spec = readFileSync(resolve(root, "specs/application/README.md"), "utf8").match(/This is the protocol, version (\d+),/)?.[1];
  assert.ok(dotnet, "BridgeProtocol.Version not found");
  assert.equal(npm, dotnet);
  assert.equal(spec, dotnet);
});
