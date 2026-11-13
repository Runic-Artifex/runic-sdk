import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { trainVersions, trainFeedSource, activeTrainFeed, trainFeedNpm, trainFeedNpmSpec } from './train-feed.mjs';

test('the train versions are the central versions of Runic.CommandLine and Runic.Translations', () => {
  const props = `<Project><ItemGroup>
    <PackageVersion Include="Runic.Translations" Version="0.10.0" />
    <PackageVersion Include="Runic.Translations.Hosting" Version="0.10.0" />
    <PackageVersion Include="Runic.CommandLine" Version="0.12.0" />
    <PackageVersion Include="Runic.CommandLine.Spectre" Version="0.12.0" />
  </ItemGroup></Project>`;
  assert.deepEqual(trainVersions(props), { cli: '0.12.0', translations: '0.10.0' });
  assert.throws(() => trainVersions('<Project />'), /no PackageVersion for Runic.CommandLine/);
  // The repository's own pins parse.
  assert.match(trainVersions().cli, /^\d+\.\d+\.\d+/);
});

test('consumers add the feed with a mapping for the upstream package ids only while it is active', () => {
  assert.equal(activeTrainFeed({}), null);
  assert.deepEqual(trainFeedSource({}), { source: '', mapping: '' });
  const { source, mapping } = trainFeedSource({ RUNIC_TRAIN_FEED: '/feeds/a&b' });
  assert.equal(source, '<add key="runic-train-feed" value="/feeds/a&amp;b"/>');
  for (const pattern of ['Runic.CommandLine', 'Runic.CommandLine.*', 'Runic.Translations', 'Runic.Translations.*', 'dotnet-runic-translations'])
    assert.ok(mapping.includes(`<package pattern="${pattern}"/>`), pattern);
  assert.ok(!mapping.includes('pattern="Runic.*"'));
});

test('npm consumers install a candidate tarball of the feed only for its exact version', () => {
  const output = mkdtempSync(join(tmpdir(), 'train-feed-test-'));
  try {
    mkdirSync(join(output, 'nuget'));
    writeFileSync(join(output, 'train-feed.json'), JSON.stringify({ upstreams: { translations: { npm: [
      { name: '@runic-artifex/vite-plugin-runic-translations', version: '0.10.0', file: 'runic-artifex-vite-plugin-runic-translations-0.10.0.tgz' }] } } }));
    const environment = { RUNIC_TRAIN_FEED: join(output, 'nuget') };
    assert.deepEqual(Object.keys(trainFeedNpm(environment)), ['@runic-artifex/vite-plugin-runic-translations']);
    assert.equal(trainFeedNpmSpec('@runic-artifex/vite-plugin-runic-translations', '0.10.0', environment),
      `file:${join(output, 'npm', 'runic-artifex-vite-plugin-runic-translations-0.10.0.tgz')}`);
    assert.equal(trainFeedNpmSpec('@runic-artifex/vite-plugin-runic-translations', '0.9.1', environment), '0.9.1');
    assert.equal(trainFeedNpmSpec('@runic-artifex/vite-plugin-runic-translations', '0.10.0', {}), '0.10.0');
  } finally {
    rmSync(output, { recursive: true, force: true });
  }
});
