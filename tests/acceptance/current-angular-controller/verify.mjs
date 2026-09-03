#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import process from 'node:process';
import { releaseManifestAfter, releaseManifestFacts } from '../v0.2-baselines/metrics.mjs';

export const RECEIPT_SCHEMA = 'runic.current-angular-controller/1';
export const REPEAT_RECEIPT_SCHEMA = 'runic.current-angular-controller-repeat/1';
export const NPM_FEED = 'w20-005-local-candidate-npm-feed';
export const CANDIDATE_NAMES = ['@runic-artifex/application-bridge', '@runic-artifex/angular'];
const root = resolve(import.meta.dirname, '../..');
const hostTransport = resolve(import.meta.dirname, '../current-host-transport');
const bridgeIr = JSON.parse(await readFile(join(hostTransport, 'generated', 'bridge.ir.json'), 'utf8'));
export const BRIDGE_CONTRACT = {
  identity: bridgeIr.wire.protocol.identity,
  version: bridgeIr.wire.protocol.version,
  fingerprint: bridgeIr.fingerprint.value,
};
const generator = resolve(root, '../runic-toolkit/web/packages/application-bridge-tooling/dist/esm/cli.js');
const archives = (process.env.RUNIC_CURRENT_ANGULAR_CONTROLLER_NPM_ARCHIVES ?? '').split(',').filter(Boolean).map((path) => resolve(path));
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const sha256 = async (path) => createHash('sha256').update(await readFile(path)).digest('hex');

function run(command, args, cwd, env = {}) {
  return new Promise((done) => {
    const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] }); const output = [];
    child.stdout.on('data', (value) => output.push(value)); child.stderr.on('data', (value) => output.push(value));
    child.on('error', (error) => done({ ok: false, exitCode: null, reasonCode: error.code === 'ENOENT' ? 'command-not-found' : 'command-spawn-failed', output: Buffer.concat(output).toString('utf8') }));
    child.on('close', (exitCode) => done({ ok: exitCode === 0, exitCode, reasonCode: exitCode === 0 ? null : 'command-exit-nonzero', output: Buffer.concat(output).toString('utf8') }));
  });
}
function requireSuccess(name, result) { if (!result.ok) throw new Error(name + ' failed: ' + result.reasonCode + '\n' + result.output.slice(-4096)); }
function phase(name, argv, result) { return { name, argv, status: result.ok ? 'passed' : 'failed', exitCode: result.exitCode, reasonCode: result.ok ? null : result.reasonCode }; }

const registry = "import { createReadStream, readFileSync } from 'node:fs'; import { createServer } from 'node:http'; import { createHash } from 'node:crypto'; import { basename } from 'node:path'; import { execFileSync } from 'node:child_process'; const entries=new Map(process.argv.slice(1).map((archive)=>{const manifest=JSON.parse(execFileSync('tar',['-xOf',archive,'package/package.json'],{encoding:'utf8'}));return [manifest.name,{archive,manifest}];})); const server=createServer((request,response)=>{const pathname=new URL(request.url ?? '/','http://localhost').pathname;for(const [name,entry] of entries){const archivePath='/archive/'+basename(entry.archive);if(decodeURIComponent(pathname.slice(1))===name){const tarball='http://127.0.0.1:'+server.address().port+archivePath;const integrity='sha512-'+createHash('sha512').update(readFileSync(entry.archive)).digest('base64');response.writeHead(200,{'content-type':'application/json'});return response.end(JSON.stringify({name,'dist-tags':{latest:entry.manifest.version},versions:{[entry.manifest.version]:{...entry.manifest,dist:{tarball,integrity}}}}));}if(pathname===archivePath){response.writeHead(200);return createReadStream(entry.archive).pipe(response);}}response.writeHead(404);response.end();});server.listen(0,'127.0.0.1',()=>process.stdout.write('http://127.0.0.1:'+server.address().port+'\\n'));process.on('SIGTERM',()=>server.close(()=>process.exit(0)));";

