#!/usr/bin/env node

import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";
import { spawnSync } from "node:child_process";

const [, , version, suppliedDirectory] = process.argv;
if (version === undefined || suppliedDirectory === undefined) {
  console.error("Usage: node eng/verify-npm-artifacts.mjs <package-version> <package-directory>");
  process.exit(2);
}

const directory = resolve(suppliedDirectory);
const expected = new Set([
  "@runic-artifex/application-bridge",
  "@runic-artifex/angular",
]);
const packageArchives = new Map();
const archives = readdirSync(directory).filter((file) => file.endsWith(".tgz"));
if (archives.length !== expected.size) throw new Error(`Expected ${expected.size} npm archives, found ${archives.length}.`);

for (const archive of archives) {
  const result = spawnSync("tar", ["-xOf", resolve(directory, archive), "package/package.json"], {
    encoding: "utf8",
  });
  if (result.status !== 0) throw new Error(`Could not inspect ${basename(archive)}.`);
  const manifest = JSON.parse(result.stdout);
  if (!expected.delete(manifest.name)) throw new Error(`Unexpected npm package '${manifest.name}'.`);
  packageArchives.set(manifest.name, resolve(directory, archive));
  if (manifest.version !== version) throw new Error(`${manifest.name} has version ${manifest.version}.`);
  if (manifest.license !== "MIT") throw new Error(`${manifest.name} must use MIT.`);
  if (manifest.repository?.url !== "git+https://github.com/Runic-Artifex/runic-toolkit.git") {
    throw new Error(`${manifest.name} has invalid repository provenance.`);
  }
}
if (expected.size !== 0) throw new Error(`Missing npm packages: ${[...expected].join(", ")}.`);

const consumer = mkdtempSync(join(tmpdir(), "runic-toolkit-npm-consumer."));
try {
  writeFileSync(join(consumer, "package.json"), JSON.stringify({ private: true, type: "module" }));
  const archive = packageArchives.get("@runic-artifex/application-bridge");
  const install = spawnSync("npm", ["install", "--ignore-scripts", "--no-package-lock", archive], {
    cwd: consumer,
    encoding: "utf8",
  });
  if (install.status !== 0) throw new Error(`Could not install the npm artifact in isolation:\n${install.stderr}`);
  writeFileSync(join(consumer, "verify.mjs"), `
import { Effect, Schema } from "effect";
import { MockApplicationBridge, createApplicationBridgeController, defineApplicationContract } from "@runic-artifex/application-bridge";
const Command = Schema.TaggedStruct("InitializeApplication", {});
const Snapshot = Schema.Struct({ ready: Schema.Boolean });
const Receipt = Schema.TaggedStruct("Accepted", {});
const Event = Schema.TaggedStruct("Changed", {});
const contract = defineApplicationContract({ identity: "runic.consumer", version: 1, fingerprint: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", command: Command, receipt: Receipt, event: Event, snapshot: Snapshot, initialize: { _tag: "InitializeApplication" } });
const layer = MockApplicationBridge({ initialize: () => Effect.succeed({ ready: true }), dispatch: () => Effect.succeed({ _tag: "Accepted" }) });
const bridge = createApplicationBridgeController(contract, layer);
const snapshot = await bridge.initialize();
await bridge.dispose();
if (snapshot.ready !== true) throw new Error("The installed Application Bridge package did not execute.");
`, "utf8");
  const execute = spawnSync(process.execPath, [join(consumer, "verify.mjs")], { cwd: consumer, encoding: "utf8" });
  if (execute.status !== 0) throw new Error(`The isolated npm consumer failed:\n${execute.stderr}`);
} finally {
  rmSync(consumer, { recursive: true, force: true });
}
console.log(`Verified ${packageArchives.size} Runic Toolkit npm artifacts for ${version}.`);
