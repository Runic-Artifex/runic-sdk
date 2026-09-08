import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compare, validate, checkSoak } from './metrics.mjs';
import { descendants, trackedProcesses, trackProcesses } from './process-tree.mjs';
function receipt(value=100) {return {schema:'runic.reliability/1',status:'passed',sourceRevision:'a'.repeat(40),artifacts:{app:'b'.repeat(64)},environment:{machine:'test',memoryMetric:'rss'},workload:'fixed',profile:'default',samples:Array.from({length:15},()=>({startupMs:value,peakTreeBytes:value,durationMs:1,exitCode:0,remainingProcesses:0}))};}
const cycle=()=>({operations:{window:true,reconnect:true,cancellation:true},resources:{leases:0,transactions:0,presentations:0,pendingOperations:0},remainingProcesses:0,treeBytes:100});
test('comparison rejects reproducible >20% regressions and accepts boundary',()=>{assert.equal(compare(receipt(),receipt(121)).passed,false);assert.equal(compare(receipt(),receipt(120)).passed,true);});
test('single slow sample is not reproducible regression',()=>{const c=receipt();c.samples[0].startupMs=10000;assert.equal(compare(receipt(),c).passed,true);});
test('provider delta reported without applying equivalent-configuration gate',()=>{const c=receipt(200);c.profile='native';assert.equal(compare(receipt(),c,true).passed,null);assert.throws(()=>compare(receipt(),c));});
test('incomplete, failed, and mismatched receipts rejected',()=>{for(const mutate of [x=>x.status='failed',x=>x.samples.pop()&&x.samples.splice(0,6),x=>x.sourceRevision='unknown',x=>x.artifacts={},x=>x.samples[0].remainingProcesses=1,x=>x.samples[0].peakTreeBytes=NaN]){const c=receipt();mutate(c);assert.throws(()=>validate(c));}const c=receipt();c.environment.machine='other';assert.throws(()=>compare(receipt(),c));});
test('soak requires elapsed time, actual operations, zero leaks, and stable memory',()=>{let samples=Array.from({length:60},cycle);assert.equal(checkSoak(samples,7200000).passed,true);assert.throws(()=>checkSoak(samples,7199999));samples[1].operations.cancellation=false;assert.throws(()=>checkSoak(samples,7200000));samples=Array.from({length:60},cycle);samples[0].resources.leases=1;assert.throws(()=>checkSoak(samples,7200000));samples=Array.from({length:60},(_,i)=>({...cycle(),treeBytes:i<30?100:150}));assert.throws(()=>checkSoak(samples,7200000));});
test('tree follows indirect descendants without unrelated processes',()=>assert.deepEqual(descendants([{pid:1,parent:0},{pid:4,parent:3},{pid:3,parent:1},{pid:5,parent:2}],1).map(x=>x.pid),[1,4,3]));

test("soak rejects sustained growth below startup regression threshold",()=>assert.throws(()=>checkSoak(Array.from({length:60},(_,i)=>({...cycle(),treeBytes:100+i/10})),7200000)));

test("PID reuse cannot classify unrelated process as owned",()=>{const known=new Map();trackProcesses([{pid:10,identity:"before"}],known);assert.deepEqual(trackedProcesses([{pid:10,identity:"after"}],known),[]);assert.equal(trackedProcesses([{pid:10,identity:"before"}],known).length,1);assert.throws(()=>trackProcesses([{pid:11}],known));});

test("reused root PID cannot adopt a new unrelated process tree",()=>assert.deepEqual(descendants([{pid:1,parent:0,identity:"new"},{pid:2,parent:1,identity:"child"}],1,"old"),[]));
