#!/usr/bin/env node
import { spawn } from "node:child_process";
import { readFile } from "node:fs/promises";
import { join, resolve } from "node:path";

const schema = "runic.editor-structural-quality/1";
const repeatSchema = "runic.editor-structural-quality-repeat/1";
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);

const run = (command, args, cwd, env = {}) => new Promise((done) => {
  const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ["ignore", "pipe", "pipe"] });
  const output = [];
  child.stdout.on("data", (value) => output.push(value));
  child.stderr.on("data", (value) => output.push(value));
  child.on("error", (error) => done({ ok: false, exitCode: null, output: String(error) }));
  child.on("close", (exitCode) => done({ ok: exitCode === 0, exitCode, output: Buffer.concat(output).toString("utf8") }));
});

const requireSuccess = (name, result) => {
  if (!result.ok) throw new Error(`${name} failed:\n${result.output.slice(-4096)}`);
};

const phase = (name, result) => ({ name, status: "passed", exitCode: result.exitCode });

async function source(root) {
  const [revision, tree] = await Promise.all([
    run("git", ["rev-parse", "HEAD"], root),
    run("git", ["rev-parse", "HEAD^{tree}"], root),
  ]);
  requireSuccess("source revision", revision);
  requireSuccess("source tree", tree);
  return { revision: revision.output.trim(), tree: tree.output.trim() };
}

function bridgeEvidence(output) {
  const rows = output.trim().split("\n").filter((line) => /^(transport|effect)-returned-batch,/.test(line));
  const expected = [
    ["transport-returned-batch", 1, 2], ["effect-returned-batch", 1, 2],
    ["transport-returned-batch", 256, 2], ["effect-returned-batch", 256, 2],
    ["transport-returned-batch", 1024, 2], ["effect-returned-batch", 1024, 2],
  ];
  if (rows.length !== expected.length) throw new Error("Bridge structural gate did not emit its complete fixed matrix.");
  for (const [index, row] of rows.entries()) {
    const [scenario, sizeText, repetitionsText, , , framesText, eventsText] = row.split(",");
    const [expectedScenario, expectedSize, expectedRepetitions] = expected[index];
    const size = Number(sizeText), repetitions = Number(repetitionsText), frames = Number(framesText), events = Number(eventsText);
    if (scenario !== expectedScenario || size !== expectedSize || repetitions !== expectedRepetitions ||
      events !== size * repetitions || frames !== repetitions + (scenario === "effect-returned-batch" ? 1 : 0)) {
      throw new Error(`Bridge structural gate row ${index} did not preserve returned-frame or validated-delivery bounds.`);
    }
  }
  return { returnedFrames: "exact", schemaValidatedDelivery: "exact", fixedBatches: [1, 256, 1024] };
}

async function manifest(editor) {
  const candidate = join(editor, "obj", "Release", "net10.0", "translations", "editor.esm", "web-module-manifest-v1.json");
  await readFile(candidate, "utf8");
  return candidate;
}

async function one(toolkit, editor, provenance) {
  const phases = [];
  const bridgeGate = await run("npm", ["run", "verify:application-bridge-performance"], toolkit);
  requireSuccess("Bridge structural performance gate", bridgeGate);
  phases.push(phase("bridge-structural-gate", bridgeGate));
  const bridge = bridgeEvidence(bridgeGate.output);

  const bridgeTests = await run("npm", ["--workspace", "@runic-artifex/application-bridge", "test"], toolkit);
  requireSuccess("Bridge bounded-command package tests", bridgeTests);
  if (!bridgeTests.output.includes("returned host batches enforce the browser item limit") || !bridgeTests.output.includes("the browser retains command identifiers only while requests are pending")) {
    throw new Error("Bridge package tests did not demonstrate the bounded command paths.");
  }
  phases.push(phase("bridge-bounded-command-tests", bridgeTests));

  const build = await run("nix", ["develop", editor, "-c", "dotnet", "build", join(editor, "RunicTranslations.Editor.csproj"), "--configuration", "Release", "-p:RunicTranslationsBuildMode=Verification"], editor);
  requireSuccess("generated-manifest-aware Editor build", build);
  phases.push(phase("editor-generated-manifest-build", build));
  const frontend = join(editor, "Frontend");
  const frontendManifest = await manifest(editor);
  const check = await run("npm", ["run", "check"], frontend, { RUNIC_TRANSLATIONS_MANIFEST: frontendManifest });
  requireSuccess("Editor Svelte check", check);
  phases.push(phase("editor-svelte-check", check));

  const tests = [
    ["editor-model-scale", ["--expose-gc", "test/verify-review-model.mjs"], "PASS: review quality is deterministic;", "OBSERVATION:"],
    ["editor-keyboard-a11y", ["test/verify-keyboard-a11y.mjs"], "PASS: headless Editor keyboard and accessibility semantics", undefined],
    ["editor-command-palette", ["test/verify-command-palette.mjs"], "PASS: command palette exposes", undefined],
  ];
  for (const [name, args, required, observation] of tests) {
    const result = await run("node", args, frontend);
    requireSuccess(name, result);
    if (!result.output.includes(required) || (observation !== undefined && !result.output.includes(observation))) {
      throw new Error(`${name} did not emit its required bounded evidence.`);
    }
    phases.push(phase(name, result));
  }

  return {
    schema,
    localProfiles: provenance,
    bridge,
    model: { messages: 50_000, reviewLocales: 100, retainedHeapMiBMaximum: 256, timing: "observation-only" },
    keyboardAccessibility: { commandSearch: "focused", recoveryFocusOrder: ["rollback", "complete"], labelsAndLandmarks: "checked", forcedColors: "checked" },
    phases,
  };
}

