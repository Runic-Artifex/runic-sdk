import { test, expect } from 'bun:test';
import { readFileSync } from 'node:fs';
import { checkNugetPush, globMatches, nugetOwnership, nugetPolicy, publishMissing } from './nuget-policy.mjs';
const workspace = JSON.parse(readFileSync(new URL('../workspace.json', import.meta.url), 'utf8'));
// Keyless nuget.org lookups answered from fixtures, so no test reaches nuget.org.
const search = 'https://azuresearch-usnc.nuget.org/query';
const fixtures = {
  'runic.desktop': {owners: ['runic-artifex']},
  'runic.foreign': {owners: ['someone-else']},
  'runic.unlisted': {unlisted: true},
};
const requests = [];
async function nugetFixture(url) {
  requests.push(url);
  const {pathname, searchParams} = new URL(url);
  if (url === 'https://api.nuget.org/v3/index.json')
    return Response.json({resources: [{'@id': search, '@type': 'SearchQueryService/3.5.0'}]});
  if (url.startsWith(`${search}?`)) {
    const id = searchParams.get('q').replace('packageid:', ''), fixture = fixtures[id.toLowerCase()];
    return Response.json({data: fixture && !fixture.unlisted ? [{id, owners: fixture.owners}] : []});
  }
  const flat = pathname.match(/^\/v3-flatcontainer\/([^/]+)\/index\.json$/);
  if (flat) return fixtures[flat[1]] ? Response.json({versions: ['0.7.0-preview.7']}) : new Response('', {status: 404});
  throw new Error(`Unexpected request ${url}`);
}
const ownership = id => nugetOwnership(id, {fetchImpl: nugetFixture});
test('nuget.org ownership lookups need no key and tell new IDs from published ones', async () => {
  expect(await ownership('Runic.Desktop')).toEqual({exists: true, owners: ['runic-artifex']});
  expect(await ownership('Runic.Unlisted')).toEqual({exists: true, owners: undefined});
  expect(await ownership('Runic.New')).toEqual({exists: false});
  expect(requests.every(url => new URL(url).hostname.endsWith('nuget.org'))).toBe(true);
  await expect(nugetOwnership('Runic.Desktop', {maxAttempts: 1, fetchImpl: async () => new Response('', {status: 500})})).rejects.toThrow('NuGet lookup failed: 500');
});
test('package globs follow nuget.org: * matches any run, case-insensitively', () => {
  expect(globMatches('*', 'dotnet-runic')).toBe(true);
  expect(globMatches('Runic.*', 'runic.application.views')).toBe(true);
  expect(globMatches('dotnet-runic*', 'dotnet-runic')).toBe(true);
  expect(globMatches('Runic.*', 'Runic')).toBe(false);
  expect(globMatches('Runic.*', 'RunicXApplication')).toBe(false);
  expect(globMatches('Runic.Application', 'Runic.Application.Views')).toBe(false);
});
test('an ID outside the policy fails the check by name with the remedy; a covered published ID passes', async () => {
  const policy = {owner: 'runic-artifex', packageGlobs: ['Runic.Desktop', 'Runic.Navigation'], pushNewPackages: true};
  await checkNugetPush(['Runic.Desktop'], {policy, ownership, log: () => {}});
  const failure = checkNugetPush(['Runic.Desktop', 'Runic.New', 'dotnet-runic'], {policy, ownership, log: () => {}});
  await expect(failure).rejects.toThrow('- Runic.New matches none of the policy\'s package globs (Runic.Desktop, Runic.Navigation).\n- dotnet-runic matches none');
  await expect(failure).rejects.toThrow('nothing was published');
  await expect(failure).rejects.toThrow('eng/release/nuget-policy.json');
  await expect(failure).rejects.not.toThrow('- Runic.Desktop');
});
test('a covered new ID passes with a notice unless the policy may only push new versions', async () => {
  const messages = [], policy = {owner: 'runic-artifex', packageGlobs: ['Runic.*'], pushNewPackages: true};
  await checkNugetPush(['Runic.Desktop', 'Runic.New', 'Runic.Unlisted'], {policy, ownership, log: message => messages.push(message)});
  expect(messages).toEqual([
    '::notice::Runic.New is a new nuget.org package ID; the mirrored policy of runic-artifex covers it (glob Runic.*).',
    '::warning::Runic.Unlisted has no listed version on nuget.org, so its owners could not be checked.',
  ]);
  await expect(checkNugetPush(['Runic.New'], {policy: {...policy, pushNewPackages: false}, ownership, log: () => {}}))
    .rejects.toThrow('Runic.New is a new nuget.org package ID, but the policy may only push new versions');
});
test('an existing ID that runic-artifex does not own fails the check', async () => {
  await expect(checkNugetPush(['Runic.Foreign'], {ownership, log: () => {}}))
    .rejects.toThrow('Runic.Foreign is owned on nuget.org by someone-else, not by runic-artifex');
});
test('the NuGet check covers only unpublished packages, runs in the dry run, and precedes every push', async () => {
  const packages = [
    {registry: 'npm', name: '@runic-artifex/views'},
    {registry: 'nuget', name: 'Runic.Desktop'},
    {registry: 'nuget', name: 'Runic.New'},
  ];
  for (const dryRun of [false, true]) {
    const pushed = [], checked = [];
    await expect(publishMissing(packages, {dryRun, log: () => {}, push: p => { pushed.push(p.name); },
      matches: async p => p.name === 'Runic.Desktop',
      checkPush: async ids => { checked.push(...ids); throw new Error('refused'); }})).rejects.toThrow('refused');
    expect(checked).toEqual(['Runic.New']);
    expect(pushed).toEqual([]);
  }
  const pushed = [], checked = [];
  await publishMissing(packages, {dryRun: true, log: () => {}, matches: async () => false,
    checkPush: async ids => { checked.push(...ids); }, push: p => { pushed.push(p.name); }});
  expect(checked).toEqual(['Runic.Desktop', 'Runic.New']);
  expect(pushed).toEqual([]);
  await publishMissing(packages, {dryRun: false, log: () => {}, matches: async () => false,
    checkPush: async () => {}, push: p => { pushed.push(p.name); }});
  expect(pushed).toEqual(packages.map(p => p.name));
});
test('the mirrored policy covers every NuGet package of the workspace', () => {
  expect(nugetPolicy.owner).toBe('runic-artifex');
  expect(typeof nugetPolicy.pushNewPackages).toBe('boolean');
  expect(workspace.nuget.length).toBeGreaterThan(0);
  const uncovered = workspace.nuget.map(entry => entry.name).filter(id => !nugetPolicy.packageGlobs.some(pattern => globMatches(pattern, id)));
  expect(uncovered).toEqual([]);
});
test('publish-preview runs the policy check in the dry run and after NuGet login, before publishing', () => {
  const cli = readFileSync(new URL('./cli.mjs', import.meta.url), 'utf8');
  expect(cli).toContain('await publishMissing(candidate.packages, {dryRun,');
  expect(cli.match(/dotnet', \['nuget', 'push'/g)).toHaveLength(1);
  const {candidate, publish} = Bun.YAML.parse(readFileSync(new URL('../../.github/workflows/publish-preview.yml', import.meta.url), 'utf8')).jobs;
  expect(candidate.steps.some(s => s.run?.includes('cli.mjs publish artifacts/packages artifacts/release/packages.json --dry-run'))).toBe(true);
  const steps = publish.steps;
  expect(steps.findIndex(s => s.uses?.startsWith('NuGet/login@'))).toBeLessThan(steps.findIndex(s => s.run?.trim() === 'bun eng/release/cli.mjs publish artifacts/packages artifacts/release/packages.json'));
});
