import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { test } from "node:test";
import { root } from "../../eng/run.mjs";

// docs/msbuild.md lists every MSBuild property and item that a Runic package reads from an application's project
// (#1169). This test reads the packages' props and targets and fails for a public name (one without the `_Runic`
// prefix) that the page does not mention. Former names, which the targets only read to report their rename or
// removal, are not settings; the targets declare them, so they are recognized from there.
const page = readFileSync(resolve(root, "docs/msbuild.md"), "utf8");
const buildDirectories = ["build", "buildTransitive", "buildMultiTargeting"];

function buildFiles() {
  const packages = resolve(root, "packages/dotnet");
  const files = [];
  const walk = directory => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) walk(path);
      else if (/\.(props|targets)$/.test(entry.name)) files.push(path);
    }
  };
  for (const name of readdirSync(packages)) {
    for (const build of buildDirectories) {
      const directory = join(packages, name, build);
      if (existsSync(directory)) walk(directory);
    }
  }
  return files;
}

// A former name: an item of the Views targets' rename and removal tables, or the subject of a RUNICPA108 rename error.
const formerNamePatterns = [
  /<_Runic\w*(?:Renamed|Removed)Name\s+Include="(Runic\w+)"/g,
  /The Runic build property (Runic\w+) is renamed/g,
];

test("docs/msbuild.md names every public property and item the build files read", () => {
  const files = buildFiles();
  assert.ok(files.length > 10, `expected the packages' build files, found ${files.length}`);
  const reads = new Map();
  const former = new Set();
  for (const file of files) {
    const text = readFileSync(file, "utf8");
    // $(Name) reads a property, @(Name) an item; %(Name) is item metadata, which the page lists only for D-Bus.
    for (const [, name] of text.matchAll(/[$@]\((Runic[A-Za-z0-9]+)/g)) {
      if (!reads.has(name)) reads.set(name, new Set());
      reads.get(name).add(relative(root, file));
    }
    for (const pattern of formerNamePatterns) for (const [, name] of text.matchAll(pattern)) former.add(name);
  }
  const missing = [...reads]
    .filter(([name]) => !former.has(name) && !page.includes(`\`${name}\``))
    .map(([name, where]) => `${name} (${[...where].join(", ")})`);
  assert.deepEqual(missing, [], "add these names to docs/msbuild.md, or start an internal one with _Runic");
});
