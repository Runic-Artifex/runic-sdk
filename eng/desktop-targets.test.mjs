import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { test } from "node:test";
import { root } from "./run.mjs";

// Package consumers receive Runic.Desktop.targets through buildTransitive. In this
// repository a ProjectReference does not, so Directory.Build.targets imports it only
// for projects that set RunicImportDesktopTargets. Keep that opt-in exact.
const desktop = "packages/dotnet/Runic.Desktop/Runic.Desktop.csproj";

function projectGraph() {
  const files = execFileSync("git", ["ls-files", "*.csproj"], { cwd: root, encoding: "utf8" }).trim().split("\n");
  return new Map(files.map(file => {
    const text = readFileSync(resolve(root, file), "utf8");
    const references = [...text.matchAll(/<ProjectReference\s+Include="([^"$]+)"/g)]
      .map(match => relative(root, resolve(root, dirname(file), match[1].replaceAll("\\", "/"))).replaceAll("\\", "/"));
    return [file, { text, references }];
  }));
}

test("Desktop build targets are imported exactly where Runic.Desktop is referenced", () => {
  const graph = projectGraph();
  const memo = new Map();
  const usesDesktop = file => {
    if (file === desktop) return true;
    if (memo.has(file)) return memo.get(file);
    memo.set(file, false);
    const result = (graph.get(file)?.references ?? []).some(usesDesktop);
    memo.set(file, result);
    return result;
  };
  assert.match(readFileSync(resolve(root, "Directory.Build.targets"), "utf8"),
    /<Import Project="packages\/dotnet\/Runic\.Desktop\/buildTransitive\/Runic\.Desktop\.targets" Condition="'\$\(RunicImportDesktopTargets\)' == 'true'" \/>/);
  const consumers = [];
  for (const [file, { text }] of graph) {
    const optedIn = /<RunicImportDesktopTargets>true<\/RunicImportDesktopTargets>/.test(text);
    // Libraries only flow the reference; the consuming executable applies the targets.
    const expected = !file.startsWith("packages/") && usesDesktop(file);
    assert.equal(optedIn, expected, `${file}: ${expected ? "set" : "remove"} RunicImportDesktopTargets`);
    if (expected) consumers.push(file);
  }
  assert.ok(consumers.length > 0);
});

// Runic.Desktop has no presentation server since 0.15 (X2): every Desktop executable in this repository references
// Runic.Desktop.Kestrel, directly or through a project, as a package consumer references the package. A build with
// -p:RunicDesktopServer=Native selects the native server instead (Directory.Build.targets).
const kestrel = "packages/dotnet/Runic.Desktop.Kestrel/Runic.Desktop.Kestrel.csproj";

test("Desktop executables reference a presentation server", () => {
  const graph = projectGraph();
  const reaches = (target, file, seen = new Set()) => {
    if (file === target) return true;
    if (seen.has(file)) return false;
    seen.add(file);
    return (graph.get(file)?.references ?? []).some(reference => reaches(target, reference, seen));
  };
  const executables = [];
  for (const [file, { text }] of graph) {
    if (!/<RunicImportDesktopTargets>true<\/RunicImportDesktopTargets>/.test(text)) continue;
    if (!/<OutputType>(Win)?Exe<\/OutputType>/.test(text)) continue;
    executables.push(file);
    assert.ok(reaches(kestrel, file), `${file}: reference ${kestrel}`);
  }
  assert.ok(executables.length > 0);
  assert.ok(!reaches(kestrel, desktop), "Runic.Desktop must not reference Runic.Desktop.Kestrel");
});
