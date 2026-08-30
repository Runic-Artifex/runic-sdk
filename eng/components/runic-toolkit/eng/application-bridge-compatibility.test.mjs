import assert from "node:assert/strict";
import test from "node:test";
import { cp, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { compareApplicationBridgeContracts } from "./application-bridge-compatibility.mjs";

const manifest = "protocol/application-bridge/setup/generated/bridge.manifest.json";

test("the structural report is deterministic and source-linked", async () => {
  const directory = await mkdtemp(join(tmpdir(), "runic-bridge-compat-"));
  const baselineDirectory = join(directory, "baseline");
  const candidateDirectory = join(directory, "candidate");
  await cp(dirname(manifest), baselineDirectory, { recursive: true });
  await cp(dirname(manifest), candidateDirectory, { recursive: true });
  const baseline = join(baselineDirectory, "bridge.manifest.json");
  const candidate = join(candidateDirectory, "bridge.manifest.json");
  const modified = JSON.parse(await readFile(candidate, "utf8"));
  modified.events.push("FutureEvent");
  await writeFile(candidate, `${JSON.stringify(modified)}\n`);
  const report = await compareApplicationBridgeContracts(baseline, candidate);
  assert.equal(report.classification, "additive");
  assert.equal(report.diagnostics[0].classification, "additive");
  assert.match(report.diagnostics[0].source, /candidate\/bridge\.manifest\.json#/);
});

test("hostile schema and metadata changes are classified as breaking with JSON pointers", async () => {
  const directory = await mkdtemp(join(tmpdir(), "runic-bridge-compat-hostile-"));
  const baselineDirectory = join(directory, "baseline");
  const candidateDirectory = join(directory, "candidate");
  await cp(dirname(manifest), baselineDirectory, { recursive: true });
  await cp(dirname(manifest), candidateDirectory, { recursive: true });
  const candidate = join(candidateDirectory, "bridge.manifest.json");
  const candidateManifest = JSON.parse(await readFile(candidate, "utf8"));
  candidateManifest.commands[0].advancesRevision = !candidateManifest.commands[0].advancesRevision;
  await writeFile(candidate, `${JSON.stringify(candidateManifest)}\n`);
  const schemaPath = join(candidateDirectory, candidateManifest.schemas.find((item) => item.kind === "command").file);
  const schema = JSON.parse(await readFile(schemaPath, "utf8"));
  schema.additionalProperties = true;
  schema.properties._tag.enum = ["HostileTag"];
  await writeFile(schemaPath, `${JSON.stringify(schema)}\n`);
  const report = await compareApplicationBridgeContracts(join(baselineDirectory, "bridge.manifest.json"), candidate);
  assert.equal(report.classification, "breaking");
  assert.ok(report.diagnostics.some((item) => item.code === "command-metadata-changed" && item.source.includes("#/commands/0/")));
  assert.ok(report.diagnostics.some((item) => item.code === "additional-properties-changed" && item.source.includes("#/additionalProperties")));
  assert.ok(report.diagnostics.some((item) => item.code === "enum-value-removed" && item.source.includes("#/properties/_tag")));
});

test("strict bidirectional readers treat enum additions and either required-set direction as breaking", async () => {
  const directory = await mkdtemp(join(tmpdir(), "runic-bridge-compat-strict-"));
  const baselineDirectory = join(directory, "baseline");
  const candidateDirectory = join(directory, "candidate");
  await cp(dirname(manifest), baselineDirectory, { recursive: true });
  await cp(dirname(manifest), candidateDirectory, { recursive: true });
  const candidate = join(candidateDirectory, "bridge.manifest.json");
  const candidateManifest = JSON.parse(await readFile(candidate, "utf8"));
  const schemaPath = join(candidateDirectory, candidateManifest.schemas.find((item) => item.kind === "command").file);
  const schema = JSON.parse(await readFile(schemaPath, "utf8"));
  schema.properties._tag.enum.push("FutureTag");
  schema.required = schema.required.filter((property) => property !== "_tag");
  await writeFile(schemaPath, `${JSON.stringify(schema)}\n`);
  const report = await compareApplicationBridgeContracts(join(baselineDirectory, "bridge.manifest.json"), candidate);
  assert.equal(report.classification, "breaking");
  assert.ok(report.diagnostics.some((item) => item.code === "enum-value-added"));
  assert.ok(report.diagnostics.some((item) => item.code === "required-set-changed"));
  assert.ok(report.diagnostics.some((item) => item.code === "property-became-optional"));
});
