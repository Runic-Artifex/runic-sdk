import assert from 'node:assert/strict';
import test from 'node:test';
import { CANDIDATES, MANIFEST, NPM_CANDIDATE, NPM_FEED, NUGET_FEED, RECEIPT_SCHEMA, REPEAT_RECEIPT_SCHEMA, requireCandidateFeed, verifyReceipt, verifyRepeatedReceipt } from './verify.mjs';

const authority = { path: 'runic.release.json', revision: 'a'.repeat(40), tree: 'b'.repeat(40), digest: 'c'.repeat(64), publicPackageCounts: { nuget: 1, npm: 1 }, before: { revision: 'a'.repeat(40), tree: 'b'.repeat(40), status: '' }, after: { revision: 'a'.repeat(40), tree: 'b'.repeat(40), status: '' } };
const phase = (name, argv) => ({ name, argv, status: 'passed', exitCode: 0, reasonCode: null });
function receipt() {
  return {
    schema: RECEIPT_SCHEMA,
    feeds: { nuget: NUGET_FEED, npm: NPM_FEED },
    isolation: { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet', npmCache: '.npm-cache' },
    releaseManifest: structuredClone(authority),
    bridgeManifest: structuredClone(MANIFEST),
    fixtures: ['initialize.client.json', 'resynchronized.host.json', 'late-old-admission-error.host.json', 'future-admission-error.host.json'].map((name) => ({ name, sha256: 'd'.repeat(64) })),
    nugetCandidates: CANDIDATES.map((candidate) => ({ ...candidate, source: NUGET_FEED, contentHash: 'sha512-fixture' })),
    npmCandidate: { ...NPM_CANDIDATE, source: NPM_FEED, integrity: 'sha512-fixture', archiveSha256: 'e'.repeat(64) },
    phases: [
      phase('generated-contract', ['node', '<authoritative-generator>', 'check', '--source', 'application.bridge.ts', '--ir', 'generated/bridge.ir.json', '--facade', 'generated/application.bridge.generated.ts']),
      phase('generated-fixtures', ['node', 'verify-fixtures.mjs']),
      phase('restore', ['dotnet', 'restore', 'HostConsumer.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']),
      phase('build', ['dotnet', 'build', 'HostConsumer.csproj', '--no-restore', '--configuration', 'Release', '--nologo']),
      phase('npm-install', ['npm', 'install', '--ignore-scripts']),
      phase('transport', ['node', 'client.mjs', '<local-host>', 'generated/bridge.ir.json', 'fixtures']),
      phase('controlled-teardown', ['POST', '<local-host>/shutdown']),
    ],
  };
}

test('host transport requires an explicit isolated candidate feed', () => {
  assert.throws(() => requireCandidateFeed(undefined), /RUNIC_CURRENT_HOST_TRANSPORT_NUGET_FEED/);
});

test('host transport receipt accepts an exact local package journey', () => {
  assert.deepEqual(verifyReceipt(receipt(), authority), { ok: true, errors: [] });
});

test('host transport receipt rejects forged provenance and incomplete transport evidence', () => {
  const value = receipt();
  value.nugetCandidates[0].source = 'https://api.nuget.org/v3/index.json';
  value.npmCandidate.archiveSha256 = 'forged';
  value.phases[5].status = 'failed';
  const report = verifyReceipt(value, authority);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /NuGet provenance mismatch/);
  assert.match(report.errors.join('\n'), /npm provenance mismatch/);
  assert.match(report.errors.join('\n'), /transport evidence malformed/);
});

test('host transport repeated receipts require byte-identical journeys', () => {
  const value = { schema: REPEAT_RECEIPT_SCHEMA, journeys: [receipt(), receipt()] };
  assert.deepEqual(verifyRepeatedReceipt(value, authority), { ok: true, errors: [] });
  value.journeys[1].bridgeManifest.wire.protocol.identity = 'forged';
  const report = verifyRepeatedReceipt(value, authority);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /bridge manifest mismatch/);
  assert.match(report.errors.join('\n'), /not deterministic/);
});
