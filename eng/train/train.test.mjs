import { expect, test } from 'bun:test';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { compareVersions, lineUpperBound, parseRange, rangeContains, sameLine } from './versions.mjs';
import { nugetConfig, parseNuspec, repoOf } from './nuget.mjs';
import { followsMain, isNamedCiRun, parseSpec, selectRunAtOrAfter, successfulMainRuns, upstreamMovement, versionForRelease } from './sources.mjs';
import { refuseFollowingMain } from './gate.mjs';
import { repos as allRepos } from './nuget.mjs';
import { keyCoverage, keysFromJson, keysFromMarkdown, lintLockstep, lintRanges, lintTemplatePins, lintToolPins, rmf2MessageNames } from './checks.mjs';
import { activeEntries, classify, loadExpected } from './expected.mjs';
import { bindingError, errorLines, scenarios } from './scenarios.mjs';
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

test('inputs are published[:version], run[:id] or local:<dir>, and a release includes its own prerelease line', () => {
  expect(parseSpec('published')).toEqual({ kind: 'published', argument: null, commit: null, text: 'published' });
  expect(parseSpec('published:0.12.0').argument).toBe('0.12.0');
  expect(parseSpec('run:43302549984')).toMatchObject({ kind: 'run', argument: '43302549984' });
  expect(parseSpec('local:artifacts/packages')).toMatchObject({ kind: 'local', argument: 'artifacts/packages' });
  for (const bad of ['main', 'run:abc', 'published:x', 'local', '']) expect(() => parseSpec(bad)).toThrow();
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
  // Command Line 0.12's writer: `arguments` and `message`, with `message: null` for a key without a catalog message.
  const written = keysFromJson(JSON.stringify({ keys: [
    { key: 'help.usage', arguments: [], message: 'help.usage' },
    { key: 'faults.RCLI4001', arguments: [], message: null },
  ] }));
  expect([...written.required]).toEqual(['help.usage']);
  expect([...written.withoutMessage]).toEqual(['faults.RCLI4001']);
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
  // Translations 0.10.0 fixed the 0.18 train's entries; these are four of them as they were.
  expect(loadExpected()).toEqual([]);
  const directory = mkdtempSync(join(tmpdir(), 'runic-expected-'));
  const below = { 'Runic.Translations.CommandLine': '0.10.0', 'Runic.Translations': '0.10.0' };
  const entry = (id, applies, failures) => ({ id, title: id, remove: 'After Translations 0.10.0.', applies: { package: applies, below: below[applies] }, failures });
  writeFileSync(join(directory, 'expected-failures.json'), JSON.stringify({ entries: [
    { ...entry('I1', 'Runic.Translations.CommandLine', [{ scenario: 'S1', match: 'NU1608: .*Runic\\.Translations\\.CommandLine \\S+ requires Runic\\.CommandLine' }]), waive: ['NU1608'] },
    entry('I2', 'Runic.Translations.CommandLine', [{ scenario: 'S1', check: 'german', match: '^English framework text in German output: Run "' }]),
    entry('I4', 'Runic.Translations', [{ scenario: 'S6', check: 'range-form', match: '^Runic\\.Translations\\.(Hosting|Wpf) ' }]),
    entry('I3', 'Runic.Translations', [{ scenario: 'S8', match: 'NU1(605|109): .*CsWebUi' }])] }));
  let entries;
  try { entries = loadExpected(join(directory, 'expected-failures.json')); } finally { rmSync(directory, { recursive: true, force: true }); }
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

test('the newest successful main run is chosen from the unfiltered listing', () => {
  const runs = [
    { id: 5, head_branch: 'main', status: 'completed', conclusion: 'cancelled' },
    { id: 7, head_branch: 'main', status: 'in_progress', conclusion: null },
    { id: 4, head_branch: 'main', status: 'completed', conclusion: 'success' },
    { id: 6, head_branch: 'main', status: 'completed', conclusion: 'success' },
    { id: 8, head_branch: 'feature', status: 'completed', conclusion: 'success' },
  ];
  expect(successfulMainRuns(runs).map(run => run.id)).toEqual([6, 4]);
  expect(successfulMainRuns(undefined)).toEqual([]);
});

test('a failed binding reports the script\'s error, not Bun\'s banner', () => {
  const output = 'error: x/package-lock.json: @runic-artifex/vite-plugin-runic-translations 0.12.0 is locked, but the train candidate is 0.13.0\n      at main (bind.mjs:12:5)\n\nBun v1.4.4 (Linux x64)\n';
  expect(bindingError(output)).toBe('error: x/package-lock.json: @runic-artifex/vite-plugin-runic-translations 0.12.0 is locked, but the train candidate is 0.13.0');
  expect(bindingError('Bun v1.4.4 (Linux x64)\n')).toBe('(no output)');
});

test('run@<commit> names a full commit; a bare run follows main and only informational rows take it', () => {
  const commit = 'c1eb5af8'.padEnd(40, '0');
  expect(parseSpec(`run@${commit}`)).toEqual({ kind: 'run', argument: null, commit, text: `run@${commit}` });
  for (const bad of ['run@c1eb5af8', `published@${commit}`, `run@${commit.toUpperCase()}`]) expect(() => parseSpec(bad)).toThrow();
  expect(followsMain(parseSpec('run'))).toBe(true);
  expect(followsMain(parseSpec(`run@${commit}`))).toBe(false);
  expect(followsMain(parseSpec('run:43913357796'))).toBe(false);
  expect(() => refuseFollowingMain({ row: 'P-tr', cli: 'published:0.13.0', translations: 'run:1', sdk: 'run' })).toThrow(/Row P-tr needs a commit or run for --sdk: pass run@<commit>/);
  expect(() => refuseFollowingMain({ row: 'P-tr', cli: 'published:0.13.0', translations: 'run:1', sdk: `run@${commit}` })).not.toThrow();
  expect(() => refuseFollowingMain({ row: 'N-main', cli: 'run', translations: 'run', sdk: 'run' })).not.toThrow();
  // The publish rows of the workflow pass their inputs through; the nightly N-main row is the one that follows main.
  expect(workflow.on.workflow_dispatch.inputs.sdk.description).toContain('run@<commit>');
});

// A fake GitHub API: run listings of ci.yml, compare results by head commit and artifacts by run id.
function fakeGitHub({ runs, ahead = null, artifacts = {}, missing = [], onMain = [] }) {
  const requests = [];
  const repo = allRepos.sdk;
  const fetchImpl = async url => {
    requests.push(url);
    const reply = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
    const { pathname, searchParams } = new URL(url);
    if (pathname.endsWith('/actions/workflows/ci.yml/runs')) {
      let listed = runs.filter(run => !searchParams.get('head_sha') || run.head_sha === searchParams.get('head_sha'))
        .filter(run => searchParams.get('event') ? (run.event ?? 'push') === searchParams.get('event') : true)
        .filter(run => searchParams.get('branch') ? (run.head_branch ?? 'main') === searchParams.get('branch') : true);
      // GitHub's status filter omitted a successful run for over an hour (I2): a selection that used it would miss run 30.
      if (searchParams.get('status') === 'success') listed = listed.filter(run => run.conclusion === 'success' && !missing.includes(run.id));
      return reply({ workflow_runs: listed.map(run => ({ head_branch: 'main', event: 'push', path: '.github/workflows/ci.yml',
        repository: { full_name: repo.repository }, html_url: `https://github.com/${repo.repository}/actions/runs/${run.id}`, ...run })) });
    }
    const compare = /\/compare\/([0-9a-f]+)\.\.\.([0-9a-f]+|main)$/.exec(pathname);
    // `<commit>...main`: whether main is at or after the commit (onMain lists the commits on main).
    if (compare?.[2] === 'main') return reply({ status: onMain.includes(compare[1]) ? 'ahead' : 'diverged' });
    if (compare) return ahead ? reply({ status: ahead[compare[2]] ?? 'behind' }) : reply({ message: 'Not Found' }, 404);
    const artifact = /\/actions\/runs\/(\d+)\/artifacts$/.exec(pathname);
    if (artifact) {
      const id = Number(artifact[1]);
      return reply({ artifacts: artifacts[id] === false ? [] : [{ id: id * 10, name: repo.artifact(id), expired: false }] });
    }
    return reply({ message: 'Not Found' }, 404);
  };
  return { repo, fetchImpl, requests };
}

const sha = text => text.padEnd(40, '0');

test('a train input takes the newest green main run at or after its commit, from the unfiltered listing', async () => {
  const pinned = sha('a1'), later = sha('b2'), older = sha('c3');
  const { repo, fetchImpl, requests } = fakeGitHub({
    runs: [
      { id: 50, head_sha: later, status: 'in_progress', conclusion: null },
      { id: 40, head_sha: later, status: 'completed', conclusion: 'failure' },
      { id: 30, head_sha: later, status: 'completed', conclusion: 'success' },
      { id: 20, head_sha: pinned, status: 'completed', conclusion: 'success' },
      { id: 10, head_sha: older, status: 'completed', conclusion: 'success' },
    ],
    ahead: { [later]: 'ahead', [older]: 'behind' },
    missing: [30],
  });
  const run = await selectRunAtOrAfter(repo, pinned, { fetchImpl });
  // Run 30 is newer than the pinned commit's own run 20 and descends from it; the status filter would have hidden it.
  expect(run.id).toBe(30);
  expect(requests.some(url => url.includes('status='))).toBe(false);
  // The pinned commit's own run is accepted when nothing newer qualifies.
  const own = fakeGitHub({ runs: [{ id: 20, head_sha: pinned, status: 'completed', conclusion: 'success' }], ahead: {} });
  expect((await selectRunAtOrAfter(own.repo, pinned, { fetchImpl: own.fetchImpl })).id).toBe(20);
});

test('without a run at or after the commit the error names the commit and every run found', async () => {
  const pinned = sha('a1'), older = sha('c3'), later = sha('b2');
  const { repo, fetchImpl } = fakeGitHub({
    runs: [
      { id: 60, head_sha: later, status: 'queued', conclusion: null },
      { id: 50, head_sha: later, status: 'completed', conclusion: 'success' },
      { id: 10, head_sha: older, status: 'completed', conclusion: 'success' },
    ],
    ahead: { [later]: 'ahead', [older]: 'behind' },
    artifacts: { 50: false },
  });
  const error = await selectRunAtOrAfter(repo, pinned, { fetchImpl }).catch(caught => caught);
  expect(error.message).toContain(`Runic-Artifex/runic-sdk has no successful ci.yml run on main (or merge queue run of a commit on main) at or after ${pinned}`);
  expect(error.message).toContain('60 (b20000000, push, queued): not a success');
  expect(error.message).toContain('50 (b20000000, push, completed/success): no unexpired runic-sdk-50 artifact');
  expect(error.message).toContain('10 (c30000000, push, completed/success): behind a10000000, not at or after it');
  expect(error.message).toContain('dispatch ci.yml on main once');
  // A rejection by the caller (the train feed's version check) is reported with the others.
  const rejected = fakeGitHub({ runs: [{ id: 20, head_sha: pinned, status: 'completed', conclusion: 'success' }], ahead: {} });
  await expect(selectRunAtOrAfter(rejected.repo, pinned, { fetchImpl: rejected.fetchImpl }, async () => ({ reason: 'Runic.Translations 0.13.0, not 0.14.0' })))
    .rejects.toThrow('20 (a10000000, push, completed/success): Runic.Translations 0.13.0, not 0.14.0');
  // A commit GitHub does not know.
  const unknown = fakeGitHub({ runs: [{ id: 20, head_sha: later, status: 'completed', conclusion: 'success' }] });
  await expect(selectRunAtOrAfter(unknown.repo, pinned, { fetchImpl: unknown.fetchImpl })).rejects.toThrow(`has no commit ${pinned}`);
  await expect(selectRunAtOrAfter(unknown.repo, 'a1', { fetchImpl: unknown.fetchImpl })).rejects.toThrow('Expected a full commit SHA');
});

// D077.22: the merge queue moves main to exactly the commit its run tested.
const queue = (id, head_sha, extra = {}) => ({ id, head_sha, event: 'merge_group', head_branch: `gh-readonly-queue/main/pr-7-${'d'.repeat(40)}`,
  status: 'completed', conclusion: 'success', ...extra });
test('a passed merge queue run of a commit on main counts as its main run', async () => {
  const pinned = sha('a1'), ejected = sha('e5');
  // The push run of the commit ran nothing (its queue run covered it) and uploaded no artifact.
  const covered = fakeGitHub({ runs: [{ id: 21, head_sha: pinned, status: 'completed', conclusion: 'success' }, queue(20, pinned)],
    ahead: {}, artifacts: { 21: false }, onMain: [pinned] });
  expect((await selectRunAtOrAfter(covered.repo, pinned, { fetchImpl: covered.fetchImpl })).id).toBe(20);
  expect(covered.requests.some(url => new URL(url).searchParams.get('event') === 'merge_group')).toBe(true);
  // A queue run of a commit that never reached main (its group failed) is not taken.
  const off = fakeGitHub({ runs: [queue(30, ejected), { id: 10, head_sha: pinned, status: 'completed', conclusion: 'success' }],
    ahead: { [ejected]: 'ahead' }, onMain: [pinned] });
  expect((await selectRunAtOrAfter(off.repo, pinned, { fetchImpl: off.fetchImpl })).id).toBe(10);
  const none = fakeGitHub({ runs: [queue(30, ejected)], ahead: { [ejected]: 'ahead' }, onMain: [] });
  await expect(selectRunAtOrAfter(none.repo, pinned, { fetchImpl: none.fetchImpl }))
    .rejects.toThrow(`30 (e50000000, merge_group, completed/success): a merge queue run of a commit not on main`);
  // The listing rules: queue runs by their queue ref, other runs only on main.
  expect(successfulMainRuns([queue(3, pinned), queue(4, pinned, { head_branch: 'gh-readonly-queue/other/pr-1' }), { id: 5, head_branch: 'feature',
    status: 'completed', conclusion: 'success' }]).map(run => run.id)).toEqual([3]);
  const repo = allRepos.sdk;
  const named = extra => ({ path: '.github/workflows/ci.yml', repository: { full_name: repo.repository }, head_repository: { full_name: repo.repository },
    ...queue(3, pinned), ...extra });
  expect(isNamedCiRun(named({}), repo)).toBe(true);
  expect(isNamedCiRun(named({ head_branch: 'feature' }), repo)).toBe(false);
  expect(isNamedCiRun(named({ path: '.github/workflows/nix.yml' }), repo)).toBe(false);
});

test('an upstream candidate must be the commit, or the same bytes, that runic-sdk\'s train feed served', () => {
  const feed = { upstreams: { translations: { repository: 'Runic-Artifex/runic-translations-sdk', version: '0.14.0', run: { id: 7, headSha: sha('a1') },
    packages: [{ id: 'Runic.Translations', version: '0.14.0', sha256: 'n1' }],
    npm: [{ name: '@runic-artifex/vite-plugin-runic-translations', version: '0.14.0', sha256: 'p1' }] } } };
  const candidate = (headSha, nuget = 'n1', npm = 'p1') => ({ translations: { repository: 'Runic-Artifex/runic-translations-sdk', version: '0.14.0', run: { id: 9, headSha },
    packages: [{ id: 'Runic.Translations', version: '0.14.0', sha256: nuget }],
    npm: [{ name: '@runic-artifex/vite-plugin-runic-translations', version: '0.14.0', sha256: npm }] } });
  expect(upstreamMovement(feed, candidate(sha('a1')))).toMatchObject({ problems: [] });
  expect(upstreamMovement(feed, candidate(sha('a1'))).checked[0]).toContain('the same commit as this candidate');
  // Main moved by a commit that changed no package (reproducible archives, H1).
  expect(upstreamMovement(feed, candidate(sha('b2'))).checked[0]).toContain('every package is byte-identical');
  const moved = upstreamMovement(feed, candidate(sha('b2'), 'n1', 'p2'));
  expect(moved.problems[0]).toContain('Runic-Artifex/runic-translations-sdk main moved since runic-sdk\'s train candidate used it');
  expect(moved.problems[0]).toContain('npm:@runic-artifex/vite-plugin-runic-translations@0.14.0');
  // No train feed, another version, or a published input: nothing to compare.
  expect(upstreamMovement(null, candidate(sha('b2')))).toEqual({ checked: [], problems: [] });
  expect(upstreamMovement(feed, { translations: { ...candidate(sha('b2')).translations, version: '0.15.0' } })).toEqual({ checked: [], problems: [] });
  expect(upstreamMovement(feed, { translations: { ...candidate(sha('b2')).translations, run: null } })).toEqual({ checked: [], problems: [] });
});
