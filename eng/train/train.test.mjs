import { expect, test } from 'bun:test';
import { readFileSync } from 'node:fs';
import { compareVersions, lineUpperBound, parseRange, rangeContains, sameLine } from './versions.mjs';
import { nugetConfig, parseNuspec, repoOf } from './nuget.mjs';
import { parseSpec, versionForRelease } from './sources.mjs';
import { keyCoverage, keysFromJson, keysFromMarkdown, lintLockstep, lintRanges, lintTemplatePins, lintToolPins, rmf2MessageNames } from './checks.mjs';
import { activeEntries, classify, loadExpected } from './expected.mjs';
import { errorLines, scenarios } from './scenarios.mjs';
import { engineeringOnly } from '../run.mjs';

const workflow = Bun.YAML.parse(readFileSync(new URL('../../.github/workflows/train-compat.yml', import.meta.url), 'utf8'));

test('versions compare by NuGet SemVer 2.0 and lines end at the next minor before 1.0, the next major after', () => {
  expect(compareVersions('0.10.0', '0.9.1')).toBe(1);
  expect(compareVersions('0.10.0-0.0', '0.10.0-rc.1')).toBe(-1);
  expect(compareVersions('0.17.1-preview.1', '0.17.1')).toBe(-1);
  expect(compareVersions('1.0.0-rc.2', '1.0.0-rc.10')).toBe(-1);
  expect(lineUpperBound('0.12.0')).toBe('0.13.0-0.0');
  expect(lineUpperBound('1.0.0-rc.1')).toBe('2.0.0-0.0');
  expect(sameLine('0.12.0', '0.12.3')).toBe(true);
  expect(sameLine('0.9.1', '0.11.0')).toBe(false);
  expect(parseRange('0.9.1')).toMatchObject({ min: '0.9.1', minInclusive: true, max: null });
  expect(parseRange('[0.17.1]')).toMatchObject({ min: '0.17.1', max: '0.17.1', maxInclusive: true });
  expect(rangeContains('[0.9.1, 0.10.0)', '0.11.0')).toBe(false);
  expect(rangeContains('[0.12.0, 0.13.0-0.0)', '0.12.4')).toBe(true);
  expect(rangeContains('[0.12.0, 0.13.0-0.0)', '0.13.0-rc.1')).toBe(false);
});

test('package ids map to their repository by the longest source-mapping pattern', () => {
  expect(repoOf('Runic.CommandLine.Spectre')).toBe('cli');
  expect(repoOf('Runic.Translations.Wpf')).toBe('translations');
  expect(repoOf('dotnet-runic-translations')).toBe('translations');
  expect(repoOf('dotnet-runic')).toBe('sdk');
  expect(repoOf('Runic.Application.Views.Translations')).toBe('sdk');
  expect(repoOf('CsWebUi')).toBe(null);
  const config = nugetConfig({ cli: '/feeds/cli', translations: null, sdk: '/feeds/sdk' });
  expect(config).toContain('<add key="train-cli" value="/feeds/cli" />');
  expect(config).not.toContain('train-translations');
  // Translations is published: its patterns map to nuget.org explicitly, so the SDK feed's Runic.* cannot claim them.
  expect(config).toMatch(/<packageSource key="nuget.org">\s*<package pattern="\*" \/>\s*<package pattern="Runic.Translations" \/>/);
  expect(config).toMatch(/<packageSource key="train-sdk">\s*<package pattern="Runic.\*" \/>/);
});

test('inputs are published[:version] or run[:id], and a release includes its own prerelease line', () => {
  expect(parseSpec('published')).toEqual({ kind: 'published', argument: null, text: 'published' });
  expect(parseSpec('published:0.12.0').argument).toBe('0.12.0');
  expect(parseSpec('run:43302549984')).toMatchObject({ kind: 'run', argument: '43302549984' });
  for (const bad of ['main', 'run:abc', 'published:x', '']) expect(() => parseSpec(bad)).toThrow();
  expect(versionForRelease(['0.16.0-preview.1', '0.17.1-preview.1', '0.17.1-preview.2'], '0.17.1')).toBe('0.17.1-preview.2');
  expect(versionForRelease(['0.17.0', '0.17.1'], '0.17.1')).toBe('0.17.1');
  expect(versionForRelease(['0.7.0-preview.6'], '0.17.1')).toBe(null);
});

