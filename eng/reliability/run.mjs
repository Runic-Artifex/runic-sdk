import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFileSync, readdirSync, writeFileSync, statSync } from 'node:fs';
import { resolve, relative } from 'node:path';
import { hostname, release, cpus } from 'node:os';
import { createHash } from 'node:crypto';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { performance } from 'node:perf_hooks';
import { compare, validate, checkSoak, checkCycle, minimumSoakDurationMs } from './metrics.mjs';
import { processTable, descendants, memoryMetric, trackedProcesses, trackProcesses } from './process-tree.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
export function artifactHashes(directory) {
  const result={};
  function visit(path){for(const entry of readdirSync(path,{withFileTypes:true})){
    const file=resolve(path,entry.name);
    assert.ok(!entry.isSymbolicLink(),'Published artifact tree must not contain symlinks');
    if(entry.isDirectory())visit(file);else if(entry.isFile())result[relative(directory,file).replaceAll('\\','/')]=hash(readFileSync(file));
  }} visit(directory); return result;
}
async function timeout(promise,ms,message){let timer;try{return await Promise.race([promise,new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error(message)),ms);})]);}finally{clearTimeout(timer);}}
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function startup(config) {
  const start=performance.now();
  const child=spawn(config.executable,config.arguments??['--serve'],{cwd:config.directory,env:{...process.env,...config.env},stdio:['pipe','pipe','pipe']});
  const known=new Map(); let peak=0, output='', collectorError;
  const collect=()=>{try {const tree=descendants(processTable(),child.pid,known.get(child.pid)); trackProcesses(tree,known); peak=Math.max(peak,tree.reduce((sum,x)=>sum+x.bytes,0));}catch(error){collectorError=error;}};
  const interval=setInterval(collect,process.platform==='win32'?1000:100);
  let exited=false, spawnError; const exit=new Promise(accept=>{child.once('error',error=>{spawnError=error;exited=true;accept({code:-1,signal:null});});child.once('exit',(code,signal)=>{exited=true;accept({code,signal});});});
  let ready;const readiness=new Promise(accept=>{ready=accept;});
  child.stdout.on('data',x=>{output=(output+x).slice(-65536);const url=output.match(/Customer editor: (https?:\/\/\S+)/)?.[1];if(url)ready(url);});child.stderr.on('data',x=>{output=(output+x).slice(-65536);});
  try {
    const url=await timeout(Promise.race([readiness,exit.then(()=>{throw new Error(`Host exited before ready: ${spawnError?.message ?? output}`);})]),30000,'Host readiness timeout');
    assert.equal(new URL(url).hostname,'127.0.0.1');
    const response=await fetch(url,{signal:AbortSignal.timeout(10000)});assert.equal(response.status,200);await response.arrayBuffer();
    const startupMs=performance.now()-start;
    for(let i=0;i<10;i++){const r=await fetch(url,{signal:AbortSignal.timeout(10000)});assert.equal(r.status,200);await r.arrayBuffer();collect();}
    await sleep(1000); collect(); if(collectorError)throw collectorError;
    child.stdin.end('stop\n');
    const result=await timeout(exit,10000,'Graceful shutdown timeout');
    assert.equal(result.code,0);assert.equal(result.signal,null);
    const remaining=trackedProcesses(processTable(),known);assert.equal(remaining.length,0,'Descendant survived host shutdown');
    return {startupMs,peakTreeBytes:peak,durationMs:performance.now()-start,exitCode:0,remainingProcesses:0};
  } finally {
    clearInterval(interval);
    if(!exited){child.kill('SIGKILL');await exit;}
    // Only processes observed as members of this task's child tree are terminated.
    for(const p of trackedProcesses(processTable(),known))try{process.kill(p.pid,'SIGKILL');}catch{}
  }
}
async function main() {
  const [mode,input,output,extra]=process.argv.slice(2);
  if(mode==='compare'){
    const result=compare(JSON.parse(readFileSync(input)),JSON.parse(readFileSync(output)),extra==='--provider');
    console.log(JSON.stringify(result,null,2));if(result.passed===false)process.exitCode=1;return;
  }
  assert.ok(mode==='measure'||mode==='soak','Use measure CONFIG OUTPUT, soak CONFIG OUTPUT, or compare BASELINE CANDIDATE [--provider]');
  const config=JSON.parse(readFileSync(input)); assert.ok(output,'Output receipt required');
  config.directory=resolve(config.directory);config.executable=resolve(config.executable);
  assert.ok(relative(config.directory,config.executable)&&!relative(config.directory,config.executable).startsWith('..'),'Executable must be inside immutable artifact directory');
  assert.ok(statSync(config.executable).isFile());
  const provenance=JSON.parse(readFileSync(config.provenance));
  assert.match(provenance.sourceRevision,/^[a-f0-9]{40}$/);
  const artifacts=artifactHashes(config.directory);
  assert.deepEqual(artifacts,provenance.artifacts,'Artifact bytes differ from build provenance');
  const environment={machine:hash(hostname()),platform:process.platform,architecture:process.arch,os:release(),cpu:cpus()[0]?.model,node:process.version,memoryMetric,
    display:process.env.WAYLAND_DISPLAY?'wayland':process.env.DISPLAY?'x11':'none',configuration:config.environment??{}};
  const receipt={schema:'runic.reliability/1',sourceRevision:provenance.sourceRevision,artifacts,provenanceSha256:hash(readFileSync(config.provenance)),environment,profile:config.profile,workload:'customer-serve-http-10-requests-v2',startedAt:new Date().toISOString(),samples:[]};
  const persist=()=>writeFileSync(output,JSON.stringify(receipt,null,2)+'\n');
  try{
    if(mode==='measure'){
      const count=config.samples??15;assert.ok(Number.isInteger(count)&&count>=10);
      for(let i=0;i<count;i++){receipt.samples.push(await startup(config));persist();console.error(`Startup sample ${i+1}/${count}`);}
      receipt.status='passed';validate(receipt);
    }else{
      assert.ok(config.adapter,'Soak needs an instrumented application adapter');
      const adapterPath=resolve(config.adapter), adapter=await import(pathToFileURL(adapterPath));
      receipt.adapterSha256=hash(readFileSync(adapterPath));receipt.schema='runic.reliability-soak/1';receipt.workload='window-reconnect-cancellation-v1';
      const start=performance.now();const duration=config.durationMs??minimumSoakDurationMs;
      assert.ok(Number.isFinite(duration)&&duration>=minimumSoakDurationMs,'Soak duration must be at least thirty minutes');
      receipt.requestedDurationMs=duration;
      const session=await adapter.start(config);
      const failures=[];
      try{while(performance.now()-start<duration){
        const s=await timeout(session.cycle(),60000,'Soak cycle exceeded 60 seconds');
        // Check every cycle immediately, including all leak counters; duration is checked at completion.
        checkCycle(s);
        receipt.samples.push(s);receipt.elapsedMs=performance.now()-start;persist();
      }}catch(error){failures.push(error);}
      try{receipt.shutdown=await session.stop();}catch(error){failures.push(error);}
      // Shutdown must not conceal independent resource or sustained-memory failures.
      try{receipt.result=checkSoak(receipt.samples,receipt.elapsedMs??0,duration);}catch(error){failures.push(error);}
      if(failures.length)throw new AggregateError(failures,failures.map(error=>error.message).join('\n'));
    }
    receipt.finalArtifactHashes=artifactHashes(config.directory);
    assert.deepEqual(receipt.finalArtifactHashes,artifacts,'Artifacts changed during measurement');
    receipt.status='passed';
  }catch(error){
    receipt.status='failed';receipt.failure=error.message;
    // A trend failure still needs proof that the measured binary bytes stayed fixed.
    try {
      receipt.finalArtifactHashes=artifactHashes(config.directory);
      assert.deepEqual(receipt.finalArtifactHashes,artifacts,'Artifacts changed during measurement');
    } catch(finalError) {
      receipt.failure += '\n' + finalError.message;
      throw new AggregateError([error,finalError],receipt.failure);
    }
    throw error;
  }finally{receipt.finishedAt=new Date().toISOString();persist();}
}
if(process.argv[1]&&resolve(process.argv[1])===resolve(fileURLToPath(import.meta.url)))main().catch(error=>{console.error(error);process.exitCode=1;});
