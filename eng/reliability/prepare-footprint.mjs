// Bind local footprint outputs to a fresh authenticated download of their CI artifact.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdirSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { resolve, join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { artifactHashes } from './run.mjs';
const repository='Runic-Artifex/runic-sdk';
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
export function assertAuthority(run, artifact, revision, mainRevision, historical=false) {
  assert.equal(run.repository?.full_name,repository);assert.equal(run.head_repository?.full_name,repository);
  assert.equal(run.path,'.github/workflows/ci.yml');assert.equal(run.event,'push');assert.equal(run.head_branch,'main');
  assert.equal(run.head_sha,revision);assert.equal(run.conclusion,'success');assert.equal(run.status,'completed');
  assert.equal(artifact.workflow_run?.head_sha,revision);assert.equal(artifact.workflow_run?.id,run.id);
  assert.equal(artifact.workflow_run?.repository_id,run.repository.id);assert.equal(artifact.workflow_run?.head_repository_id,run.repository.id);
  assert.equal(artifact.expired,false);assert.match(artifact.digest,/^sha256:[a-f0-9]{64}$/);
  if(!historical)assert.equal(mainRevision,revision,'Candidate must match the frozen current remote main revision');
}
export function assertDownloadedTree(localDirectory, downloadedDirectory) {
  const trusted=artifactHashes(downloadedDirectory);
  assert.deepEqual(artifactHashes(localDirectory),trusted,'Local footprint tree differs from the freshly downloaded CI artifact');
  return trusted;
}
function matrices(directory) {
  return readdirSync(directory,{withFileTypes:true}).flatMap(entry=>{
    assert.ok(!entry.isSymbolicLink(),'CI artifact may not contain symbolic links');
    const path=join(directory,entry.name);return entry.isDirectory()?matrices(path):entry.name==='matrix.json'?[path]:[];
  });
}
function main() {
  const [matrixPath,destinationPath,runId,revision,baselineConfigsPath,mode]=process.argv.slice(2);
  assert.ok(matrixPath&&destinationPath&&runId&&revision&&baselineConfigsPath,'Usage: prepare-footprint MATRIX_DIRECTORY OUTPUT_DIRECTORY CI_RUN_ID EXPECTED_REVISION BASELINE_CONFIG_DIRECTORY [--historical-baseline]');
  assert.ok(mode===undefined||mode==='--historical-baseline');const historical=mode==='--historical-baseline',kind=historical?'baseline':'candidate';
  assert.match(revision,/^[a-f0-9]{40}$/);assert.match(runId,/^\d+$/);
  const localDirectory=resolve(matrixPath),destination=resolve(destinationPath);
  const localMatrix=JSON.parse(readFileSync(join(localDirectory,'matrix.json')));
  assert.ok(['linux-x64','win-x64','osx-arm64'].includes(localMatrix.rid));
  const api=path=>JSON.parse(execFileSync('gh',['api',`repos/${repository}/${path}`],{encoding:'utf8'}));
  const run=api(`actions/runs/${runId}`);
  const artifactName=`host-footprint-${localMatrix.rid}-${runId}`;
  const found=api(`actions/runs/${runId}/artifacts`).artifacts.filter(a=>a.name===artifactName);assert.equal(found.length,1);
  const artifact=found[0];
  const mainRevision=historical?undefined:api('git/ref/heads/main').object.sha;
  assertAuthority(run,artifact,revision,mainRevision,historical);
  const temporary=mkdtempSync(join(tmpdir(),'runic-footprint-ci-'));
  try {
    // Name+run identifies the download. Check exact immutable artifact identity both
    // before and after download so replacement during preparation cannot be accepted.
    execFileSync('gh',['run','download',runId,'--repo',repository,'--name',artifactName,'--dir',temporary],{stdio:'inherit'});
    const after=api(`actions/artifacts/${artifact.id}`);
    assert.equal(after.id,artifact.id);assert.equal(after.name,artifact.name);assert.equal(after.digest,artifact.digest);
    assertAuthority(run,after,revision,mainRevision,historical);
    const downloadedMatrices=matrices(temporary);assert.equal(downloadedMatrices.length,1,'Expected one footprint matrix in CI artifact');
    const matrixFile=downloadedMatrices[0],trustedDirectory=dirname(matrixFile),matrix=JSON.parse(readFileSync(matrixFile));
    assert.equal(matrix.revision,revision);assert.equal(matrix.dirty,false);assert.equal(matrix.rid,localMatrix.rid);
    const verifiedTree=assertDownloadedTree(localDirectory,trustedDirectory);
    // All metadata below is read from the fresh download, never local claims.
    const pending=[];
    for(const item of matrix.cases){
      assert.ok(['desktop','cswebui'].includes(item.host));assert.ok(['default','minimal'].includes(item.profile));
      const provider=item.nativeProvider??'None';assert.ok(['None','Windows','Linux','MacOS'].includes(provider));
      const profile=`${item.host}-${item.profile}`,name=profile+(provider==='None'?'':'-provider');
      const reportPath=join(trustedDirectory,`${name}.json`),report=JSON.parse(readFileSync(reportPath));
      assert.equal(item.verification,'passed');assert.equal(report.publishExitCode,0);assert.equal(report.verification.status,'passed');
      const parts=report.publishDirectory.replaceAll('\\','/').split('/').slice(-2);assert.ok(parts.every(p=>p&&p!=='.'&&p!=='..'));
      const directory=join(localDirectory,...parts),artifacts=artifactHashes(join(trustedDirectory,...parts));
      assert.deepEqual(artifacts,Object.fromEntries(report.files.map(file=>[file.path.replaceAll('\\','/'),file.sha256])),'CI published files differ from its report');
      const executable=report.files.find(f=>f.category==='main-executable');assert.ok(executable&&Object.hasOwn(artifacts,executable.path));
      const provenance=join(destination,`${name}-${kind}-provenance.json`);
      const provenanceBody={sourceRevision:revision,sourceRole:kind,artifacts,githubRun:run.html_url,artifactId:artifact.id,artifactDigest:artifact.digest,downloadedTreeSha256:hash(JSON.stringify(verifiedTree)),reportSha256:hash(readFileSync(reportPath)),buildSdk:report.sdk,nativeProvider:provider};
      const baseline=JSON.parse(readFileSync(join(resolve(baselineConfigsPath),`${profile}-baseline-config.json`)));
      const config={...baseline,directory,executable:join(directory,executable.path),provenance,profile:name,samples:15};
      pending.push([provenance,provenanceBody],[join(destination,`${name}-${kind}-config.json`),config]);
    }
    assertDownloadedTree(localDirectory,trustedDirectory);
    if(!historical)assert.equal(api('git/ref/heads/main').object.sha,revision,'Remote main changed during candidate preparation');
    mkdirSync(destination,{recursive:true});
    writeFileSync(join(destination,'ci-provenance.json'),JSON.stringify({run,artifact,sourceRole:kind,matrixSha256:hash(readFileSync(matrixFile)),verifiedTree},null,2));
    for(const [path,body] of pending){writeFileSync(path,JSON.stringify(body,null,2));console.log(path);}
  } finally {rmSync(temporary,{recursive:true,force:true});}
}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url))main();
