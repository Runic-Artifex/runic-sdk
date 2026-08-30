import assert from 'node:assert/strict';
import test from 'node:test';
import { verifyReceipt } from './verify.mjs';

const journey = () => ({
  schema: 'runic.controlled-clean-room-conformance/1',
  isolation: { inputs: 'isolated-copy', nuget: 'receipt-verified-local-only', npm: 'receipt-verified-local-only' },
  freezeReceipt: { sha256: 'b'.repeat(64), schema: 'runic.controlled-nonpublic-profile-freeze-repeat/1' },
  frozenProfile: { sha256: 'a'.repeat(64), profiles: { 'csharp-host': { source: {} } } },
  consumers: {},
  rejections: ['source-project-references', 'ambient-package-cache', 'remote-endpoints', 'manifest-reference-fingerprint-locale-skew', 'structured-localization-flattening'],
  externalActions: { requests: 0, bearerChanges: 0, corsChanges: 0, proxyChanges: 0 },
});

test('accepts a deterministic controlled clean-room aggregate', () => {
  const value = journey();
  assert.deepEqual(verifyReceipt({ schema: 'runic.controlled-clean-room-conformance-repeat/1', journeys: [value, structuredClone(value)] }, value), { ok: true, errors: [] });
});

test('rejects softened or forged repeated aggregate evidence', () => {
  const first = journey(), second = journey();
  second.rejections.pop();
  const report = verifyReceipt({ schema: 'runic.controlled-clean-room-conformance-repeat/1', journeys: [first, second] }, first);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /two deterministic|aggregate boundary/);
});
