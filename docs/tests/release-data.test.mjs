import assert from 'node:assert/strict';
import test from 'node:test';
import publishedRelease from '../src/lib/published-release.json' with { type: 'json' };
import {
  createReleaseDocs,
  packageInstallCommand,
} from '../src/lib/release-docs-core.ts';

test('published catalog has unique installable packages and matching registry links', () => {
  const rows = createReleaseDocs(publishedRelease).catalogRows;
  assert.equal(new Set(rows.map((row) => row.name)).size, rows.length);
  for (const row of rows) {
    assert.ok(packageInstallCommand(row), row.name);
    assert.ok(row.registryUrl.endsWith(`/${publishedRelease.version}`));
  }
  assert.ok(rows.some((row) => row.name === 'Runic.Application.Templates'));
  assert.ok(
    rows.some((row) => row.name === '@runic-artifex/application-bridge'),
  );
  assert.ok(
    rows.every(
      (row) =>
        !row.name.includes('Editor') &&
        row.name !== 'Runic.Application.Bridge.Generators',
    ),
  );
});

test('commands cover libraries, templates, tools and npm packages', () => {
  const version = { state: 'published', value: '1.2.3-preview.4' };
  for (const [name, installKind, expected] of [
    [
      'Runic.Application',
      'nuget-package',
      'dotnet add package Runic.Application --version 1.2.3-preview.4',
    ],
    [
      'Runic.Application.Templates',
      'dotnet-template',
      'dotnet new install Runic.Application.Templates::1.2.3-preview.4',
    ],
    [
      'dotnet-runic',
      'dotnet-tool',
      'dotnet tool install --local dotnet-runic --version 1.2.3-preview.4',
    ],
    [
      '@runic-artifex/desktop',
      'npm-package',
      'npm install --save-exact @runic-artifex/desktop@1.2.3-preview.4',
    ],
  ])
    assert.equal(
      packageInstallCommand({ name, installKind, version }),
      expected,
    );
  assert.equal(
    packageInstallCommand({
      name: 'Future',
      installKind: 'nuget-package',
      version: { state: 'unpublished', value: '2.0.0' },
    }),
    undefined,
  );
});

test('catalog uses its published snapshot even when development packages change', () => {
  const release = {
    version: '1.0.0',
    url: 'https://example.com/release',
    packages: [
      {
        identity: 'Released',
        ecosystem: 'nuget',
        product: 'application',
        installKind: 'nuget-package',
      },
    ],
  };
  const docs = createReleaseDocs(release);
  assert.equal(docs.catalogRows.length, 1);
  assert.equal(docs.activeVersionForProduct('application').value, '1.0.0');
  assert.equal(docs.activeVersionForProduct('future'), undefined);
});
