import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";
import { root } from "./run.mjs";
import { bumpVersionFiles, checkVersions, readVersionFiles } from "./version.mjs";

const files = () => ({
  "eng/workspace.json": '{\n  "schemaVersion": 2,\n  "version": "0.6.0-preview.1",\n  "npm": [\n    { "name": "@runic-artifex/views", "path": "packages/web/views" },\n    { "name": "@runic-artifex/react", "path": "packages/web/react" }\n  ]\n}\n',
  "eng/Versions.props": "<Project>\n  <PropertyGroup>\n    <RunicSdkVersion>0.6.0-preview.1</RunicSdkVersion>\n    <RunicPackageValidationBaselineVersion>0.6.0-preview.1</RunicPackageValidationBaselineVersion>\n  </PropertyGroup>\n</Project>\n",
  "bun.lock": '{\n  "lockfileVersion": 1,\n  "workspaces": {\n    "packages/web/views": {\n      "name": "@runic-artifex/views",\n      "version": "0.6.0-preview.1",\n    },\n    "packages/web/react": {\n      "name": "@runic-artifex/react",\n      "version": "0.6.0-preview.1",\n    },\n  },\n}\n',
  "packages/web/views/package.json": '{\n  "name": "@runic-artifex/views",\n  "version": "0.6.0-preview.1",\n  "devDependencies": { "typescript": "6.0.3" }\n}\n',
  "packages/web/react/package.json": '{\n  "name": "@runic-artifex/react",\n  "version": "0.6.0-preview.1",\n  "peerDependencies": { "@runic-artifex/views": "0.6.0-preview.1", "react": "0.6.0-preview.1" }\n}\n',
  "examples/app/packages.lock.json": JSON.stringify({ version: 2, dependencies: { "net10.0": {
    "Runic.External": { type: "Direct", requested: "[0.6.0-preview.1, )", resolved: "0.6.0-preview.1" },
    "runic.desktop": { type: "Project", dependencies: { "Runic.External": "[0.6.0-preview.1, )", "Runic.Platform": "[0.6.0-preview.1, )" } },
    "runic.platform": { type: "Project" },
  } } }, null, 2) + "\n",
  "tests/fixture/packages.Fixture.Tests.lock.json": JSON.stringify({ version: 2, dependencies: { "net10.0": {
    "runic.navigation": { type: "Project", dependencies: { "Runic.Platform": "[0.6.0-preview.1, )" } },
    "runic.platform": { type: "Project" },
  } } }, null, 2) + "\n",
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
    ["bun.lock", '"name": "@runic-artifex/react",\n      "version": "0.6.0-preview.1"', '"name": "@runic-artifex/react",\n      "version": "0.5.0"'],
    ["examples/app/packages.lock.json", '"Runic.Platform": "[0.6.0-preview.1, )"', '"Runic.Platform": "[0.5.0, )"'],
    ["tests/fixture/packages.Fixture.Tests.lock.json", '"Runic.Platform": "[0.6.0-preview.1, )"', '"Runic.Platform": "[0.5.0, )"'],
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
  assert.match(next["examples/app/packages.lock.json"], /"Runic\.Platform": "\[0\.7\.0-preview\.1, \)"/);
  assert.equal(next["examples/app/packages.lock.json"].match(/0\.6\.0-preview\.1/g)?.length, 3, "external NuGet packages are untouched");
  assert.match(next["tests/fixture/packages.Fixture.Tests.lock.json"], /"Runic\.Platform": "\[0\.7\.0-preview\.1, \)"/, "project-named lock files are rewritten");
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
