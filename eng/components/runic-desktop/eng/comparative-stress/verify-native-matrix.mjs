#!/usr/bin/env node
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { receiptSchema, verifyReceipt } from "./run.mjs";

export const schema = "runic.native-comparative-stress-matrix/1";
export const platforms = [
  { rid: "win-x64", platform: "win32", architecture: "x64" },
  { rid: "osx-x64", platform: "darwin", architecture: "x64" },
  { rid: "osx-arm64", platform: "darwin", architecture: "arm64" },
];

const revisionPattern = /^[a-f0-9]{40}$/;
const sha256 = (bytes) => createHash("sha256").update(bytes).digest("hex");
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const fail = (message) => { throw new Error(`native comparative matrix: ${message}`); };

function observation(receipt, implementation) {
  return receipt.observations?.find((item) => item.implementation === implementation);
}

export function createMatrix(entries, desktopRevision, csWebuiRevision) {
  if (!revisionPattern.test(desktopRevision) || !revisionPattern.test(csWebuiRevision)) fail("source revisions must be exact commits");
  const errors = [];
  const workloadDigests = new Set();
  const nodeVersions = new Set();
  const dotnetSdks = new Set();
  const result = [];
  for (const expected of platforms) {
    const entry = entries.find((item) => item.rid === expected.rid);
    if (!entry) { errors.push(`${expected.rid}: receipt is missing`); continue; }
    errors.push(...verifyReceipt(entry.receipt).map((error) => `${expected.rid}: ${error}`));
    if (entry.receipt.schema !== receiptSchema) errors.push(`${expected.rid}: current receipt schema is required`);
    if (entry.receipt.host?.platform !== expected.platform || entry.receipt.host?.architecture !== expected.architecture) errors.push(`${expected.rid}: host profile mismatch`);
    if (observation(entry.receipt, "runic-desktop")?.revision !== desktopRevision) errors.push(`${expected.rid}: Desktop revision mismatch`);
    if (observation(entry.receipt, "cs-webui")?.revision !== csWebuiRevision) errors.push(`${expected.rid}: CS-WebUI revision mismatch`);
    workloadDigests.add(entry.receipt.workloadSha256);
    nodeVersions.add(entry.receipt.host?.node);
    dotnetSdks.add(entry.receipt.host?.dotnetSdk);
    result.push({
      rid: expected.rid,
      host: entry.receipt.host,
      receipt: { path: entry.path, sha256: sha256(entry.bytes) },
    });
  }
  if (entries.length !== platforms.length || entries.some((entry) => !platforms.some((item) => item.rid === entry.rid))) errors.push("receipt set must contain exactly the supported native profiles");
  if (workloadDigests.size !== 1) errors.push("native receipts must use one workload");
  if (nodeVersions.size !== 1 || dotnetSdks.size !== 1) errors.push("native receipts must use one exact toolchain");
  if (errors.length) fail(errors.join("\n"));
  return {
    schema,
    sources: { runicDesktop: desktopRevision, csWebui: csWebuiRevision },
    toolchain: { node: [...nodeVersions][0], dotnetSdk: [...dotnetSdks][0] },
    workloadSha256: [...workloadDigests][0],
    platforms: result,
    interpretation: "raw-same-host-observations-only-not-a-cross-host-or-release-sla",
  };
}

async function entries(root) {
  return Promise.all(platforms.map(async ({ rid }) => {
    const path = join(`native-parity-and-stress-${rid}`, "comparative-stress", `${rid}.json`);
    const bytes = await readFile(join(root, path));
    let receipt;
    try { receipt = JSON.parse(bytes); } catch { fail(`${rid}: receipt is not JSON`); }
    return { rid, path, bytes, receipt };
  }));
}

async function main(argv) {
  const [command, rootValue, first, second] = argv;
  if (command === "run" && rootValue && first && second) {
    return process.stdout.write(`${JSON.stringify(createMatrix(await entries(resolve(rootValue)), first, second), null, 2)}\n`);
  }
  if (command === "verify" && rootValue && first && !second) {
    const expected = JSON.parse(await readFile(resolve(first), "utf8"));
    if (expected?.schema !== schema || !revisionPattern.test(expected?.sources?.runicDesktop ?? "") || !revisionPattern.test(expected?.sources?.csWebui ?? "")) fail("aggregate receipt is malformed");
    const actual = createMatrix(await entries(resolve(rootValue)), expected.sources.runicDesktop, expected.sources.csWebui);
    if (!same(actual, expected)) fail("aggregate receipt differs from the raw native evidence");
    return;
  }
  fail("usage: verify-native-matrix.mjs run <artifact-root> <desktop-revision> <cs-webui-revision> | verify <artifact-root> <matrix-receipt>");
}

if (import.meta.main) main(process.argv.slice(2)).catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
