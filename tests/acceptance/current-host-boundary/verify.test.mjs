import assert from 'node:assert/strict';
import test from 'node:test';
import { BRIDGE_CONTRACT, BRIDGE_MANIFEST_SHA256, verifyReceiptLinks } from './verify.mjs';

const releaseManifest = { revision: 'a'.repeat(40), tree: 'b'.repeat(40) };
const repeat = (journey) => ({ journeys: [journey, structuredClone(journey)] });
function receipts() {
  const host = { releaseManifest, bridgeManifest: { protocol: { identity: BRIDGE_CONTRACT.identity, version: BRIDGE_CONTRACT.version }, contractFingerprint: BRIDGE_CONTRACT.fingerprint } };
  const svelte = { releaseManifest, bridgeContract: structuredClone(BRIDGE_CONTRACT), bridgeManifestSha256: BRIDGE_MANIFEST_SHA256, candidates: [{ identity: '@runic-artifex/application-bridge' }, { identity: '@runic-artifex/svelte' }] };
  const angular = { releaseManifest, bridgeContract: structuredClone(BRIDGE_CONTRACT), bridgeManifestSha256: BRIDGE_MANIFEST_SHA256, candidates: [{ identity: '@runic-artifex/application-bridge' }, { identity: '@runic-artifex/angular' }] };
  return [repeat(host), repeat(svelte), repeat(angular)];
}
test('cross-framework receipt linkage accepts one generated host contract', () => assert.deepEqual(verifyReceiptLinks(...receipts()), { ok: true, errors: [] }));
test('cross-framework receipt linkage rejects forged contract or candidate evidence', () => { const [host, svelte, angular] = receipts(); for (const journey of svelte.journeys) { journey.bridgeContract.fingerprint = 'forged'; journey.bridgeManifestSha256 = 'a'.repeat(64); } for (const journey of angular.journeys) journey.candidates[1].identity = '@runic-artifex/forged'; const report = verifyReceiptLinks(host, svelte, angular); assert.equal(report.ok, false); assert.match(report.errors.join('\n'), /Svelte generated contract|Svelte generated manifest|Angular candidate linkage/); });
