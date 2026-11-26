// Checks the Runic Assets archive vectors (specs/assets/vectors):
// - crafted.json and packer.json are what generate.mjs writes;
// - every vector file against vector.schema.json, with unique case ids;
// - the manifest of every valid archive against specs/assets/manifest.schema.json, and that the schema rejects the
//   manifests of the cases whose error is in the shape it describes.
// The .NET reader runs the vectors in tests/dotnet/Runic.Assets.Tests (ArchiveVectorTests).
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readdirSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { inflateRawSync } from "node:zlib";
import test from "node:test";
import { root } from "./run.mjs";
import { validate } from "./json-schema.mjs";

const directory = resolve(root, "specs/assets/vectors");
const readJson = path => JSON.parse(readFileSync(path, "utf8"));
// The directory also holds the conformance driver's cases.json (eng/conformance/README.md).
const vectorFiles = readdirSync(directory)
  .filter(name => name.endsWith(".json") && !["vector.schema.json", "cases.json"].includes(name)).sort();
const cases = vectorFiles.flatMap(name => readJson(join(directory, name)).cases);

test("the archive vectors are what generate.mjs writes", () => {
  const result = spawnSync(process.execPath, [join(directory, "generate.mjs"), "--check"], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr + result.stdout);
});

test("every archive vector file follows its schema, with unique case ids", () => {
  const schema = readJson(join(directory, "vector.schema.json"));
  assert.deepEqual(vectorFiles, ["crafted.json", "packer.json"]);
  for (const name of vectorFiles) {
    const file = readJson(join(directory, name));
    assert.deepEqual(validate(schema, file), [], name);
    assert.equal(file.id, `assets.${name.slice(0, -".json".length)}`);
    for (const vector of file.cases) assert.ok(vector.id.startsWith(`${file.id}.`), vector.id);
  }
  const ids = cases.map(vector => vector.id);
  assert.equal(new Set(ids).size, ids.length);
  assert.equal(cases.length, 73);
});

// The manifest entry of an archive, from its central directory (enough ZIP for the vectors): null for an archive
// without a classic end record, or with ZIP64 records or offsets that do not lead to a central directory.
function manifestOf(bytes) {
  const end = bytes.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  if (end < 0 || bytes.readUInt16LE(end + 10) === 0xffff || bytes.readUInt32LE(end + 16) === 0xffffffff) return null;
  if (bytes.readUInt32LE(bytes.readUInt32LE(end + 16)) !== 0x02014b50) return null;
  let central = bytes.readUInt32LE(end + 16);
  for (let index = 0; index < bytes.readUInt16LE(end + 10) && central + 46 <= bytes.length; index++) {
    const nameLength = bytes.readUInt16LE(central + 28);
    const name = bytes.subarray(central + 46, central + 46 + nameLength).toString("utf8");
    if (name === "runic-assets.json") {
      const local = bytes.readUInt32LE(central + 42);
      if (local === 0xffffffff) return null;
      const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28);
      const data = bytes.subarray(start, start + bytes.readUInt32LE(central + 20));
      return bytes.readUInt16LE(central + 10) === 8 ? inflateRawSync(data) : data;
    }
    central += 46 + nameLength + bytes.readUInt16LE(central + 30) + bytes.readUInt16LE(central + 32);
  }
  return null;
}

test("the manifest schema accepts the valid archives' manifests and rejects shape errors", () => {
  const schema = readJson(resolve(root, "specs/assets/manifest.schema.json"));
  // JSON Schema's integer admits 13.0, and a schema sees neither duplicate members nor bytes; the reader rules do.
  const beyondSchema = new Set(["assets.crafted.length-fractional", "assets.crafted.length-exponent"]);
  let checked = 0;
  for (const vector of cases) {
    const bytes = manifestOf(Buffer.from(vector.input.value, "base64"));
    if (!bytes) continue;
    let manifest;
    try { manifest = JSON.parse(bytes.toString("utf8").replace(/^﻿/, "")); } catch { continue; }
    const errors = validate(schema, manifest);
    if (vector.expect.result === "valid") {
      assert.deepEqual(errors, [], vector.id);
      checked++;
    } else if (["manifest-version", "asset-invalid"].includes(vector.expect.error) && !beyondSchema.has(vector.id)) {
      assert.notDeepEqual(errors, [], `${vector.id}: the schema accepts it`);
      checked++;
    }
  }
  assert.ok(checked >= 20, `only ${checked} manifests checked`);
});