export function verifyReceipt(receipt, supplied) {
  const errors = [];
  if (receipt?.schema !== repeatSchema || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push("two quality journeys are required");
  const expectedBridge = { returnedFrames: "exact", schemaValidatedDelivery: "exact", fixedBatches: [1, 256, 1024] };
  const expectedModel = { messages: 50_000, reviewLocales: 100, retainedHeapMiBMaximum: 256, timing: "observation-only" };
  const expectedKeyboard = { commandSearch: "focused", recoveryFocusOrder: ["rollback", "complete"], labelsAndLandmarks: "checked", forcedColors: "checked" };
  const phaseNames = ["bridge-structural-gate", "bridge-bounded-command-tests", "editor-generated-manifest-build", "editor-svelte-check", "editor-model-scale", "editor-keyboard-a11y", "editor-command-palette"];
  for (const journey of receipt?.journeys ?? []) {
    if (journey?.schema !== schema || !same(journey?.localProfiles, supplied) || !same(journey?.bridge, expectedBridge) || !same(journey?.model, expectedModel) || !same(journey?.keyboardAccessibility, expectedKeyboard)) errors.push("quality evidence mismatch");
    if (!Array.isArray(journey?.phases) || !same(journey.phases.map((item) => item.name), phaseNames) || journey.phases.some((item) => item.status !== "passed" || item.exitCode !== 0)) errors.push("quality phases mismatch");
  }
  if (receipt?.journeys?.length === 2 && !same(receipt.journeys[0], receipt.journeys[1])) errors.push("quality journeys are not deterministic");
  return { ok: errors.length === 0, errors };
}

export async function runTwice(toolkitRoot, editorRoot) {
  const toolkit = resolve(toolkitRoot), editor = resolve(editorRoot);
  const supplied = { toolkit: await source(toolkit), editor: await source(editor) };
  const receipt = { schema: repeatSchema, journeys: [await one(toolkit, editor, supplied), await one(toolkit, editor, supplied)] };
  const report = verifyReceipt(receipt, supplied);
  if (!report.ok) throw new Error(report.errors.join("\n"));
  return receipt;
}

if (import.meta.main) {
  const [command, toolkitRoot, editorRoot, receiptPath] = process.argv.slice(2);
  if (command === "run-twice" && toolkitRoot && editorRoot && !receiptPath) {
    process.stdout.write(`${JSON.stringify(await runTwice(toolkitRoot, editorRoot), null, 2)}\n`);
  } else if (command === "verify-twice" && toolkitRoot && editorRoot && receiptPath) {
    const supplied = { toolkit: await source(resolve(toolkitRoot)), editor: await source(resolve(editorRoot)) };
    const report = verifyReceipt(JSON.parse(await readFile(receiptPath, "utf8")), supplied);
    if (!report.ok) throw new Error(report.errors.join("\n"));
  } else {
    throw new Error("Usage: verify.mjs run-twice <runic-toolkit> <runic-translations-editor> | verify-twice <runic-toolkit> <runic-translations-editor> <receipt>");
  }
}