async function startRegistry() {
  if (archives.length !== 2) throw new Error('RUNIC_CURRENT_ANGULAR_CONTROLLER_NPM_ARCHIVES must name exact Application Bridge and Angular archives');
  const manifests = [];
  for (const archive of archives) { const result = await run('tar', ['-xOf', archive, 'package/package.json'], root); requireSuccess('read ' + basename(archive), result); manifests.push(JSON.parse(result.output)); }
  if (!same(manifests.map((item) => item.name).sort(), [...CANDIDATE_NAMES].sort())) throw new Error('candidate archives must contain exactly the Bridge and Angular packages');
  if (new Set(manifests.map((item) => item.gitHead)).size !== 1) throw new Error('candidate archives must share one source revision');
  const child = spawn('node', ['--input-type=module', '--eval', registry, ...archives], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] });
  const url = await new Promise((resolveUrl, reject) => { const timer = setTimeout(() => reject(new Error('local npm registry did not bind')), 5000); child.stdout.once('data', (value) => { clearTimeout(timer); resolveUrl(value.toString('utf8').trim()); }); child.once('error', reject); });
  return { child, url, manifests };
}

const consumer = [
  "import assert from 'node:assert/strict'; import { readFile } from 'node:fs/promises'; import { createEnvironmentInjector, runInInjectionContext } from '@angular/core'; import { Effect, Schema } from 'effect'; import { MockApplicationBridge, TestApplicationBridge, bridge as bridgeAuthoring, bridgeError, createApplicationBridgeController, defineApplicationBridgeContract, materializeApplicationBridgeContract } from '@runic-artifex/application-bridge'; import { injectApplicationBridge, provideApplicationBridge } from '@runic-artifex/angular';",
  "const manifest=JSON.parse(await readFile(process.argv[2],'utf8')); assert.deepEqual({identity:manifest.wire.protocol.identity,version:manifest.wire.protocol.version,fingerprint:manifest.fingerprint.value},{identity:'runic.artifex.setup',version:1,fingerprint:process.argv[3]}); const Snapshot=Schema.Struct({viewId:Schema.String,revision:Schema.Int,selectedFeatures:Schema.Array(Schema.String),canNavigateBack:Schema.Boolean,canNavigateNext:Schema.Boolean}); const Initialize=Schema.TaggedStruct('InitializeApplication',{}); const Navigate=Schema.TaggedStruct('Navigate',{target:Schema.String,expectedRevision:Schema.Int}); const Receipt=Schema.TaggedStruct('NavigationAccepted',{snapshot:Snapshot}); const Event=Schema.TaggedStruct('NavigationChanged',{viewId:Schema.String,revision:Schema.Int}); const definition=defineApplicationBridgeContract({protocol:manifest.wire.protocol,csharp:{namespace:'Runic.Client',contractName:'Client'},snapshot:Snapshot,commands:[bridgeAuthoring.command(Initialize,{receipt:Receipt}),bridgeAuthoring.command(Navigate,{receipt:Receipt})],events:[Event],errors:[],initialize:{_tag:'InitializeApplication'}}); const contract=materializeApplicationBridgeContract(definition,manifest.fingerprint.value); const initial={viewId:'Welcome',revision:0,selectedFeatures:[],canNavigateBack:false,canNavigateNext:true}; const complete={...initial,viewId:'Complete',revision:1,canNavigateBack:true};",
  "const base=MockApplicationBridge({initialize:()=>Effect.succeed(initial),dispatch:(command,publish)=>command._tag==='Navigate'?publish({_tag:'NavigationChanged',viewId:'Complete',revision:1}).pipe(Effect.as({_tag:'NavigationAccepted',snapshot:complete})):Effect.fail(bridgeError('CommandRejected','Unsupported command.'))}); const controller=createApplicationBridgeController(contract,base); const injector=createEnvironmentInjector([provideApplicationBridge({controller,snapshotFromEvent:event=>event._tag==='NavigationChanged'?complete:undefined})],null); const client=runInInjectionContext(injector,()=>injectApplicationBridge()); assert.deepEqual(await client.initialize(),initial); await client.dispatch({_tag:'Navigate',target:'Complete',expectedRevision:0}); await new Promise(resolve=>setImmediate(resolve)); assert.deepEqual(client.snapshot(),complete); injector.destroy(); assert.deepEqual(await controller.reconnect(),initial); await controller.dispose();",
  "const rejected=createApplicationBridgeController(contract,TestApplicationBridge(base,{rejectCommandTags:new Set(['Navigate'])})); const rejectedInjector=createEnvironmentInjector([provideApplicationBridge({controller:rejected})],null); const rejectedClient=runInInjectionContext(rejectedInjector,()=>injectApplicationBridge()); await rejectedClient.initialize(); await assert.rejects(rejectedClient.dispatch({_tag:'Navigate',target:'Complete',expectedRevision:0}),error=>error._tag==='CommandRejected'); assert.equal(rejectedClient.error()._tag,'CommandRejected'); rejectedInjector.destroy(); await rejected.dispose(); const unavailable=createApplicationBridgeController(contract,MockApplicationBridge({initialize:()=>Effect.fail(bridgeError('TransportUnavailable','Unavailable.',true)),dispatch:()=>Effect.fail(bridgeError('TransportUnavailable','Unavailable.',true))})); const unavailableInjector=createEnvironmentInjector([provideApplicationBridge({controller:unavailable})],null); const unavailableClient=runInInjectionContext(unavailableInjector,()=>injectApplicationBridge()); await assert.rejects(unavailableClient.reconnect(),error=>error._tag==='TransportUnavailable'); assert.equal(unavailableClient.error()._tag,'TransportUnavailable'); unavailableInjector.destroy(); await unavailable.dispose(); console.log('angular-controller-consumer-ok');",
].join('\n');

