import assert from 'node:assert/strict';
import test from 'node:test';
import { verifyReceipt } from './verify.mjs';

const journey = () => ({
  schema: 'runic.local-operator-rehearsal/1', publication: 'forbidden',
  isolation: { evidenceInputs: 'isolated-copy', packageCaches: 'fresh-per-workflow', transport: 'none' },
  retainedProfiles: { 'csharp-host': {}, 'local-application-bridge': {}, 'editor-desktop': {}, 'd008-hosted-product': {} },
  evidence: {
    audit: { sha256: 'a'.repeat(64) }, documentation: { sha256: 'b'.repeat(64) },
    directTool: { package: { metadata: { toolCommandName: 'dotnet-runic', dependencies: [] } }, replay: { sdk: '10.0.302', command: 'dotnet-runic', isolation: { packageSources: 'exact-local-only', dotnetCliHome: '.dotnet', nugetPackages: '.nuget/packages', nugetHttpCache: '.nuget/http' } } },
    support: { artifactRole: 'support-envelope-only', replay: { matchedFrozenReceipt: true, sdk: '10.0.302' } },
    recovery: { packages: [{ identity: 'Runic.Application' }, { identity: 'Runic.Application.Testing' }, { identity: 'Runic.Assets' }], replay: { matchedFrozenReceipt: true, sdk: '10.0.302' } },
  },
  migration: { outcome: 'manual-replacement-eligible', guidance: 'user-performed-verified-manual-replacement', directToolCommand: 'dotnet-runic' },
  support: { preview: 'lists-selection-and-omissions', collect: 'byte-identical', remove: 'verified', hostileRejections: ['workspace-root', 'relative-path', 'token', 'source-text', 'translation-text', 'review-text'] },
  recovery: { completeRollback: true, mutationBeforeRecovery: 'rejected', staleBridgeState: 'rejected', diagnostics: 'sanitized-counts' },
  privacy: { optIn: true, excluded: ['source-content', 'translation-content', 'review-content', 'session-content', 'cookie-content', 'token-content'], outboundTransportAttempts: 0, upload: 'forbidden', telemetry: 'forbidden' },
  externalActions: { requests: 0, updates: 0, installs: 0, deletes: 0, rollbacks: 0, repairs: 0, uploads: 0, signatures: 0, tags: 0, releases: 0 },
});

test('accepts the deterministic local operator rehearsal', () => {
  const value = journey();
  assert.deepEqual(verifyReceipt({ schema: 'runic.local-operator-rehearsal-repeat/1', journeys: [value, structuredClone(value)] }, value), { ok: true, errors: [] });
});

test('rejects automatic migration, remote action, privacy leakage, and unsafe recovery', () => {
  const first = journey(), second = journey();
  second.migration.guidance = 'automatic-update';
  second.externalActions.updates = 1;
  second.privacy.excluded.pop();
  second.recovery.staleBridgeState = 'resumed';
  second.evidence.directTool.package.metadata.dependencies = ['remote'];
  const report = verifyReceipt({ schema: 'runic.local-operator-rehearsal-repeat/1', journeys: [first, second] }, first);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /two deterministic|operator boundary|provenance or privacy/);
});
