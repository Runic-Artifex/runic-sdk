import { execFileSync } from 'node:child_process';
import { readFileSync, readdirSync } from 'node:fs';
export function processTable() {
  if (process.platform === 'linux') return readdirSync('/proc').filter(x=>/^\d+$/.test(x)).flatMap(id=>{
    try { const stat=readFileSync(`/proc/${id}/stat`,'utf8'); const tail=stat.slice(stat.lastIndexOf(')')+2).split(' ');
      const status=readFileSync(`/proc/${id}/status`,'utf8');
      return [{pid:+id,parent:+tail[1],identity:tail[19],bytes:+(status.match(/^VmRSS:\s+(\d+)/m)?.[1]??0)*1024}];
    } catch {return [];}
  });
  if (process.platform === 'darwin') return execFileSync('ps',['-axo','pid=,ppid=,rss=,lstart='],{encoding:'utf8'}).trim().split('\n').map(line=>{const [pid,parent,kb,...started]=line.trim().split(/\s+/);return {pid:+pid,parent:+parent,bytes:+kb*1024,identity:started.join(' ')};});
  if (process.platform === 'win32') {
    const raw=execFileSync('powershell.exe',['-NoProfile','-NonInteractive','-Command','@(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,WorkingSetSize,CreationDate) | ConvertTo-Json -Compress'],{encoding:'utf8'});
    return JSON.parse(raw).map(x=>({pid:x.ProcessId,parent:x.ParentProcessId,identity:String(x.CreationDate),bytes:Number(x.WorkingSetSize)}));
  }
  throw new Error(`Unsupported process collection platform ${process.platform}`);
}
export function descendants(table, root, identity) {
  if(identity !== undefined && !table.some(p=>p.pid===root && p.identity===identity)) return [];
  const ids=new Set([root]); let changed=true;
  while(changed){changed=false;for(const p of table)if(ids.has(p.parent)&&!ids.has(p.pid)){ids.add(p.pid);changed=true;}}
  return table.filter(p=>ids.has(p.pid));
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