export function verifyReceipt(receipt, authority) {
  const errors = [];
  if (receipt?.schema !== RECEIPT_SCHEMA) errors.push('receipt schema mismatch');
  if (!same(receipt?.feeds, { npm: NPM_FEED })) errors.push('candidate feed mismatch');
  if (!same(receipt?.isolation, { npmCache: '.npm-cache' })) errors.push('cache isolation mismatch');
  if (!same(receipt?.releaseManifest, authority)) errors.push('release authority mismatch');
  if (!same(receipt?.bridgeContract, BRIDGE_CONTRACT) || !/^[a-f0-9]{64}$/.test(receipt?.bridgeManifestSha256 ?? '')) errors.push('shared generated contract mismatch');
  if (!Array.isArray(receipt?.candidates) || receipt.candidates.length !== 2 || receipt.candidates.some((candidate) => !CANDIDATE_NAMES.includes(candidate?.identity) || candidate.source !== NPM_FEED || typeof candidate.version !== 'string' || !candidate.integrity || !/^[a-f0-9]{64}$/.test(candidate.archiveSha256 ?? ''))) errors.push('npm provenance mismatch');
  const expected = [['generated-contract', ['node', '<authoritative-generator>', 'check', '--source', 'current-host-transport/application.bridge.ts', '--ir', 'current-host-transport/generated/bridge.ir.json', '--facade', 'current-host-transport/generated/application.bridge.generated.ts']], ['npm-install', ['npm', 'install', '--ignore-scripts']], ['angular-controller', ['node', 'consumer.mjs', 'generated/bridge.ir.json', BRIDGE_CONTRACT.fingerprint]]];
  if (!Array.isArray(receipt?.phases) || receipt.phases.length !== expected.length) errors.push('phase evidence malformed'); else expected.forEach(([name, argv], index) => { const item = receipt.phases[index]; if (!item || item.name !== name || !same(item.argv, argv) || item.status !== 'passed' || item.exitCode !== 0 || item.reasonCode !== null) errors.push(name + ' evidence malformed'); });
  return { ok: errors.length === 0, errors };
}
export function verifyRepeatedReceipt(receipt, authority) {
  const errors = [];
  if (receipt?.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push('repeat receipt schema mismatch'); else { receipt.journeys.forEach((journey, index) => errors.push(...verifyReceipt(journey, authority).errors.map((error) => 'journey ' + (index + 1) + ': ' + error))); if (!same(receipt.journeys[0], receipt.journeys[1])) errors.push('Angular controller journeys are not deterministic'); }
  return { ok: errors.length === 0, errors };
}
export async function runAngularController(manifestPath) {
  const authority = await releaseManifestFacts(manifestPath); const directory = await mkdtemp(join(tmpdir(), 'runic-current-angular-controller-')); const environment = { npm_config_cache: join(directory, '.npm-cache'), npm_config_update_notifier: 'false' }; let localRegistry;
  try {
    const generatedResult = await run('node', [generator, 'check', '--source', join(hostTransport, 'application.bridge.ts'), '--ir', join(hostTransport, 'generated', 'bridge.ir.json'), '--facade', join(hostTransport, 'generated', 'application.bridge.generated.ts')], root); requireSuccess('generated-contract', generatedResult); const bridgeManifest = JSON.parse(await readFile(join(hostTransport, 'generated', 'bridge.ir.json'), 'utf8')); if (!same({ identity: bridgeManifest.wire.protocol?.identity, version: bridgeManifest.wire.protocol?.version, fingerprint: bridgeManifest.fingerprint?.value }, BRIDGE_CONTRACT)) throw new Error('W20-002 generated contract identity mismatch');
    localRegistry = await startRegistry(); const versions = Object.fromEntries(localRegistry.manifests.map((item) => [item.name, item.version]));
    await Promise.all([cp(join(hostTransport, 'generated'), join(directory, 'generated'), { recursive: true }), writeFile(join(directory, 'package.json'), JSON.stringify({ private: true, type: 'module', dependencies: { '@angular/core': '22.0.8', '@runic-artifex/application-bridge': versions['@runic-artifex/application-bridge'], '@runic-artifex/angular': versions['@runic-artifex/angular'], effect: '3.22.1' }, overrides: { '@runic-artifex/application-bridge': versions['@runic-artifex/application-bridge'] } }, null, 2) + '\n'), writeFile(join(directory, '.npmrc'), '@runic-artifex:registry=' + localRegistry.url + '\n'), writeFile(join(directory, 'consumer.mjs'), consumer)]);
    const phases = []; for (const [name, command, args] of [['npm-install', 'npm', ['install', '--ignore-scripts']], ['angular-controller', 'node', ['consumer.mjs', 'generated/bridge.ir.json', BRIDGE_CONTRACT.fingerprint]]]) { const result = await run(command, args, directory, environment); phases.push(phase(name, [command, ...args], result)); requireSuccess(name, result); }
    const lock = JSON.parse(await readFile(join(directory, 'package-lock.json'), 'utf8')); const candidates = await Promise.all(localRegistry.manifests.map(async (item) => { const entry = lock.packages?.['node_modules/' + item.name]; if (!entry || entry.version !== item.version || !entry.resolved?.startsWith(localRegistry.url) || !entry.integrity) throw new Error('npm provenance failed closed for ' + item.name); const archive = archives[localRegistry.manifests.indexOf(item)]; return { identity: item.name, version: item.version, source: NPM_FEED, integrity: entry.integrity, archiveSha256: await sha256(archive) }; }));
    const receipt = { schema: RECEIPT_SCHEMA, feeds: { npm: NPM_FEED }, isolation: { npmCache: '.npm-cache' }, releaseManifest: await releaseManifestAfter(manifestPath, authority), bridgeContract: BRIDGE_CONTRACT, bridgeManifestSha256: await sha256(join(hostTransport, 'generated', 'bridge.ir.json')), candidates, phases: [phase('generated-contract', ['node', '<authoritative-generator>', 'check', '--source', 'current-host-transport/application.bridge.ts', '--ir', 'current-host-transport/generated/bridge.ir.json', '--facade', 'current-host-transport/generated/application.bridge.generated.ts'], generatedResult), ...phases] }; const report = verifyReceipt(receipt, receipt.releaseManifest); if (!report.ok) throw new Error(report.errors.join('\n')); return receipt;
  } finally { if (localRegistry) localRegistry.child.kill('SIGTERM'); await rm(directory, { recursive: true, force: true }); }
}
export async function runAngularControllerTwice(manifestPath) { const journeys = [await runAngularController(manifestPath), await runAngularController(manifestPath)]; const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys }; const report = verifyRepeatedReceipt(receipt, journeys[0].releaseManifest); if (!report.ok) throw new Error(report.errors.join('\n')); return receipt; }
async function main() { const [command, manifestPath, receiptPath] = process.argv.slice(2); if (command === 'run-twice' && manifestPath && !receiptPath) return process.stdout.write(JSON.stringify(await runAngularControllerTwice(manifestPath), null, 2) + '\n'); if (command === 'verify-twice' && manifestPath && receiptPath) { const report = verifyRepeatedReceipt(JSON.parse(await readFile(receiptPath, 'utf8')), await releaseManifestFacts(manifestPath)); if (!report.ok) throw new Error(report.errors.join('\n')); return; } throw new Error('Usage: node eng/current-angular-controller/verify.mjs run-twice <runic.release.json> | verify-twice <runic.release.json> <receipt.json>'); }
if (import.meta.main) main().catch((error) => { process.stderr.write(error.message + '\n'); process.exitCode = 1; });
