import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { authority, scan, json, sha256, validateCandidate, VERSION, REPOSITORY } from './artifacts.mjs';
import { verifyGates } from './gates.mjs';
import { acceptancePolicy } from './policy.mjs';
import { finalCI } from './ci.mjs';
import { registryMatches } from './registry.mjs';
import { runChecked } from './process.mjs';
const root = fileURLToPath(new URL('../..',import.meta.url));
const [command,...args]=process.argv.slice(2);
const run=runChecked;
function verify(directory,candidate) {
  validateCandidate(candidate);
  assert.equal(execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim(),candidate.source,'Checkout must be frozen source');
  assert.deepEqual(scan(directory,authority(json(join(root,'eng/workspace.json'))),candidate.source),candidate.packages);
}
async function verifiedCIBytes(candidate) {
  finalCI(candidate);
  const dir=mkdtempSync(join(tmpdir(),'runic-final-ci-'));
  try {
    run('gh',['run','download',candidate.ciRunId,'--repo',REPOSITORY,'--name',`runic-sdk-${candidate.ciRunId}`,'--dir',dir]);
    verify(dir,candidate);
  } finally {rmSync(dir,{recursive:true,force:true});}
}
if(command==='seal') {
  const [directory,source,ciRunId,output,profile='demo-preview']=args;
  assert.match(source,/^[a-f0-9]{40}$/); assert.match(ciRunId,/^[1-9][0-9]*$/);
  assert.equal(execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim(),source);
  assert.equal(execFileSync('git',['status','--porcelain','--untracked-files=no'],{cwd:root,encoding:'utf8'}).trim(),'','Commit source before sealing');
  const body={schema:'runic.preview/1',version:VERSION,repository:REPOSITORY,source,ciRunId,acceptancePolicy:acceptancePolicy(profile),packages:scan(directory,authority(json(join(root,'eng/workspace.json'))),source)};
  const candidate={...body,digest:sha256(JSON.stringify(body))};
  await verifiedCIBytes(candidate);
  writeFileSync(output,JSON.stringify(candidate,null,2)+'\n',{flag:'wx'});
} else if(command==='verify') verify(args[0],json(args[1]));
else if(command==='gates') verifyGates(json(args[0]),json(args[1]));
else if(command==='final-ci') await verifiedCIBytes(json(args[0]));
else if(command==='registry') {
  const candidate=json(args[0]); validateCandidate(candidate);
  for(const p of candidate.packages) assert(await registryMatches(p,{waitForAvailability:true}),`Not available: ${p.name}`);
} else if(command==='publish') {
  const [directory,manifest,receipts]=args, candidate=json(manifest);
  verify(directory,candidate); verifyGates(candidate,json(receipts)); await verifiedCIBytes(candidate);
  assert.equal(process.env.GITHUB_REPOSITORY,REPOSITORY,'OIDC publication must run in release repository');
  assert.equal(process.env.GITHUB_SHA,candidate.source,'Publish workflow must run at frozen source');
  assert(process.env.ACTIONS_ID_TOKEN_REQUEST_URL,'OIDC unavailable');
  // Every package already present must match before publishing any missing identity.
  const pending=[];
  for(const p of candidate.packages) if(!await registryMatches(p)) pending.push(p);
  for(const p of pending) {
    const path=resolve(directory,p.file);
    if(p.registry==='npm') run('npm',['publish',path,'--tag','preview','--access','public','--provenance','--registry','https://registry.npmjs.org']);
    else { assert(process.env.NUGET_API_KEY,'NuGet OIDC login missing'); run('dotnet',['nuget','push',path,'--source','https://api.nuget.org/v3/index.json','--api-key',process.env.NUGET_API_KEY]); }
  }
} else throw new Error('Use seal <packages> <source> <run> <new-manifest> [demo-preview|full-v1], verify <packages> <manifest>, gates <manifest> <receipts>, final-ci <manifest>, registry <manifest>, or publish <packages> <manifest> <receipts>');
