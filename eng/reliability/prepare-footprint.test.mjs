import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { assertAuthority, assertDownloadedTree } from './prepare-footprint.mjs';
const revision='a'.repeat(40);
function authority(){return {run:{id:42,repository:{id:7,full_name:'Runic-Artifex/runic-sdk'},head_repository:{full_name:'Runic-Artifex/runic-sdk'},path:'.github/workflows/ci.yml',event:'push',head_branch:'main',head_sha:revision,conclusion:'success',status:'completed'},artifact:{expired:false,digest:'sha256:'+'b'.repeat(64),workflow_run:{id:42,head_sha:revision,repository_id:7,head_repository_id:7}}};}
test('candidate requires expected repository, push workflow, branch and current frozen head',()=>{
 for(const change of [x=>x.run.repository.full_name='fork/runic-sdk',x=>x.run.head_repository.full_name='fork/runic-sdk',x=>x.run.path='.github/workflows/other.yml',x=>x.run.event='pull_request',x=>x.run.head_branch='feature',x=>x.run.conclusion='failure',x=>x.artifact.workflow_run.id=43,x=>x.artifact.workflow_run.head_repository_id=8]){
  const value=authority();change(value);assert.throws(()=>assertAuthority(value.run,value.artifact,revision,revision));
 }
 const value=authority();assertAuthority(value.run,value.artifact,revision,revision);assert.throws(()=>assertAuthority(value.run,value.artifact,revision,'c'.repeat(40)));
 assertAuthority(value.run,value.artifact,revision,undefined,true);
});
test('self-consistent tampered binary and report cannot impersonate downloaded CI artifact',()=>{
 const temp=mkdtempSync(join(tmpdir(),'runic-artifact-binding-test-'));
 try{
  const local=join(temp,'local'),download=join(temp,'download');mkdirSync(local);mkdirSync(download);
  for(const directory of [local,download]){writeFileSync(join(directory,'app'),'verified binary');writeFileSync(join(directory,'report.json'),'verified report');}
  assertDownloadedTree(local,download);
  writeFileSync(join(local,'app'),'changed binary');writeFileSync(join(local,'report.json'),'self-authored matching hash claim');
  assert.throws(()=>assertDownloadedTree(local,download),/differs from the freshly downloaded CI artifact/);
  writeFileSync(join(local,'app'),'verified binary');writeFileSync(join(local,'report.json'),'verified report');writeFileSync(join(local,'stale-extra'),'extra');
  assert.throws(()=>assertDownloadedTree(local,download));
 }finally{rmSync(temp,{recursive:true,force:true});}
});
