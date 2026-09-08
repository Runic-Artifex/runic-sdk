import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, mkdirSync, chmodSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { artifactHashes } from './run.mjs';
test('spawn failure produces a failed receipt without unhandled rejection', {skip:process.platform==='win32'},()=>{
 const directory=mkdtempSync(join(tmpdir(),'runic-reliability-test-'));
 try {
  const artifacts=join(directory,'published');mkdirSync(artifacts);const executable=join(artifacts,'app');writeFileSync(executable,'not executable');chmodSync(executable,0o600);
  const provenance=join(directory,'provenance.json');writeFileSync(provenance,JSON.stringify({sourceRevision:'a'.repeat(40),artifacts:artifactHashes(artifacts)}));
  for(const mode of ['measure','soak']){
   const config=join(directory,'config.json'),output=join(directory,'receipt.json');writeFileSync(config,JSON.stringify({directory:artifacts,executable,provenance,profile:'test',adapter:resolve('eng/reliability/native-soak-adapter.mjs')}));
   const result=spawnSync(process.execPath,[resolve('eng/reliability/run.mjs'),mode,config,output],{encoding:'utf8',timeout:10000});
   assert.equal(result.status,1,result.stderr);const receipt=JSON.parse(readFileSync(output));assert.equal(receipt.status,'failed');assert.match(receipt.failure,/EACCES/);assert.doesNotMatch(result.stderr,/UnhandledPromiseRejection/);
  }
 }finally{rmSync(directory,{recursive:true,force:true});}
});
