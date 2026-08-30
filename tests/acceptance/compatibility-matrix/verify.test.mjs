import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import test from 'node:test';
import { LANE_NAMES, RECEIPT_SCHEMA, projectCompatibilityMatrix, verifyReceipt } from './verify.mjs';

const manifestPath = process.env.RUNIC_RELEASE_MANIFEST
  ? resolve(process.env.RUNIC_RELEASE_MANIFEST)
  : resolve(import.meta.dirname, '../../../.github/runic.release.json');
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
const authority = { path: 'runic.release.json', revision: 'a'.repeat(40), tree: 'b'.repeat(40), digest: 'c'.repeat(64), publicPackageCounts: { nuget: 25, npm: 5 }, before: { revision: 'a'.repeat(40), tree: 'b'.repeat(40), status: '' }, after: { revision: 'a'.repeat(40), tree: 'b'.repeat(40), status: '' } };
const receipt = () => ({ schema: RECEIPT_SCHEMA, releaseManifest: structuredClone(authority), authority: structuredClone(manifest), matrix: projectCompatibilityMatrix(manifest) });

test('consumer fixture projects all three compatibility lanes from the release authority', () => {
  const value = receipt();
  assert.deepEqual(value.matrix.lanes.map((lane) => lane.name), LANE_NAMES);
  assert.ok(value.matrix.lanes.every((lane) => lane.products.length === lane.versions.length));
  assert.deepEqual(verifyReceipt(value, authority, manifest), { ok: true, errors: [] });
});

test('consumer fixture fails closed for forged or unsupported lane inputs', () => {
  const missing = structuredClone(manifest);
  missing.compatibilityTrains[0].lanes = missing.compatibilityTrains[0].lanes.filter((lane) => lane.name !== 'previous-supported');
  assert.throws(() => projectCompatibilityMatrix(missing), /requires exactly one 'previous-supported' lane/);

  const forged = receipt();
  forged.matrix.lanes[0].versions[0].version = { state: 'published', value: '9.9.9-forged' };
  forged.authority.policy.noForwardingPackages = false;
  const report = verifyReceipt(forged, authority, manifest);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /compatibility matrix projection mismatch/);
  assert.match(report.errors.join('\n'), /release authority content mismatch/);
});
