import test from 'node:test';
import assert from 'node:assert/strict';
import {descendants,trackProcesses,trackedProcesses} from './reliability/process-tree.mjs';

test('a stale Windows PPID cannot adopt an older unrelated service for cleanup',()=>{
 const tree=[
  {pid:2448,parent:15040,identity:'/Date(1788894014561)/'},
  {pid:14584,parent:2448,identity:'/Date(1788894210000)/'},
  {pid:6088,parent:14584,identity:'/Date(1788892241468)/'},
  {pid:6089,parent:6088,identity:'/Date(1788892241500)/'},
  {pid:16000,parent:14584,identity:'/Date(1788894210001)/'},
 ];
 const owned=descendants(tree,2448,tree[0].identity);
 assert.deepEqual(owned.map(p=>p.pid),[2448,14584,16000]);
 const known=new Map();trackProcesses(owned,known);
 assert.deepEqual(trackedProcesses(tree.filter(p=>[6088,6089].includes(p.pid)),known),[]);
});
test('absent root and changed root identity cannot adopt orphaned descendants',()=>{
 assert.deepEqual(descendants([{pid:2,parent:1,identity:'200'}],1),[]);
 assert.deepEqual(descendants([{pid:1,parent:0,identity:'200'},{pid:2,parent:1,identity:'300'}],1,'100'),[]);
});
test('equal creation timestamps retain legitimate children and unknown identity fails closed',()=>{
 assert.deepEqual(descendants([{pid:1,parent:0,identity:'100'},{pid:2,parent:1,identity:'100'}],1).map(p=>p.pid),[1,2]);
 assert.throws(()=>descendants([{pid:1,parent:0,identity:'100'},{pid:2,parent:1}],1),/creation identity/);
});
test('macOS lstart order excludes stale descendants too',()=>{
 const table=[{pid:1,parent:0,identity:'Tue Sep  8 19:00:00 2026'},{pid:2,parent:1,identity:'Tue Sep  8 18:00:00 2026'},{pid:3,parent:1,identity:'Tue Sep  8 19:00:01 2026'}];
 assert.deepEqual(descendants(table,1).map(p=>p.pid),[1,3]);
});
