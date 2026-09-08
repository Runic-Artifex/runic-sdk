import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { descendants, processTable, trackedProcesses, trackProcesses, waitForTrackedExit } from './process-tree.mjs';
export async function start(config) {
  const child=spawn(config.executable,[...(config.arguments??[]),'--soak'],{cwd:config.directory,env:{...process.env,...config.env},stdio:['pipe','pipe','pipe']});
  let failure, ended=false, stderr='', baselineCount, peak=0;
  const known=new Map(), lines=[], waiting=[];
  const input=createInterface({input:child.stdout});
  input.on('line',line=>{if(waiting.length)waiting.shift()(line);else lines.push(line);});
  child.stderr.on('data',x=>{stderr=(stderr+x).slice(-65536);});
  const exit=new Promise(accept=>{child.once('error',error=>{ended=true;failure=error;while(waiting.length)waiting.shift()(null);accept({code:-1,signal:null,error});});child.once('exit',(code,signal)=>{ended=true;failure=new Error(`Fixture exited ${code}/${signal}: ${stderr}`);while(waiting.length)waiting.shift()(null);accept({code,signal});});});
  const read=async prefix=>{while(true){if(ended)throw failure;const line=lines.length?lines.shift():await new Promise(r=>waiting.push(r));if(line===null)throw failure;if(line.startsWith(prefix))return line.slice(prefix.length);}};
  const collect=()=>{const table=processTable(),tree=descendants(table,child.pid,known.get(child.pid));trackProcesses(tree,known);const all=trackedProcesses(table,known);peak=Math.max(peak,all.reduce((s,p)=>s+p.bytes,0));return all;};
  let collectionError;
  const interval=setInterval(()=>{try{collect();}catch(error){collectionError=error;}},process.platform==='win32'?1000:100);
  return {
    async cycle(){
      await read('RUNIC_SOAK_READY'); peak=0;
      child.stdin.write('cycle\n');
      const result=JSON.parse(await read('RUNIC_SOAK_CYCLE '));
      // The retained host may retain a stable native helper pool. Growth is a failure.
      const current=collect(); if(collectionError)throw collectionError;
      baselineCount??=current.length;
      const growth=Math.max(0,current.length-baselineCount);
      return {...result,treeBytes:peak,remainingProcesses:growth,processCount:current.length,processes:current};
    },
    async stop(){
      const shutdownStarted=performance.now();
      try{
        if(!ended)child.stdin.end('stop\n');
        let timer;const result=await Promise.race([exit,new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('Fixture shutdown timeout')),15000);})]).finally(()=>clearTimeout(timer));
        if(result.error)throw result.error;
        assert.equal(result.code,0,stderr);assert.equal(result.signal,null);
        return await waitForTrackedExit(known,{timeoutMs:Math.max(0,15000-(performance.now()-shutdownStarted))});
      }finally{
        clearInterval(interval);input.close();
        if(!ended){child.kill('SIGKILL');await exit;}
        for(const p of trackedProcesses(processTable(),known))try{process.kill(p.pid,'SIGKILL');}catch{}
      }
    }
  };
}
