#!/usr/bin/env node

import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { applicationNpmPackageIdentities } from "./application-npm-packages.mjs";

const [, , version, suppliedDirectory] = process.argv;
if (version === undefined || suppliedDirectory === undefined) {
  console.error("Usage: node eng/verify-npm-artifacts.mjs <package-version> <package-directory>");
  process.exit(2);
}

const directory = resolve(suppliedDirectory);
const expected = new Set(applicationNpmPackageIdentities);
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
  const toolingArchive = packageArchives.get("@runic-artifex/application-bridge-tooling");
  const install = spawnSync(
    process.platform === "win32" ? "npm.cmd" : "npm",
    ["install", "--ignore-scripts", "--no-audit", "--no-fund", archive, toolingArchive],
    { cwd: consumer, encoding: "utf8" },
  );
  if (install.status !== 0) throw new Error(`Could not install the npm artifact in isolation:\n${install.stderr}`);
  writeFileSync(join(consumer, "verify.mjs"), `
import { Effect, Schema } from "effect";
import { MockApplicationBridge, bridge, createApplicationBridgeController, defineApplicationBridgeContract, materializeApplicationBridgeContract } from "@runic-artifex/application-bridge";
const Command = Schema.TaggedStruct("InitializeApplication", {});
const Snapshot = Schema.Struct({ ready: Schema.Boolean });
const Receipt = Schema.TaggedStruct("Accepted", {});
const Event = Schema.TaggedStruct("Changed", {});
const definition = defineApplicationBridgeContract({ protocol: { identity: "runic.consumer", version: 1 }, csharp: { namespace: "Runic.Consumer", contractName: "Consumer" }, snapshot: Snapshot, commands: [bridge.command(Command, { receipt: Receipt })], events: [Event], errors: [], initialize: { _tag: "InitializeApplication" } });
const contract = materializeApplicationBridgeContract(definition, "c".repeat(64));
const layer = MockApplicationBridge({ initialize: () => Effect.succeed({ ready: true }), dispatch: () => Effect.succeed({ _tag: "Accepted" }) });
const controller = createApplicationBridgeController(contract, layer);
const snapshot = await controller.initialize();
await controller.dispose();
if (snapshot.ready !== true) throw new Error("The installed Application Bridge package did not execute.");
`, "utf8");
  mkdirSync(join(consumer, "src"));
  writeFileSync(join(consumer, "src", "application.bridge.ts"), `
import { Schema } from "effect";
import { bridge, defineApplicationBridgeContract } from "@runic-artifex/application-bridge";
const Command = Schema.TaggedStruct("InitializeApplication", {});
const Receipt = Schema.TaggedStruct("Initialized", {});
export default defineApplicationBridgeContract({ protocol:{identity:"runic.consumer",version:1}, csharp:{namespace:"Runic.Consumer",contractName:"Consumer"}, snapshot:Schema.Struct({ready:Schema.Boolean}).annotations({identifier:"Snapshot"}), commands:[bridge.command(Command,{receipt:Receipt})], events:[], errors:[], initialize:{_tag:"InitializeApplication"} });
`, "utf8");
  const generate = spawnSync(join(consumer, "node_modules", ".bin", "runic-bridge"), ["generate", "--ir", "Contract/bridge.ir.json"], { cwd: consumer, encoding: "utf8" });
  if (generate.status !== 0) throw new Error(`The isolated compiler failed:\n${generate.stderr}`);
  const check = spawnSync(join(consumer, "node_modules", ".bin", "runic-bridge"), ["check", "--ir", "Contract/bridge.ir.json"], { cwd: consumer, encoding: "utf8" });
  if (check.status !== 0) throw new Error(`The isolated compiler check failed:\n${check.stderr}`);
  const execute = spawnSync(process.execPath, [join(consumer, "verify.mjs")], { cwd: consumer, encoding: "utf8" });
  if (execute.status !== 0) throw new Error(`The isolated npm consumer failed:\n${execute.stderr}`);
} finally {
  rmSync(consumer, { recursive: true, force: true });
}
console.log(`Verified ${packageArchives.size} Runic Toolkit npm artifacts for ${version}.`);
