import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { promisify } from 'node:util';
import test from 'node:test';
import { releaseData } from '../src/lib/generated/release-data.ts';
import {
  createReleaseDocs,
  packageInstallCommand,
} from '../src/lib/release-docs-core.ts';
const run = promisify(execFile);

test('generated catalog matches every workspace package and remains unpublished', async () => {
  await run(process.execPath, ['scripts/generate-release-data.mjs', '--check']);
  const workspace = JSON.parse(
    await readFile(
      new URL('../../eng/workspace.json', import.meta.url),
      'utf8',
    ),
  );
  const candidate = releaseData.currentCandidate;
  assert.equal(candidate.version, workspace.version);
  assert.equal(candidate.publication, 'unpublished');
  assert.match(candidate.workspaceSha256, /^[a-f0-9]{64}$/);
  assert.deepEqual(
    candidate.packages.map((p) => p.identity),
    [...workspace.nuget, ...workspace.npm].map((p) => p.name),
  );
  assert.equal(
    new Set(candidate.packages.map((p) => p.identity)).size,
    candidate.packages.length,
  );
  assert(candidate.packages.some((p) => p.identity === 'Runic.Platform.MacOS'));
  assert(
    candidate.packages.some((p) => p.identity === 'Runic.Application.CsWebUi'),
  );
  assert(candidate.packages.every((p) => !p.identity.includes('Editor')));
  for (const entry of createReleaseDocs(releaseData).catalogRows)
    assert.equal(packageInstallCommand(entry), undefined);
});

test('public commands require published state, not just a candidate version', () => {
  const entry = {
    name: 'Runic.Application',
    installKind: 'nuget-package',
    version: { state: 'unpublished', value: '0.2.0-preview.1' },
  };
  assert.equal(packageInstallCommand(entry), undefined);
  assert.equal(
    packageInstallCommand({
      ...entry,
      version: { ...entry.version, state: 'published' },
    }),
    'dotnet add package Runic.Application --version 0.2.0-preview.1',
  );
});
