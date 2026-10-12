import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fetchRegistry, registryMatches } from './registry.mjs';
// The nuget.org trusted-publishing policy, mirrored by hand: nuget.org does not
// publish it, and no request can test a key's push permission for a version that
// does not exist yet (README "New NuGet package IDs").
export const nugetPolicy = JSON.parse(readFileSync(new URL('./nuget-policy.json', import.meta.url), 'utf8'));
// nuget.org package globs: `*` matches any run of characters, case-insensitively.
export function globMatches(pattern, id) {
  const expression = pattern.split('*').map(part => part.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*');
  return new RegExp(`^${expression}$`, 'i').test(id);
}
async function nugetJson(url, options) {
  const response = await fetchRegistry(url, options);
  if (response.status === 404) { await response.body?.cancel(); return undefined; }
  assert(response.ok, `NuGet lookup failed: ${response.status} ${url}`);
  return response.json();
}
// Read-only and keyless: the owners of an ID nuget.org knows (undefined when the
// search index lists none of its versions), or `exists: false` for a new ID.
// `options` reach fetchRegistry, so tests inject fetchImpl.
export async function nugetOwnership(id, options = {}) {
  const index = await nugetJson('https://api.nuget.org/v3/index.json', options);
  const search = index?.resources?.find(resource => String(resource['@type']).startsWith('SearchQueryService'))?.['@id'];
  assert.equal(typeof search, 'string', 'nuget.org service index has no SearchQueryService');
  const query = new URLSearchParams({q: `packageid:${id}`, prerelease: 'true', semVerLevel: '2.0.0', take: '1'});
  const result = await nugetJson(`${search}?${query}`, options);
  const entry = result?.data?.find(value => value.id.toLowerCase() === id.toLowerCase());
  if (entry) return {exists: true, owners: [entry.owners ?? []].flat()};
  // Search omits an ID whose versions are all unlisted; the flat container keeps them.
  const versions = await nugetJson(`https://api.nuget.org/v3-flatcontainer/${id.toLowerCase()}/index.json`, options);
  return versions ? {exists: true, owners: undefined} : {exists: false};
}
// Fails, naming every ID and the remedy, when the trusted-publishing key could not
// push one of these IDs. cli.mjs publish runs it in the dry run and before the first push.
export async function checkNugetPush(ids, {policy = nugetPolicy, ownership = nugetOwnership, log = console.log} = {}) {
  const problems = [];
  for (const id of ids) {
    const glob = policy.packageGlobs.find(pattern => globMatches(pattern, id));
    const state = await ownership(id);
    if (!glob)
      problems.push(`${id} matches none of the policy's package globs (${policy.packageGlobs.join(', ')}).`);
    if (state.exists && state.owners && !state.owners.some(owner => owner.toLowerCase() === policy.owner.toLowerCase()))
      problems.push(`${id} is owned on nuget.org by ${state.owners.join(', ') || 'nobody listed'}, not by ${policy.owner}, so the policy cannot push it; add ${policy.owner} as an owner or choose another ID.`);
    else if (!state.exists && !policy.pushNewPackages)
      problems.push(`${id} is a new nuget.org package ID, but the policy may only push new versions of existing packages.`);
    else if (glob && !state.exists)
      log(`::notice::${id} is a new nuget.org package ID; the mirrored policy of ${policy.owner} covers it (glob ${glob}).`);
    else if (glob && !state.owners)
      log(`::warning::${id} has no listed version on nuget.org, so its owners could not be checked.`);
  }
  if (problems.length) throw new Error([
    'The NuGet trusted-publishing key cannot push every package of this release; nothing was published.',
    ...problems.map(problem => `- ${problem}`),
    `Before releasing, edit the trusted-publishing policy of ${policy.owner} on nuget.org so that it may push new packages and its package glob matches each ID, then update eng/release/nuget-policy.json to match (eng/release/README.md, "New NuGet package IDs").`,
  ].join('\n'));
}
// Every registry read and the NuGet push check finish before the first push, so a
// refused package ID stops the release before anything is published.
export async function publishMissing(packages, {dryRun, push, matches = registryMatches, checkPush = checkNugetPush, log = console.log}) {
  // Partial publication may be resumed, but an existing version is never replaced:
  // registryMatches throws when a published version has different contents.
  const pending = [];
  for (const p of packages) if (!await matches(p)) pending.push(p);
  await checkPush(pending.filter(p => p.registry === 'nuget').map(p => p.name));
  log(`${dryRun ? 'Would publish' : 'Publishing'} ${pending.length} of ${packages.length} packages: ${pending.map(p => p.name).join(', ') || 'none (all already published with matching contents)'}`);
  if (!dryRun) for (const p of pending) push(p);
}
