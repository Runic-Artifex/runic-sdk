#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import {
  constants as zlibConstants,
  brotliCompressSync,
  gzipSync,
} from "node:zlib";

const repositoryRoot = resolve(import.meta.dirname, "..");
const measureOnly = process.argv.includes("--measure");
const budgetPath = resolve(repositoryRoot, "eng/frontend-production-budgets.json");
const budgets = measureOnly
  ? undefined
  : JSON.parse(readFileSync(budgetPath, "utf8"));
const frameworks = [
  ["react", "@runic-artifex/sample-todo-react"],
  ["vue", "@runic-artifex/sample-todo-vue"],
  ["svelte", "@runic-artifex/sample-todo-svelte"],
  ["angular", "@runic-artifex/sample-todo-angular"],
];
const measurements = {};

for (const [framework, workspace] of frameworks) {
  run("npm", ["run", "build:production", "--workspace", workspace]);
  const first = readManifest(framework);
  run("npm", ["run", "build:production", "--workspace", workspace]);
  const second = readManifest(framework);
  assertDeterministic(framework, first, second);
  measurements[framework] = measure(framework, second);
}

const report = {
  schema: "runic-toolkit.examples.frontend-production-measurement/1",
  method: {
    runtime: process.version,
    build: "two clean production builds in one fixed repository root",
    files: "application and stylesheet entrypoints from runic-toolkit.assets.json",
    gzip: "node:zlib gzipSync level 9",
    brotli: "node:zlib brotliCompressSync quality 11, generic mode",
  },
  frameworks: measurements,
};

if (measureOnly) {
  console.log(JSON.stringify(report, null, 2));
  process.exit(0);
}

if (budgets.schema !== "runic-toolkit.examples.frontend-production-budgets/1") {
  throw new Error("The frontend production budget file has an unsupported schema.");
}
for (const [framework, measurement] of Object.entries(measurements)) {
  const budget = budgets.frameworks[framework];
  if (budget === undefined) {
    throw new Error(`No production budget is declared for ${framework}.`);
  }
  for (const kind of ["rawBytes", "gzipBytes", "brotliBytes"]) {
    if (measurement[kind] > budget[kind]) {
      throw new Error(
        `${framework} ${kind} is ${measurement[kind]} bytes; budget is ${budget[kind]} bytes.`,
      );
    }
  }
}

console.log(JSON.stringify(report, null, 2));
console.log("Frontend production determinism and compressed-size budgets passed.");

function readManifest(framework) {
  const directory = resolve(
    repositoryRoot,
    `samples/Todo.Frontends/${framework}/dist`,
  );
  const manifest = JSON.parse(readFileSync(
    resolve(directory, "runic-toolkit.assets.json"),
    "utf8",
  ));
  if (manifest.mode !== "production") {
    throw new Error(`${framework} did not emit a production asset manifest.`);
  }
  return { directory, manifest };
}

function assertDeterministic(framework, first, second) {
  const firstFiles = stableFiles(first.manifest);
  const secondFiles = stableFiles(second.manifest);
  if (JSON.stringify(firstFiles) !== JSON.stringify(secondFiles)) {
    throw new Error(`${framework} production output changed between clean builds.`);
  }
}

function stableFiles(manifest) {
  return Object.fromEntries(
    Object.entries(manifest.files)
      .filter(([path]) => path !== "runic-toolkit.assets.json")
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([path, metadata]) => [
        path,
        { bytes: metadata.bytes, sha256: metadata.sha256 },
      ]),
  );
}

function measure(framework, output) {
  const paths = [
    output.manifest.entrypoints.app,
    output.manifest.entrypoints.styles,
  ];
  let rawBytes = 0;
  let gzipBytes = 0;
  let brotliBytes = 0;
  for (const path of paths) {
    const bytes = readFileSync(resolve(output.directory, path));
    if (bytes.includes("runic-toolkit.todo.mock/1")) {
      throw new Error(
        `${framework} production entrypoints contain the frontend-only Todo fixture.`,
      );
    }
    rawBytes += bytes.byteLength;
    gzipBytes += gzipSync(bytes, { level: 9 }).byteLength;
    brotliBytes += brotliCompressSync(bytes, {
      params: {
        [zlibConstants.BROTLI_PARAM_MODE]: zlibConstants.BROTLI_MODE_GENERIC,
        [zlibConstants.BROTLI_PARAM_QUALITY]: 11,
      },
    }).byteLength;
  }
  return {
    entrypoints: paths,
    rawBytes,
    gzipBytes,
    brotliBytes,
  };
}

function run(command, arguments_) {
  const result = spawnSync(command, arguments_, {
    cwd: repositoryRoot,
    stdio: "inherit",
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`${command} ${arguments_.join(" ")} exited with ${result.status}.`);
  }
}
