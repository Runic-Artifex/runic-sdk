#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import process from 'node:process';
import { releaseManifestAfter, releaseManifestFacts } from '../v0.2-baselines/metrics.mjs';

export const RECEIPT_SCHEMA = 'runic.current-svelte-controller/1';
export const REPEAT_RECEIPT_SCHEMA = 'runic.current-svelte-controller-repeat/1';
const root = resolve(import.meta.dirname, '../..');
const archives = (process.env.RUNIC_CURRENT_SVELTE_CONTROLLER_NPM_ARCHIVES ?? '').split(',').filter(Boolean).map((path) => resolve(path));
export const NPM_FEED = 'w20-003-local-candidate-npm-feed';
export const CANDIDATE_NAMES = ['@runic-artifex/application-bridge', '@runic-artifex/svelte'];
const hostTransport = resolve(import.meta.dirname, '../current-host-transport');
const bridgeManifestPath = join(hostTransport, 'generated', 'bridge.ir.json');
const bridgeManifest = JSON.parse(await readFile(bridgeManifestPath, 'utf8'));
export const BRIDGE_CONTRACT = { identity: bridgeManifest.wire.protocol.identity, version: bridgeManifest.wire.protocol.version, fingerprint: bridgeManifest.fingerprint.value };
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const sha256 = async (path) => createHash('sha256').update(await readFile(path)).digest('hex');

function run(command, args, cwd, env = {}) {
  return new Promise((done) => {
    const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
    const output = [];
    child.stdout.on('data', (value) => output.push(value));
    child.stderr.on('data', (value) => output.push(value));
    child.on('error', (error) => done({ ok: false, exitCode: null, reasonCode: error.code === 'ENOENT' ? 'command-not-found' : 'command-spawn-failed', output: Buffer.concat(output).toString('utf8') }));
    child.on('close', (exitCode) => done({ ok: exitCode === 0, exitCode, reasonCode: exitCode === 0 ? null : 'command-exit-nonzero', output: Buffer.concat(output).toString('utf8') }));
  });
}
function requireSuccess(name, result) { if (!result.ok) throw new Error(name + ' failed: ' + result.reasonCode + '\n' + result.output.slice(-4096)); }
function phase(name, argv, result) { return { name, argv, status: result.ok ? 'passed' : 'failed', exitCode: result.exitCode, reasonCode: result.ok ? null : result.reasonCode }; }

const registry = "import { createReadStream, readFileSync } from 'node:fs'; import { createServer } from 'node:http'; import { createHash } from 'node:crypto'; import { basename } from 'node:path'; import { execFileSync } from 'node:child_process'; const entries=new Map(process.argv.slice(1).map((archive)=>{const manifest=JSON.parse(execFileSync('tar',['-xOf',archive,'package/package.json'],{encoding:'utf8'}));return [manifest.name,{archive,manifest}];})); const server=createServer((request,response)=>{const pathname=new URL(request.url ?? '/','http://localhost').pathname;for(const [name,entry] of entries){const archivePath='/archive/'+basename(entry.archive);if(decodeURIComponent(pathname.slice(1))===name){const tarball='http://127.0.0.1:'+server.address().port+archivePath;const integrity='sha512-'+createHash('sha512').update(readFileSync(entry.archive)).digest('base64');response.writeHead(200,{'content-type':'application/json'});return response.end(JSON.stringify({name,'dist-tags':{latest:entry.manifest.version},versions:{[entry.manifest.version]:{...entry.manifest,dist:{tarball,integrity}}}}));}if(pathname===archivePath){response.writeHead(200);return createReadStream(entry.archive).pipe(response);}}response.writeHead(404);response.end();});server.listen(0,'127.0.0.1',()=>process.stdout.write('http://127.0.0.1:'+server.address().port+'\\n'));process.on('SIGTERM',()=>server.close(()=>process.exit(0)));";

async function startRegistry() {
  if (archives.length !== 2) throw new Error('RUNIC_CURRENT_SVELTE_CONTROLLER_NPM_ARCHIVES must name exact Application Bridge and Svelte archives');
  const manifests = [];
  for (const archive of archives) {
    const result = await run('tar', ['-xOf', archive, 'package/package.json'], root);
    requireSuccess('read ' + basename(archive), result);
    manifests.push(JSON.parse(result.output));
  }
  if (!same(manifests.map((item) => item.name).sort(), [...CANDIDATE_NAMES].sort())) throw new Error('candidate archives must contain exactly the Bridge and Svelte packages');
  const child = spawn('node', ['--input-type=module', '--eval', registry, ...archives], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] });
  const url = await new Promise((resolveUrl, reject) => { const timer = setTimeout(() => reject(new Error('local npm registry did not bind')), 5000); child.stdout.once('data', (value) => { clearTimeout(timer); resolveUrl(value.toString('utf8').trim()); }); child.once('error', reject); });
  return { child, url, manifests };
}