test('nuspec dependencies keep their framework group and range', () => {
  const nuspec = parseNuspec(`<?xml version="1.0"?><package><metadata><id>Runic.Translations.CommandLine</id><version>0.9.1</version>
    <dependencies><group targetFramework="net10.0"><dependency id="Runic.CommandLine" version="[0.9.1, 0.10.0)" exclude="Build" />
    <dependency id="Runic.Translations" version="[0.9.1, 0.10.0-0.0)" /></group></dependencies></metadata></package>`);
  expect(nuspec).toEqual({ id: 'Runic.Translations.CommandLine', version: '0.9.1', dependencies: [
    { id: 'Runic.CommandLine', range: '[0.9.1, 0.10.0)', framework: 'net10.0' },
    { id: 'Runic.Translations', range: '[0.9.1, 0.10.0-0.0)', framework: 'net10.0' }] });
});

test('key coverage reads text-keys.json or the Markdown table and the packed German catalog', () => {
  const messages = rmf2MessageNames('help {\n  usage = Aufruf\n  options = Optionen\n}\nhints {\n  more = Mehr\n}\ndiagnostics {\n  promptRequired = Pflicht\n}\nfaults {\n  RCLI4001 = x\n}\n');
  expect([...messages].sort()).toEqual(['diagnostics.promptRequired', 'faults.RCLI4001', 'help.options', 'help.usage', 'hints.more']);
  const markdown = keysFromMarkdown('| Key | Arguments |\n| --- | --- |\n| `help.usage` | |\n| `help.options` | |\n| `hints.usage` | name |\n| `diagnostics.prompt-required` | |\n| `faults.RCLI4001` | |\n');
  expect(keyCoverage(markdown, messages)).toEqual({ missing: ['hints.usage'], stale: [], unknown: ['hints.more'] });
  const json = keysFromJson(JSON.stringify({ keys: [{ key: 'help.usage' }, 'help.options', { key: 'hints.more', removedIn: '0.12.0' }, { key: 'diagnostics.prompt-required', arguments: [] }] }));
  expect(keyCoverage(json, messages)).toEqual({ missing: [], stale: ['hints.more'], unknown: [] });
  expect(() => keysFromJson('{"keys":[{}]}')).toThrow();
});

const pkg = (id, version, dependencies = []) => ({ id, version, nuspec: { id, version, dependencies: dependencies.map(([dep, range]) => ({ id: dep, range, framework: 'net10.0' })) } });
const published = {
  cli: { repo: 'cli', repository: 'Runic-Artifex/runic-cli-sdk', version: '0.11.0', packages: [pkg('Runic.CommandLine', '0.11.0'), pkg('Runic.CommandLine.Spectre', '0.11.0', [['Runic.CommandLine', '[0.11.0]']])] },
  translations: { repo: 'translations', repository: 'Runic-Artifex/runic-translations-sdk', version: '0.9.1', packages: [
    pkg('Runic.Translations', '0.9.1'),
    pkg('Runic.Translations.CommandLine', '0.9.1', [['Runic.CommandLine', '[0.9.1, 0.10.0)'], ['Runic.Translations', '[0.9.1, 0.10.0-0.0)']]),
    pkg('Runic.Translations.Hosting', '0.9.1', [['Runic.Translations', '0.9.1']]),
    pkg('Runic.Translations.Build', '0.9.1', [['Runic.Translations', '0.9.1']])] },
  sdk: { repo: 'sdk', repository: 'Runic-Artifex/runic-sdk', version: '0.17.1', packages: [
    pkg('Runic.Application.Views', '0.17.1'), pkg('Runic.Application.Views.CsWebUi', '0.17.1-preview.1', [['CsWebUi', '[2.5.0-beta.4.9, 3.0.0)']]),
    pkg('Runic.Application.Views.Translations', '0.17.1', [['Runic.Translations', '[0.9.1, 0.10.0-0.0)'], ['Runic.Translations.Hosting', '0.9.1'], ['Runic.Application.Views', '[0.17.1]']])] },
};

