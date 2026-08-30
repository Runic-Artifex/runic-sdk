import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import {
  mkdtemp,
  mkdir,
  readFile,
  rm,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { promisify } from 'node:util';
import test from 'node:test';

import { releaseData } from '../src/lib/generated/release-data.ts';
import {
  availabilityLabel,
  createReleaseDocs,
  packageInstallCommand,
  versionLabel,
} from '../src/lib/release-docs-core.ts';

const run = promisify(execFile);

test('generated release data is current and authority-validated', async () => {
  await run(process.execPath, ['scripts/generate-release-data.mjs', '--check']);
  assert.match(releaseData.source.authorityRevision, /^[0-9a-f]{40}$/);
  for (const hash of [
    releaseData.source.manifestSha256,
    releaseData.source.schemaSha256,
    releaseData.source.verifierSha256,
    releaseData.source.compatibilitySetSha256,
    releaseData.source.compatibilitySchemaSha256,
    releaseData.source.compatibilityVerifierSha256,
  ]) {
    assert.match(hash, /^[0-9a-f]{64}$/);
  }
  assert.deepEqual(releaseData.compatibilitySet.languageProfiles, {
    v1: [
      {
        language: 'csharp',
        role: 'application-backend',
        state: 'supported',
      },
      {
        language: 'typescript-effect',
        role: 'frontend',
        state: 'supported',
      },
    ],
    postV1: [
      {
        language: 'rust',
        role: 'native-and-backend',
        state: 'unassigned',
      },
      {
        language: 'cpp',
        role: 'native-and-backend',
        state: 'unassigned',
      },
    ],
  });
});

test('projects all authoritative compatibility lanes without certifying them', () => {
  const lanes = releaseData.compatibilityTrains.flatMap((train) => train.lanes);
  assert.deepEqual(lanes.map((lane) => lane.name).sort(), [
    'current',
    'next-candidate',
    'previous-supported',
  ]);
  const docs = createReleaseDocs(releaseData);
  assert.deepEqual(
    [...new Set(docs.compatibilityRows.map((row) => row.lane))].sort(),
    ['current', 'next-candidate', 'previous-supported'],
  );
  assert.ok(
    docs.compatibilityRows.every((row) => row.version.state === 'unassigned'),
  );
});

test('rejects authority sources that differ from the pinned commit', async () => {
  const fixture = await mkdtemp(join(tmpdir(), 'runic-release-authority-'));
  const authority = join(fixture, 'authority');
  const sourceManifest = resolve(
    process.env.RUNIC_RELEASE_MANIFEST ?? '../.github/runic.release.json',
  );
  const sourceAuthority = dirname(sourceManifest);
  const sources = [
    'runic.release.json',
    'runic.release.schema.json',
    'eng/verify-release-manifest.mjs',
    'runic.compatibility-set.json',
    'runic.compatibility-set.schema.json',
    'eng/verify-compatibility-set.mjs',
  ];
  try {
    await mkdir(join(authority, 'eng'), { recursive: true });
    const contents = new Map(
      await Promise.all(
        sources.map(async (path) => {
          const source = await readFile(join(sourceAuthority, path), 'utf8');
          await writeFile(join(authority, path), source);
          return [path, source];
        }),
      ),
    );
    await run('git', ['init', '--initial-branch=main'], { cwd: authority });
    await run('git', ['config', 'user.email', 'tests@runic-artifex.invalid'], {
      cwd: authority,
    });
    await run('git', ['config', 'user.name', 'Runic tests'], {
      cwd: authority,
    });
    await run('git', ['add', '.'], { cwd: authority });
    await run('git', ['commit', '--quiet', '-m', 'authority fixture'], {
      cwd: authority,
    });
    const { stdout: revision } = await run('git', ['rev-parse', 'HEAD'], {
      cwd: authority,
    });
    const generate = (authorityRevision) =>
      run(
        process.execPath,
        [
          'scripts/generate-release-data.mjs',
          '--check',
          '--manifest',
          join(authority, 'runic.release.json'),
          '--authority-revision',
          authorityRevision,
          '--output',
          join(fixture, 'release-data.ts'),
        ],
        { cwd: process.cwd() },
      );
    for (const path of sources) {
      await writeFile(join(authority, path), `${contents.get(path)}\n`);
      await assert.rejects(
        generate(revision.trim()),
        /release authority source does not match/,
      );
      await writeFile(join(authority, path), contents.get(path));
    }
    const linkedAuthority = join(fixture, 'linked-authority');
    await symlink(authority, linkedAuthority, 'dir');
    await run(
      process.execPath,
      [
        'scripts/generate-release-data.mjs',
        '--manifest',
        join(linkedAuthority, 'runic.release.json'),
        '--authority-revision',
        revision.trim(),
        '--output',
        join(fixture, 'linked-release-data.ts'),
      ],
      { cwd: process.cwd() },
    );
    const { stdout: tree } = await run('git', ['rev-parse', 'HEAD^{tree}'], {
      cwd: authority,
    });
    await assert.rejects(
      generate(tree.trim()),
      /release authority revision must resolve to a Git commit/,
    );
  } finally {
    await rm(fixture, { recursive: true, force: true });
  }
});

test('projects canonical packages, archive migrations, and independent distribution versions', () => {
  const unassigned = { state: 'unassigned', value: null };
  const published = { state: 'published', value: '2.0.0' };
  const fixture = {
    products: [
      { id: 'assets', stableOwner: 'Runic Assets', support: 'supported' },
      {
        id: 'flow',
        stableOwner: 'Runic Flow',
        support: 'archived',
        archive: {
          status: 'archived',
          evidence: {
            repository: 'flow',
            revision: 'a'.repeat(40),
            path: 'docs/adr/archive.md',
          },
        },
      },
      { id: 'editor', stableOwner: 'Editor', support: 'supported' },
    ],
    canonicalPackages: [
      {
        identity: 'Runic.Assets',
        ecosystem: 'nuget',
        product: 'assets',
        state: 'approved',
        installKind: 'nuget-package',
      },
      {
        identity: 'Runic.Assets.Testing',
        ecosystem: 'nuget',
        product: 'assets',
        state: 'approved',
        installKind: 'nuget-package',
      },
    ],
    currentPackages: [
      {
        identity: 'RunicAssets.RunicToolkit',
        ecosystem: 'nuget',
        product: 'assets',
        stableOwner: 'Runic Assets',
        support: 'supported',
        disposition: 'retire',
        target: null,
        migration: {
          kind: 'package',
          target: 'Runic.Assets',
          guidance: 'No forwarding package.',
        },
      },
      {
        identity: 'RunicFlow',
        ecosystem: 'nuget',
        product: 'flow',
        stableOwner: 'Runic Flow',
        support: 'archived',
        disposition: 'archive',
        target: null,
        migration: {
          kind: 'remove',
          target: null,
          guidance:
            'Archived migration source only; remove without replacement.',
        },
      },
    ],
    compatibilityTrains: [
      {
        id: 'v0.2',
        lanes: [
          {
            name: 'current',
            versions: [{ product: 'assets', version: unassigned }],
          },
        ],
      },
    ],
    distributions: [
      {
        product: 'editor',
        identity: 'RunicTranslations.Editor',
        kind: 'application-archive',
        version: published,
      },
    ],
  };
  const docs = createReleaseDocs(fixture);

  assert.deepEqual(
    docs.catalogRows.map((row) => row.name),
    ['Runic.Assets', 'Runic.Assets.Testing'],
  );
  assert.equal(docs.migrationRows.length, 2);
  assert.equal(docs.migrationRows[0].disposition, 'retire');
  assert.equal(docs.migrationRows[0].migrationKind, 'package');
  assert.equal(docs.migrationRows[0].migrationTarget, 'Runic.Assets');
  assert.equal(versionLabel(docs.catalogRows[0].version), 'Version unassigned');
  assert.equal(docs.activeLaneForProduct('flow'), undefined);
  assert.equal(docs.activeVersionForProduct('flow'), undefined);
  assert.equal(packageInstallCommand(docs.catalogRows[0]), undefined);
  assert.equal(
    availabilityLabel(docs.distributionRows[0].version),
    'Published',
  );
  assert.match(docs.releaseSummary, /has not assigned versions/);

  const pendingDocs = createReleaseDocs({
    ...fixture,
    compatibilityTrains: [
      {
        ...fixture.compatibilityTrains[0],
        lanes: [
          {
            name: 'current',
            versions: [{ product: 'assets', version: published }],
          },
        ],
      },
    ],
  });
  assert.match(
    pendingDocs.releaseSummary,
    /active compatibility-lane versions/,
  );
});
