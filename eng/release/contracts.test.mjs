import { test, expect } from 'bun:test';
import { authority, json, sha256, validateCandidate, dependencyOrder, VERSION, REPOSITORY } from './artifacts.mjs';
function candidate() {
 const body={schema:'runic.preview/1',version:VERSION,repository:REPOSITORY,source:'a'.repeat(40),ciRunId:'123',packages:[{name:'A',file:'nuget/A.nupkg',sha256:'b'.repeat(64),dependencies:{}}]};
 return {...body,digest:sha256(JSON.stringify(body))};
}
test('authority refuses historical inventory and mixed versions',()=>{
 expect(()=>authority({version:'1.0.0-preview.1',nuget:[],npm:[]})).toThrow();
 expect(()=>authority({version:VERSION,nuget:[],npm:[]})).toThrow();
});
test('candidate rejects changed bytes/metadata',()=>{const c=candidate();validateCandidate(c);c.source='c'.repeat(40);expect(()=>validateCandidate(c)).toThrow();});
test('dependencies publish first and cycles fail',()=>{
 const a={name:'A',dependencies:{B:VERSION}}, b={name:'B',dependencies:{}};
 expect(dependencyOrder([a,b]).map(x=>x.name)).toEqual(['B','A']);b.dependencies.A=VERSION;expect(()=>dependencyOrder([a,b])).toThrow();
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

test('packed READMEs with a main-branch link are rejected',()=>{
 const dir=mkdtempSync(join(tmpdir(),'runic-readme-test-')), source='a'.repeat(40);
 const inventory=[{registry:'npm',name:'@runic-artifex/test'},{registry:'nuget',name:'Runic.Test'}];
 mkdirSync(join(dir,'npm'));mkdirSync(join(dir,'nuget'));
 const npm={name:inventory[0].name,version:VERSION,repository:`https://github.com/${REPOSITORY}`,gitHead:source};
 const script=`import io,json,tarfile,zipfile,sys\nd,p,v,s,r,npm_readme,nuget_readme=sys.argv[1:]\nwith tarfile.open(d+'/npm/test.tgz','w:gz') as t:\n for n,b in [('package/package.json',p.encode()),('package/README.md',npm_readme.encode())]:\n  i=tarfile.TarInfo(n);i.size=len(b);t.addfile(i,io.BytesIO(b))\nwith zipfile.ZipFile(d+'/nuget/test.nupkg','w') as z:\n z.writestr('Runic.Test.nuspec','<package><metadata><id>Runic.Test</id><version>'+v+'</version><readme>README.md</readme><repository url="https://github.com/'+r+'" commit="'+s+'" /></metadata></package>')\n z.writestr('README.md',nuget_readme)\n`;
 const pack=(npmReadme,nugetReadme)=>execFileSync('python3',['-c',script,dir,JSON.stringify(npm),VERSION,source,REPOSITORY,npmReadme,nugetReadme]);
 const tagged=`https://github.com/${REPOSITORY}/blob/v${VERSION}/README.md`, main=`https://github.com/${REPOSITORY}/blob/main/README.md`;
 try {
  pack(tagged,tagged);expect(scan(dir,inventory,source).map(p=>Object.hasOwn(p,'readme'))).toEqual([false,false]);
  pack(main,tagged);expect(()=>scan(dir,inventory,source)).toThrow('npm/test.tgz README links to a main branch');
  pack(tagged,'https://github.com/Runic-Artifex/runic-cli-sdk/tree/main/docs');expect(()=>scan(dir,inventory,source)).toThrow('nuget/test.nupkg README links to a main branch');
 } finally {rmSync(dir,{recursive:true,force:true});}
});

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
import { fetchRegistry, registryMatches, nugetLists, waitForNuget } from './registry.mjs';
test('registry polling retries availability and transient status with Retry-After', async()=>{
 const statuses=[404,429,503,200], delays=[];let time=0;
 const response=await fetchRegistry('https://registry.npmjs.org/test',{
  waitForAvailability:true, now:()=>time, sleep:async ms=>{delays.push(ms);time+=ms;},
  fetchImpl:async()=>new Response('',{status:statuses.shift(),headers:{'retry-after':'2'}})
 });
 expect(response.status).toBe(200);expect(delays).toEqual([2000,2000,2000]);
 const waiting=[401,404,200];
 expect((await fetchRegistry('https://registry.npmjs.org/-/package/new/dist-tags',{waitForAvailability:true,now:()=>0,sleep:async()=>{},
  fetchImpl:async()=>new Response('',{status:waiting.shift()})})).status).toBe(200);
 expect((await fetchRegistry('https://registry.npmjs.org/test',{fetchImpl:async()=>new Response('',{status:401})})).status).toBe(401);
 let attempts=0;
 expect((await fetchRegistry('https://registry.npmjs.org/test',{fetchImpl:async()=>{attempts++;return new Response('',{status:404});}})).status).toBe(404);
 expect(attempts).toBe(1);
});
test('registry polling is bounded and never retries mismatching published bytes', async()=>{
 let attempts=0;
 const response=await fetchRegistry('https://registry.npmjs.org/test',{waitForAvailability:true,maxAttempts:3,now:()=>0,sleep:async()=>{},fetchImpl:async()=>{attempts++;return new Response('',{status:404});}});
 expect(response.status).toBe(404);expect(attempts).toBe(3);
 await expect(fetchRegistry('https://registry.npmjs.org/test',{now:()=>0,budgetMs:1000,fetchImpl:async()=>new Response('',{status:429,headers:{'retry-after':'60'}})})).rejects.toThrow('Retry-After');
 let reads=0; const p={name:'@runic-artifex/test',registry:'npm',version:VERSION,sha256:sha256('correct')};
 await expect(registryMatches(p,{waitForAvailability:true,fetchImpl:async()=>{
  reads++;return reads===1 ? Response.json({name:p.name,version:VERSION,dist:{tarball:'https://registry.npmjs.org/test.tgz'}}) : new Response('wrong');
 }})).rejects.toThrow('Registry bytes differ');
 expect(reads).toBe(2);
});
test('registry polling retries thrown timeouts and network errors within its limits', async()=>{
 const timeout=()=>new DOMException('The operation timed out.','TimeoutError');
 let attempts=0, delays=[];
 const response=await fetchRegistry('https://api.nuget.org/v3-flatcontainer/test/index.json',{now:()=>0,sleep:async ms=>{delays.push(ms);},
  fetchImpl:async()=>{if(attempts++===0) throw timeout(); return new Response('',{status:200});}});
 expect(response.status).toBe(200);expect(attempts).toBe(2);expect(delays).toEqual([1000]);
 attempts=0;
 await expect(fetchRegistry('https://api.nuget.org/test',{maxAttempts:3,now:()=>0,sleep:async()=>{},
  fetchImpl:async()=>{attempts++;throw timeout();}})).rejects.toThrow('timed out');
 expect(attempts).toBe(3);
 attempts=0;let time=0;
 await expect(fetchRegistry('https://api.nuget.org/test',{budgetMs:5000,now:()=>time,sleep:async ms=>{time+=ms;},
  fetchImpl:async()=>{attempts++;throw new TypeError('fetch failed');}})).rejects.toThrow('fetch failed');
 expect(attempts).toBe(3);
 attempts=0;
 await expect(fetchRegistry('https://api.nuget.org/test',{now:()=>0,sleep:async()=>{},
  fetchImpl:async()=>{attempts++;throw new Error('bug');}})).rejects.toThrow('bug');
 expect(attempts).toBe(1);
});

test('after publication, the release waits until nuget.org lists every NuGet package version (#337)', async()=>{
 const urls=[];
 const listing=versions=>async url=>{urls.push(url);return versions===null ? new Response('',{status:404}) : Response.json({versions});};
 expect(await nugetLists('Runic.Desktop','0.8.0-Preview.4',{fetchImpl:listing(['0.8.0-preview.3','0.8.0-preview.4'])})).toBe(true);
 expect(urls).toEqual(['https://api.nuget.org/v3-flatcontainer/runic.desktop/index.json']);
 expect(await nugetLists('Runic.Desktop','0.8.0-preview.4',{fetchImpl:listing(['0.8.0-preview.3'])})).toBe(false);
 expect(await nugetLists('Runic.New','0.8.0-preview.4',{fetchImpl:listing(null)})).toBe(false);
 await expect(nugetLists('Runic.Desktop','0.8.0-preview.4',{fetchImpl:async()=>new Response('',{status:500})})).rejects.toThrow('NuGet version list failed');
 const packages=[{registry:'nuget',name:'Runic.A',version:'1.0.0'},{registry:'nuget',name:'Runic.B',version:'1.0.0'},{registry:'npm',name:'@runic-artifex/a',version:'1.0.0'}];
 // Runic.A is listed from the first poll, Runic.B from the third; npm packages are not polled, and a listed package is not asked again.
 let time=0;const asked=[],logs=[],sleeps=[];
 await waitForNuget(packages,{now:()=>time,sleep:async ms=>{sleeps.push(ms);time+=ms;},log:line=>logs.push(line),intervalMs:1000,timeoutMs:10000,
  lists:async name=>{asked.push(name);return name==='Runic.A' || sleeps.length>=2;}});
 expect(asked).toEqual(['Runic.A','Runic.B','Runic.B','Runic.B']);
 expect(sleeps).toEqual([1000,1000]);
 expect(logs[0]).toBe('Waiting for nuget.org to list 1 package(s): Runic.B 1.0.0');
 time=0;
 await expect(waitForNuget(packages,{now:()=>time,sleep:async ms=>{time+=ms;},log:()=>{},intervalMs:30000,timeoutMs:120000,lists:async name=>name==='Runic.A'}))
  .rejects.toThrow('nuget.org still does not list Runic.B 1.0.0 after 2 minutes');
 expect(time).toBeLessThan(120000);
});

import { runChecked } from './process.mjs';
test('subprocess failures do not expose argv or underlying process errors',()=>{
 const secret='test-secret-never-log';
 try {runChecked('dotnet',['nuget','push','file','--api-key',secret],{},()=>({status:1,error:new Error(`failed --api-key ${secret}`)}));throw Error('Expected failure');}
 catch(error) {expect(String(error)).not.toContain(secret);expect(error.message).toContain('Release subprocess failed');expect(error.cause).toBeUndefined();}
});

import { npmLatest } from './registry.mjs';
test('a dry run treats an npm package that does not exist yet as having no latest', async()=>{
 const urls=[];
 const fetchImpl=async url=>{urls.push(url);return url.includes('/dist-tags') ? new Response('',{status:401}) : new Response('',{status:404});};
 expect(await npmLatest('@runic-artifex/new',{fetchImpl})).toBeUndefined();
 expect(urls).toEqual(['https://registry.npmjs.org/%40runic-artifex%2Fnew']);
 const published=async url=>url.includes('/dist-tags') ? Response.json({latest:'0.6.0-preview.1'}) : Response.json({});
 expect(await npmLatest('@runic-artifex/views',{fetchImpl:published})).toBe('0.6.0-preview.1');
 await expect(npmLatest('@runic-artifex/new',{fetchImpl:async()=>new Response('',{status:401}),waitForAvailability:true,maxAttempts:1})).rejects.toThrow('401');
});

test('a package on its own version is scanned at that version and dependents pin it',()=>{
 const workspace=json(new URL('../workspace.json',import.meta.url));
 const cswebui=authority(workspace).find(p=>p.name==='Runic.Application.Views.CsWebUi');
 expect(cswebui.version).toBe(workspace.nuget.find(p=>p.name===cswebui.name).version ?? VERSION);
 expect(authority({...workspace,nuget:workspace.nuget.map(p=>p.name===cswebui.name?{...p,version:'9.0.0-preview.1'}:p)})
  .find(p=>p.name===cswebui.name).version).toBe('9.0.0-preview.1');
 const dir=mkdtempSync(join(tmpdir(),'runic-own-version-test-')), source='a'.repeat(40), own='9.0.0-preview.1';
 const inventory=[{registry:'nuget',name:'Runic.Own',version:own},{registry:'nuget',name:'Runic.Consumer',version:VERSION}];
 mkdirSync(join(dir,'npm'));mkdirSync(join(dir,'nuget'));
 const script=`import zipfile,sys,xml.etree.ElementTree as E
d,r,s,own_version,consumer_version,range=sys.argv[1:]
for name,v in [('Runic.Own',own_version),('Runic.Consumer',consumer_version)]:
 root=E.Element('package');m=E.SubElement(root,'metadata')
 E.SubElement(m,'id').text=name;E.SubElement(m,'version').text=v
 E.SubElement(m,'repository',url='https://github.com/'+r,commit=s)
 if name=='Runic.Consumer':E.SubElement(E.SubElement(m,'dependencies'),'dependency',id='Runic.Own',version=range)
 with zipfile.ZipFile(d+'/nuget/'+name+'.nupkg','w') as z:z.writestr(name+'.nuspec',E.tostring(root))
`;
 const pack=(ownVersion,range)=>execFileSync('python3',['-c',script,dir,REPOSITORY,source,ownVersion,VERSION,range]);
 try {
  pack(own,`[${own}]`);
  expect(scan(dir,inventory,source).map(p=>[p.name,p.version])).toEqual([['Runic.Own',own],['Runic.Consumer',VERSION]]);
  pack(VERSION,`[${own}]`);expect(()=>scan(dir,inventory,source)).toThrow('Mixed version Runic.Own');
  pack(own,`[${VERSION}]`);expect(()=>scan(dir,inventory,source)).toThrow('Internal nuget dependency must pin candidate Runic.Own');
 } finally {rmSync(dir,{recursive:true,force:true});}
});

test('registry lookups use the version each package ships on', async()=>{
 const urls=[];const p={name:'Runic.Own',registry:'nuget',version:'9.0.0-Preview.1'};
 expect(await registryMatches(p,{fetchImpl:async url=>{urls.push(url);return new Response('',{status:404});}})).toBe(false);
 expect(urls).toEqual(['https://api.nuget.org/v3-flatcontainer/runic.own/9.0.0-preview.1/runic.own.9.0.0-preview.1.nupkg']);
 const npm={name:'@runic-artifex/own',registry:'npm',version:'9.0.0-preview.1'};urls.length=0;
 expect(await registryMatches(npm,{fetchImpl:async url=>{urls.push(url);return new Response('',{status:404});}})).toBe(false);
 expect(urls).toEqual(['https://registry.npmjs.org/%40runic-artifex%2Fown/9.0.0-preview.1']);
});
