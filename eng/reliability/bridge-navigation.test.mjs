import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
const source=readFileSync(new URL('../../packages/dotnet/Runic.Desktop/Internal/WebUiBridge.cs',import.meta.url),'utf8');
const script=source.split('"""')[1].replaceAll('__TOKEN__','1').replaceAll('__PORT__','8080').replaceAll('__BASE_PATH__','').replaceAll('__SESSION_CREDENTIAL__','').replaceAll('__CUSTOM_WINDOW_DRAG__','false');
for (const [advertisement,intercepted] of [['__webui_core_api__,',false],['__webui_core_api__,save,',false],['__webui_core_api__,,',true],[',',true],['',false]]) {
 test(`wire binding list ${JSON.stringify(advertisement)} ${intercepted?'intercepts':'allows'} navigation`,async()=>{
  let socket;const events={};
  class Socket {static OPEN=1;readyState=1;handlers={};constructor(){socket=this;}addEventListener(name,callback){this.handlers[name]=callback;}send(){}close(){}}
  const location={href:'http://127.0.0.1:8080/'};
  const context={TextEncoder,TextDecoder,URL,WebSocket:Socket,location,window:{location},document:{addEventListener(){}},navigation:{addEventListener(name,callback){events[name]=callback;}},addEventListener(){},setTimeout(){},clearTimeout(){},setInterval(){},console};
  runInNewContext(script,context);
  const text=new TextEncoder().encode(advertisement);const packet=new Uint8Array(9+text.length);packet[0]=0xDD;packet[7]=0xF5;packet[8]=1;packet.set(text,9);
  socket.handlers.message({data:packet.buffer});await context.webui.connected;
  let prevented=false;events.navigate({destination:{url:location.href},preventDefault(){prevented=true;}});
  assert.equal(prevented,intercepted);
 });
}
