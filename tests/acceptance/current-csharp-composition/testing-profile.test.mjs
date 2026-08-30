import assert from 'node:assert/strict';
import test from 'node:test';
import { CANDIDATES, FEED, INPUTS, MANIFEST, RECEIPT_SCHEMA, REPEAT_RECEIPT_SCHEMA, requireCandidateFeed, verifyReceipt, verifyRepeatedReceipt } from './testing-profile.mjs';

const authority = { path: 'runic.release.json', revision: 'a'.repeat(40), tree: 'b'.repeat(40), digest: 'c'.repeat(64), publicPackageCounts: { nuget: 1, npm: 1 }, before: { revision: 'a'.repeat(40), tree: 'b'.repeat(40), status: '' }, after: { revision: 'a'.repeat(40), tree: 'b'.repeat(40), status: '' } };
const phase = (name, argv) => ({ name, argv, status: 'passed', exitCode: 0, reasonCode: null });
const receipt = () => ({
  schema: RECEIPT_SCHEMA,
  feed: FEED,
  isolation: { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet' },
  releaseManifest: structuredClone(authority),
  projectReferences: [],
  manifest: structuredClone(MANIFEST),
  inputs: structuredClone(INPUTS),
  candidates: CANDIDATES.map((candidate) => ({ ...candidate, source: FEED, contentHash: 'fixture' })),
  phases: [
    phase('restore', ['dotnet', 'restore', 'TestingProfile.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']),
    phase('build', ['dotnet', 'build', 'TestingProfile.csproj', '--no-restore', '--configuration', 'Release', '--nologo']),
    phase('test', ['dotnet', 'run', '--project', 'TestingProfile.csproj', '--no-build', '--configuration', 'Release']),
  ],
});

test('testing profile requires an explicit isolated candidate feed', () => {
  assert.throws(() => requireCandidateFeed(undefined), /RUNIC_CURRENT_CSHARP_TESTING_CANDIDATE_FEED/);
});

test('testing profile receipt accepts the package-only deterministic contract', () => {
  assert.deepEqual(verifyReceipt(receipt(), authority), { ok: true, errors: [] });
});

test('testing profile receipt rejects source projects, forged inputs, and non-isolated candidates', () => {
  const value = receipt();
  value.projectReferences.push('../runic-toolkit/src/Runic.Application.Testing/Runic.Application.Testing.csproj');
  value.inputs.idSeed = 0;
  value.candidates[1].source = 'https://api.nuget.org/v3/index.json';
  const report = verifyReceipt(value, authority);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /product-source project references/);
  assert.match(report.errors.join('\n'), /deterministic input mismatch/);
  assert.match(report.errors.join('\n'), /candidate metadata mismatch/);
});

test('testing profile receipt requires two identical journeys', () => {
  const value = { schema: REPEAT_RECEIPT_SCHEMA, journeys: [receipt(), receipt()] };
  assert.deepEqual(verifyRepeatedReceipt(value, authority), { ok: true, errors: [] });
  value.journeys[1].phases[2].exitCode = 1;
  const report = verifyRepeatedReceipt(value, authority);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /test evidence malformed/);
  assert.match(report.errors.join('\n'), /testing journeys are not deterministic/);
});
