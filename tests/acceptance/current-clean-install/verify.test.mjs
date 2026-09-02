import assert from 'node:assert/strict';
import test from 'node:test';
import { RECEIPT_SCHEMA, REPEAT_RECEIPT_SCHEMA, REQUIRED_NPM_IDENTITIES, compatibilityFacts, verifyReceipt, verifyRepeatedReceipt } from './verify.mjs';

const authority = {
  schemaVersion: 1,
  id: 'runic-1.0-preview.1',
  releaseTrainVersion: '1.0.0-preview.1',
  publication: 'forbidden',
  toolchain: { dotnetSdk: '10.0.302', node: '24.18.0', npm: '11.16.0' },
  packages: [
    ...['Runic.Application.Templates', 'dotnet-runic', 'Runic.Application', 'Runic.Application.Bridge', 'Runic.Application.Desktop', 'Runic.Assets', 'Runic.Assets.Desktop', 'Runic.Desktop'].map((identity) => ({ ecosystem: 'nuget', identity, version: '1.0.0-preview.1' })),
    ...REQUIRED_NPM_IDENTITIES.map((identity) => ({ ecosystem: 'npm', identity, version: '1.0.0-preview.1' })),
  ],
};
const facts = compatibilityFacts(authority, Buffer.from(JSON.stringify(authority)));
const receipt = () => ({
  schema: RECEIPT_SCHEMA,
  compatibilitySet: structuredClone(facts),
  feeds: { nuget: 'explicit-local-directory', npm: 'explicit-runic-registry', githubPackages: 'prohibited' },
  isolation: { dotnetCliHome: '.dotnet', nugetPackages: '.nuget/packages', nugetHttpCache: '.nuget/http-cache', npmCache: '.npm-cache' },
  template: { shortName: 'runic-app-svelte', framework: 'svelte' },
  phases: ['template-install', 'tool-install', 'create', 'npm-install', 'frontend-typecheck', 'frontend-build', 'restore', 'build', 'doctor', 'inspect', 'develop', 'package', 'run', 'restart'].map((name) => ({ name, status: 'passed', exitCode: 0, reasonCode: null })),
  nugetPackages: Object.fromEntries(['Runic.Application', 'Runic.Application.Bridge', 'Runic.Application.Desktop', 'Runic.Assets', 'Runic.Assets.Desktop', 'Runic.Desktop'].map((identity) => [identity, { version: '1.0.0-preview.1', contentHash: 'sha512-fixture' }])),
  npmPackages: Object.fromEntries(REQUIRED_NPM_IDENTITIES.map((identity) => [identity, { version: '1.0.0-preview.1', integrity: 'sha512-YQ==', registry: 'http://127.0.0.1:4873' }])),
});

test('the clean-room journey accepts an exact compatibility-set package path', () => {
  assert.deepEqual(verifyReceipt(receipt(), facts), { ok: true, errors: [] });
});

test('the clean-room journey rejects a changed authority version and a softened journey', () => {
  const value = receipt();
  value.compatibilitySet.releaseTrainVersion = '1.0.0-preview.2';
  value.phases[8].status = 'failed';
  const report = verifyReceipt(value, facts);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /compatibility authority mismatch/);
  assert.match(report.errors.join('\n'), /golden-path phase evidence mismatch/);
});

test('the clean-room journey requires two byte-equivalent runs', () => {
  const value = { schema: REPEAT_RECEIPT_SCHEMA, journeys: [receipt(), receipt()] };
  assert.deepEqual(verifyRepeatedReceipt(value, facts), { ok: true, errors: [] });
  value.journeys[1].npmPackages['@runic-artifex/desktop'].registry = 'https://example.test';
  const report = verifyRepeatedReceipt(value, facts);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /clean journeys are not repeatable/);
});

test('the compatibility authority fails closed when a selected package is missing', () => {
  const invalid = structuredClone(authority);
  invalid.packages = invalid.packages.filter((item) => item.identity !== 'Runic.Desktop');
  assert.throws(() => compatibilityFacts(invalid, Buffer.from(JSON.stringify(invalid))), /Runic\.Desktop/);
});
