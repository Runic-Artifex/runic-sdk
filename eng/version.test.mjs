import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";
import { root } from "./run.mjs";
import { bumpVersionFiles, checkVersions, readVersionFiles } from "./version.mjs";

const files = () => ({
  "eng/workspace.json": '{\n  "schemaVersion": 2,\n  "version": "0.6.0-preview.1",\n  "npm": [\n    { "name": "@runic-artifex/views", "path": "packages/web/views" },\n    { "name": "@runic-artifex/react", "path": "packages/web/react" }\n  ]\n}\n',
  "eng/Versions.props": "<Project>\n  <PropertyGroup>\n    <RunicSdkVersion>0.6.0-preview.1</RunicSdkVersion>\n    <RunicPackageValidationBaselineVersion>0.6.0-preview.1</RunicPackageValidationBaselineVersion>\n  </PropertyGroup>\n</Project>\n",
  "packages/web/views/package.json": '{\n  "name": "@runic-artifex/views",\n  "version": "0.6.0-preview.1",\n  "devDependencies": { "typescript": "6.0.3" }\n}\n',
  "packages/web/react/package.json": '{\n  "name": "@runic-artifex/react",\n  "version": "0.6.0-preview.1",\n  "peerDependencies": { "@runic-artifex/views": "0.6.0-preview.1", "react": "0.6.0-preview.1" }\n}\n',
});

test("the committed release-train version copies agree with eng/workspace.json", () => {
  assert.equal(checkVersions(readVersionFiles()), JSON.parse(readFileSync(resolve(root, "eng/workspace.json"), "utf8")).version);
});

test("a mismatched version copy fails", () => {
  for (const [path, from, to] of [
    ["eng/Versions.props", "<RunicSdkVersion>0.6.0-preview.1", "<RunicSdkVersion>0.6.0-preview.2"],
    ["packages/web/views/package.json", '"version": "0.6.0-preview.1"', '"version": "0.6.0"'],
    ["packages/web/react/package.json", '"@runic-artifex/views": "0.6.0-preview.1"', '"@runic-artifex/views": "^0.6.0-preview.1"'],
    ["eng/Versions.props", "<RunicSdkVersion>0.6.0-preview.1</RunicSdkVersion>", ""],
  ]) {
    const value = files();
    value[path] = value[path].replace(from, to);
    assert.throws(() => checkVersions(value), undefined, `${path}: ${to}`);
  }
});

test("version bump rewrites every copy and keeps the validation baseline", () => {
  const next = bumpVersionFiles(files(), "0.7.0-preview.1");
  assert.equal(checkVersions(next), "0.7.0-preview.1");
  assert.match(next["eng/Versions.props"], /<RunicPackageValidationBaselineVersion>0\.6\.0-preview\.1</);
  assert.match(next["packages/web/react/package.json"], /"react": "0\.6\.0-preview\.1"/, "external dependencies are untouched");
  for (const invalid of [undefined, "0.7", "v0.7.0", "0.7.0-preview..1"])
    assert.throws(() => bumpVersionFiles(files(), invalid));
});

test("Runic.CommandLine has its own exact pin, independent of the SDK version", () => {
  const props = readFileSync(resolve(root, "Directory.Packages.props"), "utf8");
  const pins = ["Runic.CommandLine", "Runic.CommandLine.Spectre"].map(name =>
    props.match(new RegExp(`<PackageVersion Include="${name.replaceAll(".", "\\.")}" Version="([^"]+)" />`))?.[1]);
  for (const pin of pins) assert.match(pin ?? "", /^\d+\.\d+\.\d+(?:-[\w.]+)?$/, "Runic.CommandLine pins must be exact released versions");
  assert.equal(pins[0], pins[1], "Runic.CommandLine and Runic.CommandLine.Spectre are released together");
});