test('range lint reproduces I1 and I4 on the published packages and passes the fixed train', () => {
  expect(lintLockstep(published)).toEqual([]);
  expect(lintRanges(published).map(item => `${item.check} ${item.message.split(':')[0]}`)).toEqual([
    'range-form Runic.Translations.CommandLine 0.9.1 -> Runic.CommandLine [0.9.1, 0.10.0)',
    'range Runic.Translations.CommandLine 0.9.1 -> Runic.CommandLine [0.9.1, 0.10.0)',
    'range-form Runic.Translations.Hosting 0.9.1 -> Runic.Translations 0.9.1']);
  const train = structuredClone(published);
  train.cli.version = '0.12.0';
  train.cli.packages = [pkg('Runic.CommandLine', '0.12.0')];
  train.translations.version = '0.10.0';
  train.translations.packages = [pkg('Runic.Translations', '0.10.0'),
    pkg('Runic.Translations.CommandLine', '0.10.0', [['Runic.CommandLine', '[0.12.0, 0.13.0-0.0)'], ['Runic.Translations', '[0.10.0, 0.11.0-0.0)']]),
    pkg('Runic.Translations.Hosting', '0.10.0', [['Runic.Translations', '[0.10.0, 0.11.0-0.0)']])];
  train.sdk.packages[2] = pkg('Runic.Application.Views.Translations', '0.17.1', [['Runic.Translations', '[0.10.0, 0.11.0-0.0)'], ['Runic.Translations.Hosting', '0.10.0']]);
  expect(lintRanges(train)).toEqual([]);
  // A later Command Line patch stays on the line; -0 is refused (nuget.org rejects it).
  train.cli.version = '0.12.1';
  train.cli.packages = [pkg('Runic.CommandLine', '0.12.1')];
  expect(lintRanges(train)).toEqual([]);
  train.translations.packages[1] = pkg('Runic.Translations.CommandLine', '0.10.0', [['Runic.CommandLine', '[0.12.0, 0.13.0-0)']]);
  expect(lintRanges(train).map(item => item.message)).toEqual([expect.stringContaining('uses -0')]);
  train.cli.packages.push(pkg('Runic.CommandLine.Spectre', '0.11.0'));
  expect(lintLockstep(train).map(item => item.message)).toEqual([expect.stringContaining('Runic.CommandLine.Spectre 0.11.0 is not part of')]);
});

test('template and tool pins must equal the train versions', () => {
  const files = [{ name: 'content/runic-app/App.csproj', text: '<PackageReference Include="Runic.Translations" Version="0.9.1" />\n<PackageReference Include="Runic.Translations.Build" Version="0.9.1" PrivateAssets="all" />' },
    { name: 'content/runic-app/.config/dotnet-tools.json', text: '"dotnet-runic-translations": {\n  "version": "0.9.1",' }];
  expect(lintTemplatePins(files, '0.9.1')).toMatchObject({ failures: [], pins: [{ id: 'Runic.Translations' }, { id: 'Runic.Translations.Build' }, { id: 'dotnet-runic-translations' }] });
  expect(lintTemplatePins(files, '0.10.0').failures).toHaveLength(3);
  expect(lintToolPins([{ id: 'dotnet-runic', version: '0.18.0', bundled: ['0.12.0'] }, { id: 'Runic.Translations', version: '0.10.0', bundled: [] }], '0.12.0')).toEqual([]);
  expect(lintToolPins([{ id: 'dotnet-runic-translations', version: '0.9.1', bundled: ['0.9.1'] }], '0.11.0')[0].message)
    .toBe("dotnet-runic-translations 0.9.1 bundles Runic.CommandLine 0.9.1, but this train's Command Line is 0.11.0");
});

