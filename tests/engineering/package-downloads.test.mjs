import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { compare, downloadsOf, listPath, merge } from "../../eng/dependencies/package-downloads.mjs";

const root = resolve(import.meta.dir, "../..");
const hash = letter => `${letter.repeat(86)}==`;

test("PackageDownload items are read from every framework of project.assets.json with their exact version", () => {
  const assets = { project: { frameworks: {
    "net10.0": { downloadDependencies: [{ name: "Runic.Platform", version: "[0.10.0, 0.10.0]" }] },
    "net10.0-windows": { downloadDependencies: [{ name: "Microsoft.WindowsDesktop.App.Ref", version: "[10.0.12]" }] },
  } } };
  expect(downloadsOf(assets)).toEqual([{ id: "Runic.Platform", version: "0.10.0" }, { id: "Microsoft.WindowsDesktop.App.Ref", version: "10.0.12" }]);
  expect(downloadsOf({ project: { frameworks: { "net10.0": {} } } })).toEqual([]);
  expect(() => downloadsOf({ project: { frameworks: { "net10.0": { downloadDependencies: [{ name: "A", version: "[1.0.0, )" }] } } } })).toThrow("exact version");
});

test("a restored download must be listed with the same content hash; listed packages a restore did not need are fine", () => {
  const list = [{ id: "Runic.Platform", version: "0.10.0", contentHash: hash("A") }, { id: "Other", version: "1.0.0", contentHash: hash("B") }];
  const hashes = { "Runic.Platform": hash("A"), "Changed": hash("C") };
  const hashOf = ({ id }) => { if (!hashes[id]) throw new Error(`${id} has no metadata.`); return hashes[id]; };
  expect(compare(list, [{ id: "Runic.Platform", version: "0.10.0" }, { id: "Runic.Platform", version: "0.10.0" }], hashOf)).toEqual([]);
  expect(compare(list, [{ id: "Runic.Platform", version: "0.11.0" }], hashOf)).toEqual(["Runic.Platform 0.11.0 is not listed."]);
  expect(compare([...list, { id: "Changed", version: "1.0.0", contentHash: hash("D") }], [{ id: "Changed", version: "1.0.0" }], hashOf))
    .toEqual([`Changed 1.0.0 has content hash ${hash("C")}; the list has ${hash("D")}.`]);
  expect(compare(list, [{ id: "Other", version: "1.0.0" }], hashOf)).toEqual(["Other has no metadata."]);
});

test("writing replaces other versions of a restored id and keeps packages of other platforms", () => {
  const list = [
    { id: "Runic.Platform", version: "0.9.0", contentHash: hash("A") },
    { id: "Microsoft.NETCore.App.Runtime.win-x64", version: "10.0.12", contentHash: hash("B") },
  ];
  expect(merge(list, [{ id: "Runic.Platform", version: "0.10.0", contentHash: hash("C") }])).toEqual([
    { id: "Microsoft.NETCore.App.Runtime.win-x64", version: "10.0.12", contentHash: hash("B") },
    { id: "Runic.Platform", version: "0.10.0", contentHash: hash("C") },
  ]);
});

test("the list is sorted, unique, hashed and names the current validation baselines", () => {
  const list = JSON.parse(readFileSync(listPath, "utf8"));
  const keys = list.map(item => `${item.id.toLowerCase()}/${item.version.toLowerCase()}`);
  expect(keys).toEqual([...new Set(keys)].sort((a, b) => a.localeCompare(b, "en")));
  for (const item of list) {
    expect(Object.keys(item)).toEqual(["id", "version", "contentHash"]);
    expect(item.contentHash).toMatch(/^[A-Za-z0-9+/]{86}==$/);
  }
  const versions = readFileSync(resolve(root, "eng/Versions.props"), "utf8");
  const property = name => new RegExp(`<${name}>([^<]+)</${name}>`).exec(versions)[1];
  const baselines = [property("RunicPackageValidationBaselineVersion"), property("RunicCsWebUiPackageValidationBaselineVersion")];
  const runic = list.filter(item => item.id.startsWith("Runic."));
  expect(runic.length).toBeGreaterThan(0);
  for (const item of runic) expect(baselines).toContain(item.version);
});
