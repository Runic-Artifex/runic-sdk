import assert from 'node:assert/strict';
import test from 'node:test';
import { APPLICATION_ARGUMENTS, CANDIDATES, FEED, MANIFEST, RECEIPT_SCHEMA, REPEAT_RECEIPT_SCHEMA, TOOL_CANDIDATE, requireCandidateFeed, verifyReceipt, verifyRepeatedReceipt } from './verify.mjs';

const authority = { path: 'runic.release.json', revision: 'a'.repeat(40), tree: 'b'.repeat(40), digest: 'c'.repeat(64), publicPackageCounts: { nuget: 1, npm: 1 }, before: { revision: 'a'.repeat(40), tree: 'b'.repeat(40), status: '' }, after: { revision: 'a'.repeat(40), tree: 'b'.repeat(40), status: '' } };
const phase = (name, argv, failed = false) => ({ name, argv, status: failed ? 'failed' : 'passed', exitCode: failed ? 1 : 0, reasonCode: failed ? 'command-exit-nonzero' : null });
const receipt = () => ({ schema: RECEIPT_SCHEMA, feed: FEED, isolation: { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet' }, releaseManifest: structuredClone(authority), projectReferences: [], manifest: structuredClone(MANIFEST), candidates: CANDIDATES.map((candidate) => ({ ...candidate, source: FEED, contentHash: 'fixture' })), tool: { ...TOOL_CANDIDATE, source: FEED, contentHash: 'a'.repeat(64) }, phases: [
  phase('restore', ['dotnet', 'restore', 'CurrentComposition.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']),
  phase('build', ['dotnet', 'build', 'CurrentComposition.csproj', '--no-restore', '--configuration', 'Release', '--nologo']),
  phase('run', ['dotnet', 'run', '--project', 'CurrentComposition.csproj', '--no-build', '--configuration', 'Release']),
  phase('negative-restore', ['dotnet', 'restore', 'negative/Negative.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']),
  phase('tool-install', ['dotnet', 'tool', 'install', 'dotnet-runic', '--tool-path', '../.tools', '--version', TOOL_CANDIDATE.version, '--configfile', '../NuGet.config', '--no-cache']),
  phase('doctor-healthy', ['dotnet-runic', 'doctor', '--project', 'CurrentComposition.csproj']),
  phase('doctor-absent-contract-output', ['dotnet-runic', 'doctor', '--project', 'CurrentComposition.csproj'], true),
  phase('doctor-stale-contract-output', ['dotnet-runic', 'doctor', '--project', 'CurrentComposition.csproj'], true),
  phase('dev-dry-run', ['dotnet-runic', 'dev', '--project', 'CurrentComposition.csproj', '--dry-run', '--', ...APPLICATION_ARGUMENTS]),
  phase('inspect-first', ['dotnet-runic', 'inspect', '--project', 'CurrentComposition.csproj', '--configuration', 'Release']),
  phase('inspect-second', ['dotnet-runic', 'inspect', '--project', 'CurrentComposition.csproj', '--configuration', 'Release']),
], development: { configuredCommands: [['npm', 'run', 'build'], ['npm', 'run', 'dev']], applicationArguments: APPLICATION_ARGUMENTS }, inspectManifest: structuredClone(MANIFEST), negativeDeclarations: [{ kind: 'missing', diagnostic: 'RAPP0000', exitCode: 1 }, { kind: 'duplicate', diagnostic: 'RAPP0002', exitCode: 1 }, { kind: 'invalid', diagnostic: 'RAPP0002', exitCode: 1 }] });

test('current C# composition requires an explicit isolated candidate feed', () => {
  assert.throws(() => requireCandidateFeed(undefined), /RUNIC_CURRENT_CSHARP_CANDIDATE_FEED/);
});

test('current C# composition receipt accepts the exact package-only fixture', () => {
  assert.deepEqual(verifyReceipt(receipt(), authority), { ok: true, errors: [] });
});

test('current C# composition receipt rejects source projects, forged candidates, changed authority, and a forged tool', () => {
  const value = receipt();
  value.projectReferences.push('../runic-toolkit/Runic.Application.csproj');
  value.candidates[0].source = 'https://api.nuget.org/v3/index.json';
  value.tool.version = '0.0.0-forged';
  value.releaseManifest.revision = '0'.repeat(40);
  const report = verifyReceipt(value, authority);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /product-source project references/);
  assert.match(report.errors.join('\n'), /candidate metadata mismatch/);
  assert.match(report.errors.join('\n'), /tool candidate metadata mismatch/);
  assert.match(report.errors.join('\n'), /release authority identity mismatch/);
});

test('current C# composition receipt requires all declaration failures and deterministic journeys', () => {
  const value = { schema: REPEAT_RECEIPT_SCHEMA, journeys: [receipt(), receipt()] };
  assert.deepEqual(verifyRepeatedReceipt(value, authority), { ok: true, errors: [] });
  value.journeys[1].negativeDeclarations[2].exitCode = 0;
  const report = verifyRepeatedReceipt(value, authority);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /invalid declaration did not fail closed/);
  assert.match(report.errors.join('\n'), /composition journeys are not deterministic/);
});

test('current C# composition receipt requires fail-closed doctor evidence and exact dev arguments', () => {
  const value = receipt();
  value.phases[6].status = 'passed';
  value.development.applicationArguments = ['--rewritten'];
  const report = verifyReceipt(value, authority);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /doctor-absent-contract-output evidence malformed/);
  assert.match(report.errors.join('\n'), /development plan mismatch/);
});
