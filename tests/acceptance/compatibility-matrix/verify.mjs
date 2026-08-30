#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { releaseManifestAfter, releaseManifestFacts } from '../v0.2-baselines/metrics.mjs';

export const RECEIPT_SCHEMA = 'runic.compatibility-matrix/1';
export const LANE_NAMES = ['current', 'previous-supported', 'next-candidate'];

const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const sha256 = (value) => createHash('sha256').update(value).digest('hex');

function fail(message) {
  throw new Error(`compatibility matrix: ${message}`);
}

export function projectCompatibilityMatrix(manifest) {
  const train = manifest?.compatibilityTrains?.filter((item) => item?.id === 'v1.0');
  if (!Array.isArray(train) || train.length !== 1) fail('requires exactly one v1.0 train');

  const lanes = LANE_NAMES.map((name) => {
    const matches = train[0].lanes?.filter((lane) => lane?.name === name);
    if (!Array.isArray(matches) || matches.length !== 1) fail(`requires exactly one '${name}' lane`);
    const lane = matches[0];
    const products = lane.products;
    const versions = lane.versions;
    if (!Array.isArray(products) || !products.length || new Set(products).size !== products.length || products.some((product) => typeof product !== 'string' || !product)) fail(`lane '${name}' has invalid products`);
    if (!Array.isArray(versions) || versions.length !== products.length) fail(`lane '${name}' has invalid versions`);
    const versionByProduct = new Map();
    for (const entry of versions) {
      if (!entry || typeof entry.product !== 'string' || !products.includes(entry.product) || versionByProduct.has(entry.product)) fail(`lane '${name}' has an invalid version entry`);
      const version = entry.version;
      if (!version || !['unassigned', 'published'].includes(version.state) || (version.state === 'unassigned' && version.value !== null) || (version.state === 'published' && (typeof version.value !== 'string' || !version.value))) fail(`lane '${name}' has an invalid version state`);
      versionByProduct.set(entry.product, version);
    }
    return { name, products: [...products], versions: products.map((product) => ({ product, version: versionByProduct.get(product) })) };
  });

  return { train: train[0].id, lanes };
}

export function verifyReceipt(receipt, authority, expectedAuthority = undefined) {
  const errors = [];
  if (!receipt || receipt.schema !== RECEIPT_SCHEMA) errors.push('receipt schema mismatch');
  if (!same(receipt?.releaseManifest, authority)) errors.push('release authority identity mismatch');
  if (expectedAuthority !== undefined && !same(receipt?.authority, expectedAuthority)) errors.push('release authority content mismatch');
  try {
    if (!same(receipt?.matrix, projectCompatibilityMatrix(receipt?.authority))) errors.push('compatibility matrix projection mismatch');
  } catch (error) {
    errors.push(error.message);
  }
  return { ok: errors.length === 0, errors };
}

export async function createReceipt(manifestPath) {
  const authority = await releaseManifestFacts(manifestPath);
  const source = await readFile(manifestPath, 'utf8');
  if (sha256(source) !== authority.digest) fail('source does not match the committed authority');
  const manifest = JSON.parse(source);
  const receipt = {
    schema: RECEIPT_SCHEMA,
    releaseManifest: await releaseManifestAfter(manifestPath, authority),
    authority: manifest,
    matrix: projectCompatibilityMatrix(manifest),
  };
  const report = verifyReceipt(receipt, receipt.releaseManifest, manifest);
  if (!report.ok) throw new Error(report.errors.join('\n'));
  return receipt;
}

async function main() {
  const [command, manifestPath, receiptPath] = process.argv.slice(2);
  if (command === 'run' && manifestPath && !receiptPath) {
    process.stdout.write(`${JSON.stringify(await createReceipt(manifestPath), null, 2)}\n`);
    return;
  }
  if (command === 'verify' && manifestPath && receiptPath) {
    const expected = await createReceipt(manifestPath);
    const receipt = JSON.parse(await readFile(receiptPath, 'utf8'));
    const report = verifyReceipt(receipt, expected.releaseManifest, expected.authority);
    if (!report.ok || !same(receipt.matrix, expected.matrix)) throw new Error(report.errors.concat('compatibility matrix differs from the authority').join('\n'));
    return;
  }
  throw new Error('Usage: node eng/compatibility-matrix/verify.mjs run <runic.release.json> | verify <runic.release.json> <receipt.json>');
}

if (import.meta.main) main().catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
