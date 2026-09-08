import { execFileSync } from 'node:child_process';
import { readFileSync, readdirSync } from 'node:fs';
export function processTable() {
  if (process.platform === 'linux') return readdirSync('/proc').filter(x=>/^\d+$/.test(x)).flatMap(id=>{
    try { const stat=readFileSync(`/proc/${id}/stat`,'utf8'); const tail=stat.slice(stat.lastIndexOf(')')+2).split(' ');
      const status=readFileSync(`/proc/${id}/status`,'utf8');
      return [{pid:+id,parent:+tail[1],identity:tail[19],state:tail[0],name:stat.slice(stat.indexOf('(')+1,stat.lastIndexOf(')')),bytes:+(status.match(/^VmRSS:\s+(\d+)/m)?.[1]??0)*1024}];
    } catch {return [];}
  });
  if (process.platform === 'darwin') return execFileSync('ps',['-axo','pid=,ppid=,rss=,lstart='],{encoding:'utf8'}).trim().split('\n').map(line=>{const [pid,parent,kb,...started]=line.trim().split(/\s+/);return {pid:+pid,parent:+parent,bytes:+kb*1024,identity:started.join(' ')};});
  if (process.platform === 'win32') {
    const raw=execFileSync('powershell.exe',['-NoProfile','-NonInteractive','-Command','@(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,WorkingSetSize,CreationDate) | ConvertTo-Json -Compress'],{encoding:'utf8'});
    return JSON.parse(raw).map(x=>({pid:x.ProcessId,parent:x.ParentProcessId,identity:String(x.CreationDate),bytes:Number(x.WorkingSetSize)}));
  }
  throw new Error(`Unsupported process collection platform ${process.platform}`);
}
function creationOrder(identity) {
  // Linux exposes boot-clock ticks; Windows PowerShell JSON uses epoch ms.
  // macOS ps lstart has second precision. Equal timestamps are legitimate when
  // parent and child start within the same clock quantum; older children are not.
  const windows = /^\/Date\((-?\d+)(?:[+-]\d{4})?\)\/$/.exec(identity ?? '');
  const value = windows ? Number(windows[1]) : /^\d+$/.test(identity ?? '') ? Number(identity) : Date.parse(identity);
  if (!Number.isFinite(value)) throw new Error(`Missing or invalid process creation identity: ${identity}`);
  return value;
}
export function descendants(table, root, identity) {
  const rootProcess = table.find(p=>p.pid===root);
  if (!rootProcess || (identity !== undefined && rootProcess.identity !== identity)) return [];
  const members = new Map([[root, creationOrder(rootProcess.identity)]]);
  let changed = true;
  while (changed) {
    changed = false;
    for (const p of table) {
      if (!members.has(p.parent) || members.has(p.pid)) continue;
      const created = creationOrder(p.identity);
      // PPID survives a parent's exit on Windows. A reused parent PID must not
      // adopt an older unrelated process into measurement or cleanup ownership.
      if (created < members.get(p.parent)) continue;
      members.set(p.pid, created);
      changed = true;
    }
  }
  return table.filter(p=>members.has(p.pid));
}
export const memoryMetric=process.platform==='win32'?'sum-process-working-set-bytes':'sum-process-rss-bytes';

// A numeric PID can be reused after exit; only retain the observed process instance.
export function trackedProcesses(table, known) {
  return table.filter(p=>known.get(p.pid)===p.identity);
}
export function trackProcesses(tree, known) {
  for(const p of tree){
    if(!p.identity)throw new Error(`Missing creation identity for process ${p.pid}`);
    known.set(p.pid,p.identity);
  }
}

// Browser helpers exit asynchronously after the host. Observe the same process
// identities until the shutdown deadline; never turn forced cleanup into a pass.
export async function waitForTrackedExit(known, { timeoutMs = 15000, table = processTable, delay = ms => new Promise(resolve => setTimeout(resolve, ms)) } = {}) {
  const start = performance.now();
  const observations = [];
  while (true) {
    const processes = trackedProcesses(table(), known);
    const elapsedMs = performance.now() - start;
    observations.push({elapsedMs, processes});
    if (!processes.length) return {elapsedMs, observations};
    if (elapsedMs >= timeoutMs) throw new Error(`Native child processes survived shutdown deadline: ${JSON.stringify(processes)}`);
    await delay(Math.min(100, timeoutMs - elapsedMs));
  }
}
