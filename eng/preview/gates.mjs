import assert from 'node:assert/strict';
import { validateCandidate } from './artifacts.mjs';
import { acceptancePolicy, validatePolicy } from './policy.mjs';
export const requiredGates = acceptancePolicy('full-v1').required;
export function verifyGates(candidate, evidence) {
  validateCandidate(candidate);
  const policy = candidate.acceptancePolicy ?? acceptancePolicy('full-v1');
  validatePolicy(policy);
  // Legacy arrays remain supported only for the full original policy.
  const receipts = Array.isArray(evidence) ? evidence : evidence.receipts;
  if (Array.isArray(evidence)) assert.equal(policy.profile, 'full-v1', 'Demo evidence requires an explicit policy and deferral envelope');
  else {
    assert.equal(evidence.schema, 'runic.preview-evidence/1');
    assert.deepEqual(evidence.policy, policy, 'Evidence policy differs from sealed candidate');
  }
  assert(Array.isArray(receipts));
  assert.equal(new Set(receipts.map(r=>r.gate)).size, receipts.length, 'Duplicate receipt gate');
  for (const gate of policy.required) {
    const r = receipts.find(r=>r.gate === gate); assert(r, `Missing acceptance: ${gate}`);
    assert.equal(r.outcome, 'pass', `Acceptance incomplete: ${gate}`);
    assert.equal(r.candidateDigest, candidate.digest); assert.equal(r.source, candidate.source);
    for (const field of ['environment','scenario','validator','evidence']) assert(typeof r[field] === 'string' && r[field].trim().length > 0, `${gate}: missing ${field}`);
    assert(Number.isFinite(Date.parse(r.recordedAt)), `${gate}: invalid recordedAt`);
    assert.deepEqual(r.artifactHashes, Object.fromEntries(candidate.packages.map(p=>[p.file,p.sha256])), `${gate}: artifact hashes mismatch`);
    if (gate.startsWith('interactive-native-')) assert.equal(r.actualNativeInteraction, true, `${gate}: simulated evidence prohibited`);
    if (gate === 'soak-two-hours') assert(r.durationSeconds >= 7200, 'Soak shorter than two hours');
    if (gate === 'performance-matched-baseline') { assert.equal(r.baselineCommit, '5afb8b8d'); assert.equal(r.matchedMachineAndWorkload, true); assert.equal(r.unresolvedRegressionsAbove20Percent, 0); }
    if (gate.startsWith('accessibility-')) for (const key of ['keyboard','screenReader','focusRestoration','ime','highContrast','displayScaling']) assert.equal(r[key], 'pass', `${gate}: ${key}`);
  }
  assert(receipts.every(r => policy.required.includes(r.gate)), 'Unexpected receipt or deferred check presented as a pass');
  if (policy.profile === 'full-v1') assert.notEqual(receipts.find(r=>r.gate==='pilot-1').validator, receipts.find(r=>r.gate==='pilot-2').validator, 'Pilots must be independent developers');
}