const consumer = [
  "import assert from 'node:assert/strict'; import { Window } from 'happy-dom';",
  "const dom = new Window(); Object.assign(globalThis, { window: dom, document: dom.document, Element: dom.Element, Node: dom.Node, Text: dom.Text, Comment: dom.Comment, Event: dom.Event, CustomEvent: dom.CustomEvent });",
  "const { run } = await import('./dist/consumer.mjs');",
  "const result = await run(); assert.deepEqual(result, { ready: 1, rendered: 1, unsubscribed: 1, controllerDisposed: 0, projectedCount: '3', invalidStatus: 'error', reconnectStatus: 'error' });",
  "console.log('svelte-controller-consumer-ok');",
].join('\n');
const provider = '<script>import { createApplicationBridgeContext } from "@runic-artifex/svelte"; let { bridge } = $props(); const context = createApplicationBridgeContext(); context.provide(bridge);</script><output>{bridge.snapshot?.count ?? -1}</output>';
const entry = [
  "import { mount, tick, unmount } from 'svelte'; import Provider from './Provider.svelte'; import { createSvelteApplicationBridge } from '@runic-artifex/svelte';",
  "export async function run() { let listener; let unsubscribed = 0; const host = { initialize: async () => ({ count: 0 }), dispatch: async () => ({ _tag: 'Ignored' }), cancel: async () => undefined, reconnect: async () => ({ count: 0 }), uiReady: async () => { host.ready += 1; }, uiRendered: async () => { host.rendered += 1; }, dispose: async () => { host.disposed += 1; }, subscribe: (next) => { listener = next; return () => { unsubscribed += 1; }; }, ready: 0, rendered: 0, disposed: 0 }; const bridge = createSvelteApplicationBridge(host, { reduce: (_snapshot, event) => event.snapshot }); const mounted = mount(Provider, { target: document.body, props: { bridge } }); await tick(); await bridge.start(); listener({ _tag: 'Changed', snapshot: { count: 3 } }); await tick(); const projectedCount = document.querySelector('output').textContent; await unmount(mounted); const invalid = createSvelteApplicationBridge({ ...host, initialize: async () => { throw new Error('invalid'); }, reconnect: async () => { throw new Error('reconnect'); } }); await invalid.start().catch(() => undefined); const invalidStatus = invalid.status; await invalid.reconnect().catch(() => undefined); return { ready: host.ready, rendered: host.rendered, unsubscribed, controllerDisposed: host.disposed, projectedCount, invalidStatus, reconnectStatus: invalid.status }; }",
].join('\n');
const viteConfig = "import { svelte } from '@sveltejs/vite-plugin-svelte'; import { defineConfig } from 'vite'; export default defineConfig({ plugins: [svelte()], build: { outDir: 'dist', lib: { entry: 'main.js', formats: ['es'], fileName: () => 'consumer.mjs' } } });";

