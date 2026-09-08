import assert from 'node:assert/strict';
import { openSync, readFileSync, closeSync, fstatSync, lstatSync, constants } from 'node:fs';
import { resolve, join, parse, relative } from 'node:path';
import { validateCandidate, sha256 } from './artifacts.mjs';
import { assessSoak, minimumSoakDurationMs } from '../reliability/metrics.mjs';
import { acceptancePolicy, validatePolicy, demoMemoryTrendWaiver } from './policy.mjs';
export const requiredGates = acceptancePolicy('full-v1').required;
export function verifyGates(candidate, evidence, context = {}) {
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
    const waived = policy.profile === 'demo-preview' && gate === 'soak-thirty-minutes' && r.outcome === 'known-issue';
    if (waived) verifyMemoryTrendWaiver(candidate, r, context);
    else {
      assert.equal(r.outcome, 'pass', `Acceptance incomplete: ${gate}`);
      assert.equal(r.waiver, undefined, 'A waiver cannot be presented as a pass');
    }
    assert.equal(r.candidateDigest, candidate.digest); assert.equal(r.source, candidate.source);
    for (const field of ['environment','scenario','validator','evidence']) assert(typeof r[field] === 'string' && r[field].trim().length > 0, `${gate}: missing ${field}`);
    assert(Number.isFinite(Date.parse(r.recordedAt)), `${gate}: invalid recordedAt`);
    assert.deepEqual(r.artifactHashes, Object.fromEntries(candidate.packages.map(p=>[p.file,p.sha256])), `${gate}: artifact hashes mismatch`);
    if (gate.startsWith('interactive-native-')) assert.equal(r.actualNativeInteraction, true, `${gate}: simulated evidence prohibited`);
    if (gate === 'soak-two-hours' || gate === 'soak-thirty-minutes') {
      const minimumSeconds = gate === 'soak-two-hours' ? 7200 : 1800;
      assert(Number.isFinite(r.durationSeconds) && r.durationSeconds >= minimumSeconds, `${gate}: soak shorter than ${minimumSeconds} seconds or invalid duration`);
    }
    if (gate === 'performance-matched-baseline') { assert.equal(r.baselineCommit, '5afb8b8d'); assert.equal(r.matchedMachineAndWorkload, true); assert.equal(r.unresolvedRegressionsAbove20Percent, 0); }
    if (gate.startsWith('accessibility-')) for (const key of ['keyboard','screenReader','focusRestoration','ime','highContrast','displayScaling']) assert.equal(r[key], 'pass', `${gate}: ${key}`);
  }
  assert(receipts.every(r => policy.required.includes(r.gate)), 'Unexpected receipt or deferred check presented as a pass');
  if (policy.profile === 'full-v1') assert.notEqual(receipts.find(r=>r.gate==='pilot-1').validator, receipts.find(r=>r.gate==='pilot-2').validator, 'Pilots must be independent developers');
}

// Only a caller-controlled artifact reader may supply companion bytes.
function verifyMemoryTrendWaiver(candidate, receipt, context) {
  const waiver = receipt.waiver;
  assert.equal(waiver?.schema, 'runic.soak-known-issue/1');
  assert.deepEqual(waiver.policy, demoMemoryTrendWaiver);
  assert.equal(waiver.rawReceiptFile, 'native-soak.json');
  assert.equal(waiver.rawReceiptText, undefined, 'Raw evidence belongs in the companion artifact');
  assert.equal(typeof context.readCompanion, 'function', 'Trusted companion artifact context required');
  const bytes = context.readCompanion(waiver.rawReceiptFile);
  assert.ok(Buffer.isBuffer(bytes) && bytes.length > 0 && bytes.length <= 64*1024*1024);
  assert.equal(sha256(bytes), waiver.rawReceiptSha256);
  const raw = JSON.parse(new TextDecoder('utf-8', {fatal:true}).decode(bytes));
  assert.equal(raw.schema, 'runic.reliability-soak/1');
  assert.equal(raw.status, 'failed');
  assert.equal(raw.failure, 'Memory medians grow in every quarter', 'Only the sole memory-trend failure is waivable');
  assert.equal(raw.sourceRevision, candidate.source, 'Raw soak must match frozen source');
  assert.equal(raw.workload, 'window-reconnect-cancellation-v1');
  assert.match(raw.adapterSha256, /^[a-f0-9]{64}$/);
  assert.match(raw.provenanceSha256, /^[a-f0-9]{64}$/);
  assert.ok(raw.artifacts && Object.keys(raw.artifacts).length > 0);
  for (const hash of Object.values(raw.artifacts)) assert.match(hash, /^[a-f0-9]{64}$/);
  assert.deepEqual(receipt.nativeArtifactHashes, raw.artifacts, 'Native artifact binding differs from raw soak');
  assert.ok(raw.environment?.machine && raw.environment?.memoryMetric);
  assert.equal(raw.environment.platform, 'linux', 'Residual trend waiver is Linux-only');
  assert.deepEqual(raw.finalArtifactHashes, raw.artifacts, 'Missing or changed final native artifact hashes');
  const result = assessSoak(raw.samples, raw.elapsedMs, raw.requestedDurationMs ?? minimumSoakDurationMs);
  assert.equal(result.failureCode, demoMemoryTrendWaiver.failureCode);
  const delta = result.quarterMediansBytes[3] - result.quarterMediansBytes[0];
  assert.ok(delta <= demoMemoryTrendWaiver.maximumQuarterGrowthBytes && delta / result.quarterMediansBytes[0] <= demoMemoryTrendWaiver.maximumQuarterGrowthRatio, 'Residual growth exceeds the observed-small waiver bounds');
  assert.equal(receipt.durationSeconds, raw.elapsedMs / 1000);
  const shutdown = raw.shutdown;
  assert.ok(Number.isFinite(shutdown?.elapsedMs) && shutdown.elapsedMs >= 0 && shutdown.elapsedMs <= 15000);
  assert.ok(Array.isArray(shutdown.observations) && shutdown.observations.length > 0);
  for (const observation of shutdown.observations) {
    assert.ok(Number.isFinite(observation.elapsedMs) && observation.elapsedMs >= 0 && observation.elapsedMs <= 15000);
    assert.ok(Array.isArray(observation.processes));
  }
  assert.deepEqual(shutdown.observations.at(-1).processes, [], 'Helpers must exit naturally');
  assert.equal(shutdown.observations.at(-1).elapsedMs, shutdown.elapsedMs);
}

// The directory comes from CLI/workflow configuration, never from the envelope.
// Require a plain file under a symlink-free directory; never fetch receipt URLs.
export function companionContext(directory) {
  if (directory === undefined) return {};
  const root = resolve(directory);
  let current = parse(root).root;
  for (const part of relative(current, root).split(/[\\/]/).filter(Boolean)) {
    current = join(current, part);
    const stat = lstatSync(current);
    assert.ok(stat.isDirectory() && !stat.isSymbolicLink(), 'Companion directory must not traverse symlinks');
  }
  return {readCompanion(name) {
    assert.equal(name, 'native-soak.json');
    const path = join(root, name), stat = lstatSync(path);
    assert.ok(stat.isFile() && !stat.isSymbolicLink());
    const fd = openSync(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
    try {
      const opened = fstatSync(fd);
      assert.ok(opened.isFile() && opened.size > 0 && opened.size <= 64*1024*1024, 'Companion receipt exceeds 64 MiB bound');
      assert.equal(opened.ino, stat.ino);assert.equal(opened.dev, stat.dev);
      return readFileSync(fd);
    } finally {closeSync(fd);}
  }};
}
