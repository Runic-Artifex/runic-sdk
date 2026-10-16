import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { sha256, inspect, VERSION } from './artifacts.mjs';
// Retry transient registry responses within one bounded request budget. A long
// Retry-After fails the budget instead of retrying earlier than the registry asks.
export async function fetchRegistry(url, {waitForAvailability=false, fetchImpl=fetch,
  sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms)), now=Date.now,
  budgetMs=180000, maxAttempts=12} = {}) {
  const deadline=now()+budgetMs;
  for(let attempt=0; attempt<maxAttempts; attempt++) {
    const remaining=deadline-now();
    assert(remaining>0,'Registry retry budget expired');
    const response=await fetchImpl(url,{signal:AbortSignal.timeout(Math.max(1,Math.min(30000,remaining)))});
    // npm answers 401 for a scoped name it does not know yet, so a just-published
    // package can report 401 or 404 until the registry exposes it.
    const retry=[429,503].includes(response.status) || (waitForAvailability && [401,404].includes(response.status));
    if(!retry || attempt===maxAttempts-1) return response;
    const header=response.headers.get('retry-after');
    let delay= Math.min(15000,1000 * 2**attempt);
    if(header !== null) {
      const seconds=Number(header);
      const requested=Number.isFinite(seconds) ? seconds*1000 : Date.parse(header)-now();
      if(Number.isFinite(requested)) delay=Math.max(0,requested);
    }
    assert(now()+delay<deadline,'Registry Retry-After exceeds retry budget; retry this read-only check later');
    await response.body?.cancel();
    await sleep(delay);
  }
}
export async function registryMatches(p, options={}) {
  let url;
  if (p.registry === 'npm') {
    const response = await fetchRegistry(`https://registry.npmjs.org/${encodeURIComponent(p.name)}/${VERSION}`,options);
    if (response.status === 404) return false;
    assert(response.ok, `npm lookup failed: ${response.status}`);
    const meta = await response.json(); assert.equal(meta.name,p.name); assert.equal(meta.version,VERSION);
    url = meta.dist.tarball;
    assert.equal(new URL(url).hostname, 'registry.npmjs.org');
  } else { const id=p.name.toLowerCase(); url=`https://api.nuget.org/v3-flatcontainer/${id}/${VERSION}/${id}.${VERSION}.nupkg`; }
  const response=await fetchRegistry(url,options);
  if (response.status===404 && p.registry==='nuget') return false;
  assert(response.ok, `Registry download failed: ${response.status}`);
  const bytes=Buffer.from(await response.arrayBuffer());
  if(p.registry==='npm') assert.equal(sha256(bytes),p.sha256,`Registry bytes differ: ${p.name}; use a new preview`);
  else {
    const dir=mkdtempSync(join(tmpdir(),'runic-registry-'));
    try { const path=join(dir,'package.nupkg'); writeFileSync(path,bytes); const actual=inspect(path,'nuget');
      assert.deepEqual(actual.content,p.content,`Registry contents differ: ${p.name}; use a new preview`);
      assert.equal(actual.source,p.source); assert.equal(actual.version,p.version);
    } finally { rmSync(dir,{recursive:true,force:true}); }
  }
  return true;
}

// dotnet restore lists a package's versions from the flat container and reports NU1102
// until the pushed version is listed there, which trails an accepted push by minutes
// (nuget.org documents up to an hour). A listed version is also downloadable.
export async function nugetLists(id, version, options={}) {
  const lower=id.toLowerCase();
  const response=await fetchRegistry(`https://api.nuget.org/v3-flatcontainer/${lower}/index.json`,options);
  if (response.status === 404) { await response.body?.cancel(); return false; }
  assert(response.ok, `NuGet version list failed for ${id}: ${response.status}`);
  const {versions}=await response.json();
  return versions.includes(version.toLowerCase());
}
// Polls until nuget.org lists every NuGet package of the release (#337), within timeoutMs.
export async function waitForNuget(packages, {timeoutMs=60*60000, intervalMs=30000, now=Date.now,
  sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms)), log=console.log, lists=nugetLists, ...options} = {}) {
  const deadline=now()+timeoutMs;
  let missing=packages.filter(p => p.registry === 'nuget');
  for (;;) {
    const listed=await Promise.all(missing.map(p => lists(p.name, p.version, options)));
    missing=missing.filter((_, index) => !listed[index]);
    if (missing.length === 0) return;
    const names=missing.map(p => `${p.name} ${p.version}`).join(', ');
    assert(now()+intervalMs < deadline, `nuget.org still does not list ${names} after ${Math.round(timeoutMs/60000)} minutes`);
    log(`Waiting for nuget.org to list ${missing.length} package(s): ${names}`);
    await sleep(intervalMs);
  }
}

// Until 1.0 every release is a preview, so npm latest follows the newest one.
// Never move latest back to an older version, for example on a late rerun.
export function needsLatest(current, version) {
  return current === undefined || Bun.semver.order(current, version) < 0;
}
// npm answers the dist-tags of an unpublished scoped name with 401. Without waiting
// for availability (a dry run before publication), a missing package has no latest.
export async function npmLatest(name, options={}) {
  if (!options.waitForAvailability) {
    const document=await fetchRegistry(`https://registry.npmjs.org/${encodeURIComponent(name)}`,options);
    await document.body?.cancel();
    if (document.status === 404) return undefined;
  }
  const response=await fetchRegistry(`https://registry.npmjs.org/-/package/${encodeURIComponent(name)}/dist-tags`,options);
  assert(response.ok, `npm dist-tag lookup failed for ${name}: ${response.status}`);
  return (await response.json()).latest;
}