export function verifyReceipt(receipt, authority) {
  const errors = [];
  if (receipt?.schema !== RECEIPT_SCHEMA) errors.push('receipt schema mismatch');
  if (!same(receipt?.feeds, { npm: NPM_FEED })) errors.push('candidate feed mismatch');
  if (!same(receipt?.isolation, { npmCache: '.npm-cache' })) errors.push('cache isolation mismatch');
  if (!same(receipt?.releaseManifest, authority)) errors.push('release authority mismatch');
  if (!same(receipt?.bridgeContract, BRIDGE_CONTRACT) || !/^[a-f0-9]{64}$/.test(receipt?.bridgeManifestSha256 ?? '')) errors.push('shared generated contract mismatch');
  if (!Array.isArray(receipt?.candidates) || receipt.candidates.length !== 2 || receipt.candidates.some((candidate) => !CANDIDATE_NAMES.includes(candidate?.identity) || candidate.source !== NPM_FEED || typeof candidate.version !== 'string' || !candidate.integrity || !/^[a-f0-9]{64}$/.test(candidate.archiveSha256 ?? ''))) errors.push('npm provenance mismatch');
  const expected = [['npm-install', ['npm', 'install', '--ignore-scripts']], ['frontend-build', ['npm', 'run', 'build']], ['headless-provider', ['node', 'consumer.mjs']]];
  if (!Array.isArray(receipt?.phases) || receipt.phases.length !== expected.length) errors.push('phase evidence malformed');
  else expected.forEach(([name, argv], index) => { const actual = receipt.phases[index]; if (!actual || actual.name !== name || !same(actual.argv, argv) || actual.status !== 'passed' || actual.exitCode !== 0 || actual.reasonCode !== null) errors.push(name + ' evidence malformed'); });
  return { ok: errors.length === 0, errors };
}
export function verifyRepeatedReceipt(receipt, authority) {
  const errors = [];
  if (receipt?.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push('repeat receipt schema mismatch');
  else { receipt.journeys.forEach((journey, index) => errors.push(...verifyReceipt(journey, authority).errors.map((error) => 'journey ' + (index + 1) + ': ' + error))); if (!same(receipt.journeys[0], receipt.journeys[1])) errors.push('Svelte controller journeys are not deterministic'); }
  return { ok: errors.length === 0, errors };
}
export async function runSvelteController(manifestPath) {
  const authority = await releaseManifestFacts(manifestPath); const directory = await mkdtemp(join(tmpdir(), 'runic-current-svelte-controller-')); const environment = { npm_config_cache: join(directory, '.npm-cache') }; let localRegistry;
  try {
    localRegistry = await startRegistry();
    const versions = Object.fromEntries(localRegistry.manifests.map((item) => [item.name, item.version]));
    await Promise.all([writeFile(join(directory, 'package.json'), JSON.stringify({ private: true, type: 'module', scripts: { build: 'vite build' }, dependencies: { '@runic-artifex/application-bridge': versions['@runic-artifex/application-bridge'], '@runic-artifex/svelte': versions['@runic-artifex/svelte'], effect: '3.22.1', 'happy-dom': '20.11.2', svelte: '5.56.8', vite: '8.2.1', '@sveltejs/vite-plugin-svelte': '7.2.0' } }, null, 2) + '\n'), writeFile(join(directory, '.npmrc'), '@runic-artifex:registry=' + localRegistry.url + '\n'), writeFile(join(directory, 'Provider.svelte'), provider), writeFile(join(directory, 'main.js'), entry), writeFile(join(directory, 'vite.config.js'), viteConfig), writeFile(join(directory, 'consumer.mjs'), consumer)]);
    const phases = [];
    for (const [name, command, args] of [['npm-install', 'npm', ['install', '--ignore-scripts']], ['frontend-build', 'npm', ['run', 'build']], ['headless-provider', 'node', ['consumer.mjs']]]) { const result = await run(command, args, directory, environment); phases.push(phase(name, [command, ...args], result)); requireSuccess(name, result); }
    const lock = JSON.parse(await readFile(join(directory, 'package-lock.json'), 'utf8'));
    const candidates = await Promise.all(localRegistry.manifests.map(async (manifest) => { const entry = lock.packages?.['node_modules/' + manifest.name]; if (!entry || entry.version !== manifest.version || !entry.resolved?.startsWith(localRegistry.url) || !entry.integrity) throw new Error('npm provenance failed closed for ' + manifest.name); const archive = archives[localRegistry.manifests.indexOf(manifest)]; return { identity: manifest.name, version: manifest.version, source: NPM_FEED, integrity: entry.integrity, archiveSha256: await sha256(archive) }; }));
    const receipt = { schema: RECEIPT_SCHEMA, feeds: { npm: NPM_FEED }, isolation: { npmCache: '.npm-cache' }, releaseManifest: await releaseManifestAfter(manifestPath, authority), bridgeContract: BRIDGE_CONTRACT, bridgeManifestSha256: await sha256(bridgeManifestPath), candidates, phases };
    const report = verifyReceipt(receipt, receipt.releaseManifest); if (!report.ok) throw new Error(report.errors.join('\n')); return receipt;
  } finally { if (localRegistry) localRegistry.child.kill('SIGTERM'); await rm(directory, { recursive: true, force: true }); }
}
export async function runSvelteControllerTwice(manifestPath) { const journeys = [await runSvelteController(manifestPath), await runSvelteController(manifestPath)]; const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys }; const report = verifyRepeatedReceipt(receipt, journeys[0].releaseManifest); if (!report.ok) throw new Error(report.errors.join('\n')); return receipt; }
async function main() { const [command, manifestPath, receiptPath] = process.argv.slice(2); if (command === 'run-twice' && manifestPath && !receiptPath) return process.stdout.write(JSON.stringify(await runSvelteControllerTwice(manifestPath), null, 2) + '\n'); if (command === 'verify-twice' && manifestPath && receiptPath) { const report = verifyRepeatedReceipt(JSON.parse(await readFile(receiptPath, 'utf8')), await releaseManifestFacts(manifestPath)); if (!report.ok) throw new Error(report.errors.join('\n')); return; } throw new Error('Usage: node eng/current-svelte-controller/verify.mjs run-twice <runic.release.json> | verify-twice <runic.release.json> <receipt.json>'); }
if (import.meta.main) main().catch((error) => { process.stderr.write(error.message + '\n'); process.exitCode = 1; });
