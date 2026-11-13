import { test } from 'node:test';
import assert from 'node:assert/strict';
import { trainVersions, trainFeedSource, activeTrainFeed } from './train-feed.mjs';

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
