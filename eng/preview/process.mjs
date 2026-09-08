import { spawnSync } from 'node:child_process';
// Never propagate child-process Error objects: their messages can include argv
// carrying a short-lived NuGet key. Output is inherited and not embedded in errors.
export function runChecked(command,args,options={},spawn=spawnSync) {
  const result=spawn(command,args,{stdio:'inherit',...options});
  if(result.error || result.status!==0) throw new Error(`Release subprocess failed (${command}; exit ${result.status ?? 'unavailable'}).`);
}
