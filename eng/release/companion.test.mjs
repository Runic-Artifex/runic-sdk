import { test, expect } from 'bun:test';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, existsSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fetchCompanion } from './companion.mjs';
import { REPOSITORY, sha256 } from './artifacts.mjs';

function fixture(content = '{"status":"failed"}\n') {
  const bytes=Buffer.from(content), sha=createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex');
  const commit='a'.repeat(40), tree='b'.repeat(40), prefix=`repos/${REPOSITORY}/git`;
  const responses={
    [`${prefix}/commits/${commit}`]:{sha:commit,tree:{sha:tree}},
    [`${prefix}/trees/${tree}`]:{sha:tree,truncated:false,tree:[{path:'native-soak.json',type:'blob',mode:'100644',size:bytes.length,sha}]},
    [`${prefix}/blobs/${sha}`]:{sha,size:bytes.length,encoding:'base64',content:bytes.toString('base64')},
  };
  const evidence={schema:'runic.preview-evidence/1',receipts:[{gate:'soak-thirty-minutes',outcome:'known-issue',waiver:{schema:'runic.soak-known-issue/1',rawReceiptFile:'native-soak.json',rawReceiptSha256:sha256(bytes)}}]};
  const calls=[], api=path=>{calls.push(path);expect(responses[path]).toBeDefined();return responses[path];};
  return {bytes,commit,tree,sha,evidence,responses,calls,api,prefix};
}
function inDirectory(run) {
  const parent=mkdtempSync(join(tmpdir(),'runic-companion-fetch-')),output=join(parent,'new');
  try {run(output);} finally {rmSync(parent,{recursive:true,force:true});}
}
test('immutable same-repository data transport preserves bytes larger than the envelope cap',()=>inDirectory(output=>{
  const f=fixture(JSON.stringify({status:'failed',padding:'x'.repeat(1100000)})+'\n');
  const result=fetchCompanion(f.evidence,f.commit,output,f.api);
  expect(readFileSync(join(output,'native-soak.json'))).toEqual(f.bytes);
  expect(JSON.parse(readFileSync(join(output,'transport.json'),'utf8'))).toMatchObject({repository:REPOSITORY,evidenceCommit:f.commit,blob:f.sha,sha256:sha256(f.bytes)});
  expect(result.fetched).toBe(true);expect(f.calls).toHaveLength(3);
  expect(()=>fetchCompanion(f.evidence,f.commit,output,f.api)).toThrow();
}));
test('empty commit is a no-op only without known-issue evidence',()=>inDirectory(output=>{
  let calls=0;const api=()=>{calls++;throw Error('unexpected');};
  expect(fetchCompanion({schema:'runic.preview-evidence/1',receipts:[]},'',output,api)).toEqual({fetched:false});
  expect(calls).toBe(0);expect(existsSync(output)).toBe(false);
  expect(()=>fetchCompanion(fixture().evidence,'',output,api)).toThrow();
  expect(()=>fetchCompanion({schema:'runic.preview-evidence/1',receipts:[]},'a'.repeat(40),output,api)).toThrow();
}));
test('rejects paths, mutable refs, wrong modes, oversized content and mismatched hashes before writing',()=>{
  for(const mutate of [
    f=>f.commit='main',f=>f.commit='https://example.invalid/file',
    f=>f.evidence.receipts[0].waiver.rawReceiptFile='../native-soak.json',
    f=>f.evidence.receipts[0].waiver.rawReceiptSha256='f'.repeat(64),
    f=>f.responses[`${f.prefix}/commits/${f.commit}`].sha='f'.repeat(40),
    f=>f.responses[`${f.prefix}/trees/${f.tree}`].truncated=true,
    f=>f.responses[`${f.prefix}/trees/${f.tree}`].tree.push({path:'script.sh'}),
    f=>f.responses[`${f.prefix}/trees/${f.tree}`].tree[0].mode='120000',
    f=>f.responses[`${f.prefix}/trees/${f.tree}`].tree[0].mode='100755',
    f=>f.responses[`${f.prefix}/trees/${f.tree}`].tree[0].type='commit',
    f=>f.responses[`${f.prefix}/trees/${f.tree}`].tree[0].size=64*1024*1024+1,
    f=>f.responses[`${f.prefix}/blobs/${f.sha}`].content=Buffer.from('different bytes').toString('base64'),
    f=>f.responses[`${f.prefix}/blobs/${f.sha}`].content+='!',
    f=>f.responses[`${f.prefix}/blobs/${f.sha}`].encoding='utf8',
  ]) inDirectory(output=>{const f=fixture();mutate(f);expect(()=>fetchCompanion(f.evidence,f.commit,output,f.api)).toThrow();expect(existsSync(output)).toBe(false);});
});
