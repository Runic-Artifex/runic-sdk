import { test, expect } from 'bun:test';
import { authority, sha256, validateCandidate, dependencyOrder, VERSION, REPOSITORY } from './artifacts.mjs';
import { requiredGates, verifyGates } from './gates.mjs';
import { verifyRun, expectedCIJobs, repositoryEndpoint } from './ci.mjs';
test('GitHub repository lookup uses its canonical route without a trailing slash',()=>{
 expect(repositoryEndpoint('')).toBe(`repos/${REPOSITORY}`);
 expect(repositoryEndpoint('actions/runs/123/jobs?per_page=100&page=2')).toBe(`repos/${REPOSITORY}/actions/runs/123/jobs?per_page=100&page=2`);
});
function candidate() {
 const body={schema:'runic.preview/1',version:VERSION,repository:REPOSITORY,source:'a'.repeat(40),ciRunId:'123',packages:[{name:'A',file:'nuget/A.nupkg',sha256:'b'.repeat(64),dependencies:{}}]};
 return {...body,digest:sha256(JSON.stringify(body))};
}
function receipts(c) { return requiredGates.map(gate=>({gate,outcome:'pass',candidateDigest:c.digest,source:c.source,artifactHashes:{'nuget/A.nupkg':'b'.repeat(64)},environment:'test fixture only',scenario:'synthetic unit test',validator:gate,evidence:'unit fixture',recordedAt:'2026-09-08T12:00:00Z',actualNativeInteraction:true,durationSeconds:7200,baselineCommit:'5afb8b8d',matchedMachineAndWorkload:true,unresolvedRegressionsAbove20Percent:0,keyboard:'pass',screenReader:'pass',focusRestoration:'pass',ime:'pass',highContrast:'pass',displayScaling:'pass'})); }
test('authority refuses historical inventory and mixed versions',()=>{
 expect(()=>authority({version:'1.0.0-preview.1',nuget:[],npm:[]})).toThrow();
 expect(()=>authority({version:VERSION,nuget:[],npm:[]})).toThrow();
});
test('candidate rejects changed bytes/metadata',()=>{const c=candidate();validateCandidate(c);c.source='c'.repeat(40);expect(()=>validateCandidate(c)).toThrow();});
test('dependencies publish first and cycles fail',()=>{
 const a={name:'A',dependencies:{B:VERSION}}, b={name:'B',dependencies:{}};
 expect(dependencyOrder([a,b]).map(x=>x.name)).toEqual(['B','A']);b.dependencies.A=VERSION;expect(()=>dependencyOrder([a,b])).toThrow();
});
test('all receipts required and bound to actual candidate',()=>{
 const c=candidate(),r=receipts(c);verifyGates(c,r);
 for(let i=0;i<r.length;i++) expect(()=>verifyGates(c,r.filter((_,j)=>j!==i))).toThrow();
 r[0].artifactHashes={};expect(()=>verifyGates(c,r)).toThrow();
});
test('human simulation, duplicate validators and short soak fail',()=>{
 const c=candidate();
 for(const [gate,key,value] of [['interactive-native-windows','actualNativeInteraction',false],['soak-two-hours','durationSeconds',7199],['accessibility-nvda','ime','pending'],['performance-matched-baseline','unresolvedRegressionsAbove20Percent',1],['pilot-2','validator','pilot-1']]) {
  const r=receipts(c);r.find(x=>x.gate===gate)[key]=value;expect(()=>verifyGates(c,r)).toThrow();
 }
});
test('full CI must belong to frozen commit and exact workflow with every job successful',()=>{
 const c=candidate(),run={head_branch:'main',id:123,head_sha:c.source,repository:{full_name:REPOSITORY},head_repository:{full_name:REPOSITORY},path:'.github/workflows/ci.yml',status:'completed',conclusion:'success',event:'push'};
 const jobs=[{name:'verify',conclusion:'success'}],artifacts=[{name:'runic-sdk-123',expired:false}];
 const authority={defaultBranch:'main',headSha:c.source,expectedJobs:['verify']};
 verifyRun(c,run,jobs,artifacts,authority);
 expect(()=>verifyRun(c,run,jobs,artifacts,{...authority,headSha:'f'.repeat(40)})).toThrow();
 expect(()=>verifyRun(c,run,jobs,artifacts,{...authority,expectedJobs:['verify','Native / osx-arm64']})).toThrow();
 for(const patch of [{head_branch:'feature'}, {head_sha:'d'.repeat(40)},{conclusion:'failure'},{path:'.github/workflows/publish-preview.yml'},{event:'pull_request'}]) expect(()=>verifyRun(c,{...run,...patch},jobs,artifacts,authority)).toThrow();
 expect(()=>verifyRun(c,run,[...jobs,{name:'native',conclusion:'skipped'}],artifacts,authority)).toThrow();
 expect(()=>verifyRun(c,run,jobs,[{...artifacts[0],expired:true}],authority)).toThrow();
});

