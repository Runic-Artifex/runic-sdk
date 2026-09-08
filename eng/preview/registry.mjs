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
    const retry=[429,503].includes(response.status) || (waitForAvailability && response.status===404);
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
