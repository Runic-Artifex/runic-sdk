import assert from 'node:assert/strict';
import test from 'node:test';
import { verifyReceipt } from './verify.mjs';

const journey = () => ({
  schema: 'runic.local-nonpublic-readiness-audit/1', publication: 'forbidden',
  isolation: { inputs: 'isolated-copy', transport: 'none' },
  retainedProfiles: { 'csharp-host': {}, 'local-application-bridge': {}, 'editor-desktop': {}, 'd008-hosted-product': {} },
  evidence: {}, postFreezePackageGate: { status: 'passed-not-retained' },
  nativeCapability: { status: 'unavailable', capability: 'private-file-handler-streaming-unavailable', abiOracle: 'passing' },
  externalActions: { requests: 0, signatures: 0, updates: 0, uploads: 0, releases: 0, tags: 0 },
});

test('accepts a deterministic local non-public readiness audit', () => { const value = journey(); assert.deepEqual(verifyReceipt({ schema: 'runic.local-nonpublic-readiness-audit-repeat/1', journeys: [value, structuredClone(value)] }, value), { ok: true, errors: [] }); });
test('rejects a softened non-public boundary or post-freeze retention claim', () => { const first = journey(), second = journey(); second.publication = 'published'; second.postFreezePackageGate.status = 'retained'; second.externalActions.updates = 1; const report = verifyReceipt({ schema: 'runic.local-nonpublic-readiness-audit-repeat/1', journeys: [first, second] }, first); assert.equal(report.ok, false); assert.match(report.errors.join('\n'), /two deterministic|readiness-audit boundary/); });