import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { execFileSync } from 'node:child_process';
import { scan } from './artifacts.mjs';
test('real archives reject stale extras, source, metadata and internal dependency leaks',()=>{
 const dir=mkdtempSync(join(tmpdir(),'runic-preview-test-')), source='a'.repeat(40);
 const inventory=[{registry:'npm',name:'@runic-artifex/test'},{registry:'nuget',name:'Runic.Test'}];
 mkdirSync(join(dir,'npm'));mkdirSync(join(dir,'nuget'));
 const npm={name:inventory[0].name,version:VERSION,repository:`https://github.com/${REPOSITORY}`,gitHead:source,dependencies:{'Runic.Test':VERSION}};
 const script=`import io,json,tarfile,zipfile,sys\nd,p,v,s,r=sys.argv[1:]\nb=p.encode()\nwith tarfile.open(d+'/npm/test.tgz','w:gz') as t:\n i=tarfile.TarInfo('package/package.json');i.size=len(b);t.addfile(i,io.BytesIO(b))\nwith zipfile.ZipFile(d+'/nuget/test.nupkg','w') as z:\n z.writestr('Runic.Test.nuspec','<package><metadata><id>Runic.Test</id><version>'+v+'</version><repository url="https://github.com/'+r+'" commit="'+s+'" /></metadata></package>')\n`;
 const pack=()=>execFileSync('python3',['-c',script,dir,JSON.stringify(npm),VERSION,source,REPOSITORY]);
 try {
  pack();expect(scan(dir,inventory,source).map(p=>p.name)).toEqual(['Runic.Test','@runic-artifex/test']);
  for(const [key,value] of [['version','0.1.0'],['gitHead','b'.repeat(40)],['repository','https://github.com/obsolete/repo']]) {
   const original=npm[key];npm[key]=value;pack();expect(()=>scan(dir,inventory,source)).toThrow();npm[key]=original;
  }
  npm.dependencies['Runic.Test']='workspace:*';pack();expect(()=>scan(dir,inventory,source)).toThrow();
  npm.dependencies['Runic.Test']=VERSION;pack();writeFileSync(join(dir,'nuget','stale.txt'),'stale');expect(()=>scan(dir,inventory,source)).toThrow();
 } finally {rmSync(dir,{recursive:true,force:true});}
});

import { acceptancePolicy } from './policy.mjs';
test('real package dependencies require registry-specific exact preview ranges',()=>{
 const dir=mkdtempSync(join(tmpdir(),'runic-exact-dependency-test-')), source='a'.repeat(40);
 const inventory=[
  {registry:'nuget',name:'Runic.Core'},{registry:'nuget',name:'Runic.Consumer'},
  {registry:'npm',name:'@runic-artifex/core'},{registry:'npm',name:'@runic-artifex/consumer'}
 ];
 mkdirSync(join(dir,'npm'));mkdirSync(join(dir,'nuget'));
 const script=`import io,json,tarfile,zipfile,sys,xml.etree.ElementTree as E
d,v,s,r,nuget_range,npm_range=sys.argv[1:]
for name in ['Runic.Core','Runic.Consumer']:
 root=E.Element('package');m=E.SubElement(root,'metadata')
 E.SubElement(m,'id').text=name;E.SubElement(m,'version').text=v
 E.SubElement(m,'repository',url='https://github.com/'+r,commit=s)
 if name=='Runic.Consumer':E.SubElement(E.SubElement(m,'dependencies'),'dependency',id='Runic.Core',version=nuget_range)
 with zipfile.ZipFile(d+'/nuget/'+name+'.nupkg','w') as z:z.writestr(name+'.nuspec',E.tostring(root))
for name in ['core','consumer']:
 p=dict(name='@runic-artifex/'+name,version=v,repository='https://github.com/'+r,gitHead=s)
 if name=='consumer':p['dependencies']={'@runic-artifex/core':npm_range}
 b=json.dumps(p).encode()
 with tarfile.open(d+'/npm/'+name+'.tgz','w:gz') as t:
  i=tarfile.TarInfo('package/package.json');i.size=len(b);t.addfile(i,io.BytesIO(b))
`;
 const pack=(nugetRange,npmRange)=>execFileSync('python3',['-c',script,dir,VERSION,source,REPOSITORY,nugetRange,npmRange]);
 try {
  pack(`[${VERSION}]`,VERSION);
  expect(scan(dir,inventory,source)).toHaveLength(4);
  for(const range of [VERSION,`[${VERSION},)`,`[${VERSION},0.3.0)`,`[0.1.0-preview.1]`]) {
   pack(range,VERSION);
   expect(()=>scan(dir,inventory,source)).toThrow('Internal nuget dependency must pin candidate');
  }
  for(const range of [`[${VERSION}]`,`^${VERSION}`,`~${VERSION}`,`>=${VERSION}`,'*','0.1.0-preview.1']) {
   pack(`[${VERSION}]`,range);
   expect(()=>scan(dir,inventory,source)).toThrow('Internal npm dependency must pin candidate');
  }
 } finally {rmSync(dir,{recursive:true,force:true});}
});
function demoEvidence() {
 const original=candidate(), {digest,...body}=original;
 body.acceptancePolicy=acceptancePolicy('demo-preview');
 const c={...body,digest:sha256(JSON.stringify(body))};
 const full=receipts(c), base=full[0];
 const r=c.acceptancePolicy.required.map(gate=>full.find(r=>r.gate===gate) ?? {...base,gate,actualNativeInteraction:true});
 return [c,{schema:'runic.preview-evidence/1',policy:structuredClone(c.acceptancePolicy),receipts:r}];
}
test('demo policy keeps all automated gates and explicitly defers broader human scope',()=>{
 const [c,e]=demoEvidence();verifyGates(c,e);
 expect(c.acceptancePolicy.deferred.every(d=>d.outcome==='deferred')).toBe(true);
 expect(c.acceptancePolicy.required.filter(g=>g.startsWith('native-')).length).toBe(6);
 for(const gate of ['interactive-native-linux-local','interactive-native-windows-vm','native-aot-osx-arm64','performance-matched-baseline','soak-two-hours']) {
  const copy=structuredClone(e);copy.receipts=copy.receipts.filter(r=>r.gate!==gate);expect(()=>verifyGates(c,copy)).toThrow();
 }
 expect(()=>verifyGates(c,e.receipts)).toThrow();
});
test('demo policy rejects fabricated deferred passes and changed scope',()=>{
 let [c,e]=demoEvidence();e.receipts.push({...e.receipts[0],gate:'pilot-1'});expect(()=>verifyGates(c,e)).toThrow();
 [c,e]=demoEvidence();e.policy.deferred[0].outcome='pass';expect(()=>verifyGates(c,e)).toThrow();
 [c,e]=demoEvidence();e.policy.required=e.policy.required.filter(g=>g!=='soak-two-hours');expect(()=>verifyGates(c,e)).toThrow();
 expect(()=>acceptancePolicy('skip-everything')).toThrow();
});

