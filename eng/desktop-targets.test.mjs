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