test('expected failures apply only below the fixing release and report entries that no longer reproduce', () => {
  const entries = loadExpected();
  expect(entries.map(entry => entry.id)).toEqual(['I1', 'I2', 'I4', 'I7', 'I3']);
  for (const entry of entries) expect(entry.applies.below).toBe('0.10.0');
  const versions = { 'Runic.Translations.CommandLine': '0.9.1', 'Runic.Translations': '0.9.1' };
  const active = activeEntries(entries, id => versions[id] ?? null);
  expect(active).toHaveLength(4);
  expect(activeEntries(entries, () => '0.10.0')).toEqual([]);
  const nu1608 = 'error NU1608: Warning As Error: Detected package version outside of dependency constraint: Runic.Translations.CommandLine 0.9.1 requires Runic.CommandLine (>= 0.9.1 && < 0.10.0) but version Runic.CommandLine 0.11.0 was resolved.';
  const { failures, stale } = classify([
    { scenario: 'S1', check: 'build', message: nu1608 },
    { scenario: 'S1', check: 'german', message: 'English framework text in German output: Run "clide greet --help" for more information.' },
    { scenario: 'S7', check: 'build', message: nu1608 },
    { scenario: 'S2', check: 'missing', message: 'No German message for help.usage' }], active);
  expect(failures.map(item => item.expected)).toEqual(['I1', 'I2', null, null]);
  expect(stale).toEqual(['I4', 'I3']);
});

test('build logs reduce to distinct error lines without project paths', () => {
  const log = ['/w/App/App.csproj : error NU1608: Warning As Error: Detected package version outside of dependency constraint: X [/w/App.slnx]',
    '/w/App/App.Tests/App.Tests.csproj : error NU1608: Warning As Error: Detected package version outside of dependency constraint: X',
    '/w/App/Program.cs(3,1): error CS0103: The name \'x\' does not exist', '    0 Error(s)', 'Build succeeded.'].join('\n');
  expect(errorLines(log)).toEqual(['error NU1608: Warning As Error: Detected package version outside of dependency constraint: X',
    "error CS0103: The name 'x' does not exist"]);
});

test('the gate runs on dispatch, from publish workflows, nightly and for its own changes, with read-only permissions', () => {
  expect(Object.keys(workflow.on).sort()).toEqual(['pull_request', 'schedule', 'workflow_call', 'workflow_dispatch']);
  for (const trigger of ['workflow_dispatch', 'workflow_call'])
    expect(Object.keys(workflow.on[trigger].inputs)).toEqual(expect.arrayContaining(['cli', 'translations', 'sdk', 'row']));
  expect(workflow.on.workflow_call.outputs.verdict.value).toBe('${{ jobs.gate.outputs.verdict }}');
  expect(workflow.on.pull_request.paths).toContain('eng/train/**');
  expect(workflow.permissions).toEqual({ contents: 'read', actions: 'read' });
  const job = workflow.jobs.gate;
  expect(job.permissions).toBeUndefined();
  // Only N-main informs without failing; a refused artifact (exit code 3) fails every row.
  expect(job['continue-on-error']).toBeUndefined();
  // The nightly rows: the newest published versions block a gate change, the newest main runs only inform.
  expect(job.strategy.matrix.include).toContain('"row":"N-pub","cli":"published","translations":"published","sdk":"published","informational":false');
  expect(job.strategy.matrix.include).toContain('"row":"N-main","cli":"run","translations":"run","sdk":"run","informational":true');
  const gate = job.steps.find(step => step.id === 'gate');
  expect(gate.env.GH_TOKEN).toBe('${{ secrets.RUNIC_TRAIN_TOKEN || github.token }}');
  expect(gate.run).toContain('bun eng/train/gate.mjs --row "$ROW" --cli "$CLI" --translations "$TRANSLATIONS" --sdk "$SDK"');
  expect(gate.run).toContain('if [[ "$INFORMATIONAL" == true && "$code" == 1 ]]; then');
  expect(job.steps.find(step => step.uses?.startsWith('actions/upload-artifact@')).with.name).toBe('train-result-${{ github.run_id }}-${{ matrix.row }}');
  expect(scenarios.map(item => `${item.id}${item.required ? '' : '?'}`)).toEqual(['S1', 'S2', 'S3', 'S4', 'S5', 'S6', 'S7', 'S8?']);
  // The gate is read only by train-compat.yml and these tests, so a gate change plans no product jobs.
  for (const file of ['eng/train/gate.mjs', 'eng/train/expected-failures.json', '.github/workflows/train-compat.yml']) expect(engineeringOnly(file)).toBe(true);
});
