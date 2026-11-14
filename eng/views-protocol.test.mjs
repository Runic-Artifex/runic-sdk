import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import test from "node:test";
import { root } from "./run.mjs";

// Since Views protocol 3, .NET answers a released View `released`.
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
    // The Runic host protocol (Runic.Application.Views.Desktop/Hosting) is another protocol, whose region-page push
    // names a page as disconnected (specs/desktop/host-protocol.md, region-page).
    if (entry.name === "Hosting") continue;
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

// The .NET producer and the npm client speak the specification's version; the engineering job fails when one of
// them moves without the other.
test("the .NET runtime, the npm runtime and the specification name one protocol version", () => {
  const dotnet = readFileSync(resolve(root, "packages/dotnet/Runic.Application.Views/Generated/BridgeRuntime.cs"), "utf8")
    .match(/public const int Version = (\d+);/)?.[1];
  const npm = readFileSync(resolve(root, "packages/web/views/src/version.ts"), "utf8").match(/export const bridgeProtocol = (\d+);/)?.[1];
  const spec = readFileSync(resolve(root, "specs/application/README.md"), "utf8").match(/This is the Runic Application Views protocol, version (\d+),/)?.[1];
  assert.ok(dotnet, "BridgeProtocol.Version not found");
  assert.ok(npm, "bridgeProtocol not found");
  assert.ok(spec, "the specification's version not found");
  assert.equal(dotnet, spec, "BridgeProtocol.Version differs from the specification's version");
  assert.equal(npm, spec, "bridgeProtocol differs from the specification's version");
});

// appearance/1's `override` (0.19): the members the specification lets a host decide are the ones the npm store reads.
test("the specification's appearance override members are the npm store's", () => {
  const spec = readFileSync(resolve(root, "specs/application/README.md"), "utf8");
  const row = spec.split("\n").find(line => line.startsWith("| `override` |"));
  assert.ok(row, "the appearance push's override row not found");
  const specified = [...row.matchAll(/`"([A-Za-z]+)"`/g)].map(match => match[1]).sort();
  const store = readFileSync(resolve(root, "packages/web/views/src/appearance.ts"), "utf8")
    .match(/const overrideMembers = \[([^\]]*)\] as const;/)?.[1];
  assert.ok(store, "overrideMembers not found in appearance.ts");
  assert.deepEqual([...store.matchAll(/"([A-Za-z]+)"/g)].map(match => match[1]).sort(), specified);
  assert.deepEqual(specified, ["colorScheme", "contrast", "reducedMotion"]);
});

// hosted-regions/1's `colorScheme` (0.19): both content rows and the update row carry it, with the values the
// <runic-hosted-region> client reads.
test("the specification's hosted region colorScheme is in the content and update pushes the client reads", () => {
  const spec = readFileSync(resolve(root, "specs/application/README.md"), "utf8").split("\n");
  const rows = spec.filter(line => /^\| `hosted-region-(content|update)` \|/.test(line));
  assert.equal(rows.length, 3, "two content rows (frame, component) and the update row");
  for (const row of rows) assert.match(row, /optional `colorScheme`: `"light"`, `"dark"` or `null`/, row.slice(0, 40));
  const client = readFileSync(resolve(root, "packages/web/views/src/hosted-region.ts"), "utf8");
  assert.match(client, /function readColorScheme\(value: unknown\): "light" \| "dark" \| null \{\n\s*return value === "light" \|\| value === "dark" \? value : null;/);
  assert.equal(client.match(/readColorScheme\(push\["colorScheme"\]\)/g)?.length, 2, "read from the content and the update push");
});