import { fetchRegistry, registryMatches } from './registry.mjs';
test('registry polling retries availability and transient status with Retry-After', async()=>{
 const statuses=[404,429,503,200], delays=[];let time=0;
 const response=await fetchRegistry('https://registry.npmjs.org/test',{
  waitForAvailability:true, now:()=>time, sleep:async ms=>{delays.push(ms);time+=ms;},
  fetchImpl:async()=>new Response('',{status:statuses.shift(),headers:{'retry-after':'2'}})
 });
 expect(response.status).toBe(200);expect(delays).toEqual([2000,2000,2000]);
 let attempts=0;
 expect((await fetchRegistry('https://registry.npmjs.org/test',{fetchImpl:async()=>{attempts++;return new Response('',{status:404});}})).status).toBe(404);
 expect(attempts).toBe(1);
});
test('registry polling is bounded and never retries mismatching published bytes', async()=>{
 let attempts=0;
 const response=await fetchRegistry('https://registry.npmjs.org/test',{waitForAvailability:true,maxAttempts:3,now:()=>0,sleep:async()=>{},fetchImpl:async()=>{attempts++;return new Response('',{status:404});}});
 expect(response.status).toBe(404);expect(attempts).toBe(3);
 await expect(fetchRegistry('https://registry.npmjs.org/test',{now:()=>0,budgetMs:1000,fetchImpl:async()=>new Response('',{status:429,headers:{'retry-after':'60'}})})).rejects.toThrow('Retry-After');
 let reads=0; const p={name:'@runic-artifex/test',registry:'npm',sha256:sha256('correct')};
 await expect(registryMatches(p,{waitForAvailability:true,fetchImpl:async()=>{
  reads++;return reads===1 ? Response.json({name:p.name,version:VERSION,dist:{tarball:'https://registry.npmjs.org/test.tgz'}}) : new Response('wrong');
 }})).rejects.toThrow('Registry bytes differ');
 expect(reads).toBe(2);
});

import { runChecked } from './process.mjs';
test('subprocess failures do not expose argv or underlying process errors',()=>{
 const secret='test-secret-never-log';
 try {runChecked('dotnet',['nuget','push','file','--api-key',secret],{},()=>({status:1,error:new Error(`failed --api-key ${secret}`)}));throw Error('Expected failure');}
 catch(error) {expect(String(error)).not.toContain(secret);expect(error.message).toContain('Release subprocess failed');expect(error.cause).toBeUndefined();}
});
test('expected CI job inventory expands every matrix and rejects ambiguous names',()=>{
 const jobs={verify:{},native:{name:'Native / ${{ matrix.rid }}',strategy:{matrix:{include:[{rid:'win-x64'},{rid:'linux-x64'},{rid:'osx-arm64'}]}}},web:{name:'Web / ${{ matrix.package }}',strategy:{matrix:'${{ fromJSON(needs.build.outputs.web) }}'}}};
 expect(expectedCIJobs({jobs},[{package:'a'},{package:'b'}])).toEqual(['Native / linux-x64','Native / osx-arm64','Native / win-x64','Web / a','Web / b','verify']);
 delete jobs.native.name;expect(()=>expectedCIJobs({jobs},[{package:'a'}])).toThrow();
});
