import assert from 'node:assert/strict';
import test from 'node:test';
import { verifyReceipt } from './verify.mjs';

const journey = () => ({
  schema: 'runic.support-certification/1',
  isolation: { inputs: 'isolated-copy', diagnosticsAuthority: 'runic.translations.editor-diagnostics/1' },
  freezeReceipt: { sha256: 'a'.repeat(64), schema: 'runic.controlled-nonpublic-profile-freeze-repeat/1' },
  frozenProfile: { sha256: 'b'.repeat(64), profiles: {} }, evidence: {},
  privacy: { optIn: true, excluded: ['source', 'translation', 'review', 'session', 'cookie', 'token'], outboundTransportAttempts: 0, upload: 'forbidden', telemetry: 'forbidden' },
  recovery: { completeRollback: true, mutationBeforeRecovery: 'rejected', staleBridgeState: 'rejected', diagnostics: 'sanitized-counts' },
  quality: { bridge: { returnedFrames: 'exact', schemaValidatedDelivery: 'exact', fixedBatches: [1, 256, 1024] }, model: { messages: 50_000, reviewLocales: 100, retainedHeapMiBMaximum: 256, timing: 'observation-only' }, keyboardAccessibility: { commandSearch: 'focused', recoveryFocusOrder: ['rollback', 'complete'], labelsAndLandmarks: 'checked', forcedColors: 'checked' }, timing: 'observation-only-not-sla' }, externalActions: { requests: 0, uploads: 0, telemetry: 0, dashboard: 0 },
});

test('accepts deterministic frozen support certification', () => {
  const value = journey();
  assert.deepEqual(verifyReceipt({ schema: 'runic.support-certification-repeat/1', journeys: [value, structuredClone(value)] }, value), { ok: true, errors: [] });
});

test('rejects softened privacy, recovery, keyboard, and accessibility evidence', () => {
  const first = journey(), second = journey();
  second.privacy.excluded.pop(); second.recovery.mutationBeforeRecovery = 'allowed'; second.quality.timing = 'SLA: 1ms'; second.quality.keyboardAccessibility.labelsAndLandmarks = 'unchecked';
  const report = verifyReceipt({ schema: 'runic.support-certification-repeat/1', journeys: [first, second] }, first);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /two deterministic|support-certification boundary/);
});
