#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import process from 'node:process';
import { releaseManifestAfter, releaseManifestFacts } from '../v0.2-baselines/metrics.mjs';

export const RECEIPT_SCHEMA = 'runic.current-host-transport/1';
export const REPEAT_RECEIPT_SCHEMA = 'runic.current-host-transport-repeat/1';
const root = resolve(import.meta.dirname, '../..');
const configuredNugetFeed = process.env.RUNIC_CURRENT_HOST_TRANSPORT_NUGET_FEED;
export const FEED_PATH = configuredNugetFeed && resolve(configuredNugetFeed);
export const APPLICATION_VERSION = process.env.RUNIC_CURRENT_HOST_TRANSPORT_APPLICATION_VERSION ?? '0.2.0-preview.1e8fff0';
export const NUGET_FEED = 'w20-002-local-candidate-feed';
export function requireCandidateFeed(path = FEED_PATH) {
  if (!path) throw new Error('RUNIC_CURRENT_HOST_TRANSPORT_NUGET_FEED must name an explicit isolated NuGet candidate feed.');
  return path;
}
export const NPM_FEED = 'w20-002-local-candidate-npm-feed';
export const CANDIDATES = ['Runic.Application', 'Runic.Application.Bridge', 'Runic.Application.Hosting'].map((identity) => ({ identity, version: APPLICATION_VERSION }));
export const NPM_CANDIDATE = { identity: '@runic-artifex/application-bridge', version: process.env.RUNIC_CURRENT_HOST_TRANSPORT_NPM_VERSION ?? '0.1.0' };
export const MANIFEST = JSON.parse(await readFile(join(import.meta.dirname, 'generated', 'bridge.ir.json'), 'utf8'));
const archive = process.env.RUNIC_CURRENT_HOST_TRANSPORT_NPM_ARCHIVE && resolve(process.env.RUNIC_CURRENT_HOST_TRANSPORT_NPM_ARCHIVE);
const contractGenerator = resolve(process.env.RUNIC_CURRENT_HOST_TRANSPORT_CONTRACT_GENERATOR ?? join(root, '../runic-toolkit/web/packages/application-bridge-tooling/dist/esm/cli.js'));
const fixtureNames = ['initialize.client.json', 'resynchronized.host.json', 'late-old-admission-error.host.json', 'future-admission-error.host.json'];
const isolation = { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet', npmCache: '.npm-cache' };
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const hash = async (path) => createHash('sha256').update(await readFile(path)).digest('hex');

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
function requireSuccess(name, result) {
  if (!result.ok) throw new Error(name + ' failed: ' + result.reasonCode + '\n' + result.output.slice(-4096));
}
function phase(name, argv, result) {
  return { name, argv, status: result.ok ? 'passed' : 'failed', exitCode: result.exitCode, reasonCode: result.ok ? null : result.reasonCode };
}
function config(folder) {
  return '<?xml version="1.0" encoding="utf-8"?>\n<configuration><packageSources><clear/><add key="candidate" value="' + FEED_PATH + '"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="*"/></packageSource></packageSourceMapping><config><add key="globalPackagesFolder" value="' + folder + '"/></config></configuration>\n';
}
const project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App"/>' + CANDIDATES.map((item) => '<PackageReference Include="' + item.identity + '" Version="' + item.version + '"/>').join('') + '</ItemGroup><ItemGroup><AdditionalFiles Include="generated/bridge.ir.json"/></ItemGroup></Project>\n';
const program = [
  'using Microsoft.AspNetCore.Builder; using Microsoft.AspNetCore.Hosting; using Microsoft.AspNetCore.Http; using Microsoft.Extensions.Hosting; using Runic.Application; using Runic.Application.Hosting; using Runic.Application.Bridge; using RunicToolkit.Setup.Contract;',
  '[assembly: RunicApplicationManifest("runic.artifex.conformance")]',
  'await using var session = new ApplicationBridgeSession(new SetupBridgeDispatcher(new Handler())); await using var transport = new ApplicationBridgeWebSocketTransport(session, new() { AllowedOrigins = new HashSet<string>(StringComparer.Ordinal) { "https://trusted.example.test" }, Limits = new BridgeLimits { MaxFrameBytes = 1024 } });',
  'var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); await using var app = builder.Build(); app.UseWebSockets(); app.MapRunicApplicationBridge("/bridge", transport); app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) => { lifetime.StopApplication(); return Results.NoContent(); }); await app.StartAsync(); Console.WriteLine("READY " + app.Urls.Single()); await app.WaitForShutdownAsync();',
  'sealed class Handler : ISetupBridgeHandler { private static ApplicationInitializedSnapshot InitialSnapshot(string viewId, long revision) => new() { ViewId = viewId, Revision = revision, SelectedFeatures = Array.Empty<string>(), CanNavigateBack = false, CanNavigateNext = true }; private static NavigationAcceptedSnapshot NavigatedSnapshot(string viewId, long revision) => new() { ViewId = viewId, Revision = revision, SelectedFeatures = Array.Empty<string>(), CanNavigateBack = false, CanNavigateNext = true }; public ValueTask<ApplicationInitialized> InitializeApplicationAsync(InitializeApplication command, BridgeCommandContext context, CancellationToken token) => ValueTask.FromResult(new ApplicationInitialized { Tag = "ApplicationInitialized", Snapshot = InitialSnapshot("Welcome", 0) }); public async ValueTask<NavigationAccepted> NavigateAsync(Navigate command, BridgeCommandContext context, CancellationToken token) { await context.Events.PublishNavigationChangedAsync(new NavigationChanged { Tag = "NavigationChanged", ViewId = command.Target, Revision = command.ExpectedRevision + 1 }, true, cancellationToken: token); return new NavigationAccepted { Tag = "NavigationAccepted", Snapshot = NavigatedSnapshot(command.Target, command.ExpectedRevision + 1) }; } public ValueTask<DestinationSelected> SelectDestinationAsync(SelectDestination command, BridgeCommandContext context, CancellationToken token) => throw new NotSupportedException(); public ValueTask<InstallationStarted> StartInstallationAsync(StartInstallation command, BridgeCommandContext context, CancellationToken token) => throw new NotSupportedException(); public ValueTask<OperationCancellationAccepted> CancelOperationAsync(CancelOperation command, BridgeCommandContext context, CancellationToken token) => throw new NotSupportedException(); }',
].join('\n');
const client = [
  "import assert from 'node:assert/strict'; import net from 'node:net'; import { readFile } from 'node:fs/promises'; import { Schema } from 'effect'; import { ApplicationBridgeLive, bridge as bridgeAuthoring, createApplicationBridgeController, createWebSocketFrameChannel, defineApplicationBridgeContract, materializeApplicationBridgeContract } from '@runic-artifex/application-bridge';",
  "const [url, manifestPath, fixturesPath] = process.argv.slice(2); const manifest = JSON.parse(await readFile(manifestPath, 'utf8')); const fixtures = await Promise.all(['initialize.client.json','resynchronized.host.json','late-old-admission-error.host.json','future-admission-error.host.json'].map(async (name) => JSON.parse(await readFile(fixturesPath + '/' + name, 'utf8')))); assert.equal(manifest.formatVersion, 1); assert.ok(Object.keys(manifest.wire.definitions).length >= 20); for (const value of fixtures) { assert.equal(value.protocol, manifest.wire.protocol.identity); assert.equal(value.version, manifest.wire.protocol.version); assert.equal(value.contractFingerprint, manifest.fingerprint.value); } assert.equal(fixtures[2].connectionEpoch, 0); assert.equal(fixtures[3].connectionEpoch, 2);",
  "const snapshot = Schema.Struct({ viewId: Schema.String, revision: Schema.Int, selectedFeatures: Schema.Array(Schema.String), canNavigateBack: Schema.Boolean, canNavigateNext: Schema.Boolean }); const initialize=Schema.TaggedStruct('InitializeApplication', {}); const navigate=Schema.TaggedStruct('Navigate', { target: Schema.String, expectedRevision: Schema.Int }); const receipt=Schema.TaggedStruct('NavigationAccepted', { snapshot }); const definition=defineApplicationBridgeContract({protocol:manifest.wire.protocol,csharp:{namespace:'Runic.Client',contractName:'Client'},snapshot,commands:[bridgeAuthoring.command(initialize,{receipt}),bridgeAuthoring.command(navigate,{receipt})],events:[Schema.TaggedStruct('NavigationChanged', { viewId: Schema.String, revision: Schema.Int })],errors:[],initialize:{_tag:'InitializeApplication'}}); const contract=materializeApplicationBridgeContract(definition,manifest.fingerprint.value);",
  "const opened = (socket) => new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); }); const closed = (socket) => new Promise((resolve) => socket.addEventListener('close', (event) => resolve(event.code), { once: true })); async function rejected(payload) { const socket = new WebSocket(url); await opened(socket); socket.send(payload); assert.equal(await closed(socket), 1008); }",
  "const endpoint = new URL(url); await new Promise((resolve, reject) => { const socket = net.connect(Number(endpoint.port), endpoint.hostname, () => socket.write('GET /bridge HTTP/1.1\\r\\nHost: ' + endpoint.host + '\\r\\nUpgrade: websocket\\r\\nConnection: Upgrade\\r\\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\\r\\nSec-WebSocket-Version: 13\\r\\nOrigin: https://untrusted.example.test\\r\\n\\r\\n')); let response = ''; socket.on('data', (chunk) => { response += chunk; if (response.includes(' 403 ')) { socket.destroy(); resolve(); } }); socket.on('end', () => response.includes(' 403 ') ? resolve() : reject(new Error('untrusted origin was not rejected'))); socket.on('error', reject); }); await rejected('not-json'); await rejected(new Uint8Array(1025));",
  "const channel = createWebSocketFrameChannel(() => new WebSocket(url)); await channel.reconnect(); const bridge = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel)); try { const event = new Promise((resolve, reject) => bridge.subscribe(resolve, reject)); assert.equal((await bridge.initialize()).viewId, 'Welcome'); assert.equal((await bridge.dispatch({ _tag: 'Navigate', target: 'Complete', expectedRevision: 0 })).snapshot.revision, 1); assert.deepEqual(await event, { _tag: 'NavigationChanged', revision: 1, viewId: 'Complete' }); assert.equal((await bridge.reconnect()).viewId, 'Welcome'); } finally { await bridge.dispose(); } console.log('host-transport-client-ok');",
].join('\n');
const registry = "import { createReadStream, readFileSync } from 'node:fs'; import { createServer } from 'node:http'; import { createHash } from 'node:crypto'; import { execFileSync } from 'node:child_process'; import { basename } from 'node:path'; const archive=process.argv[1]; const manifest=JSON.parse(execFileSync('tar',['-xOf',archive,'package/package.json'],{encoding:'utf8'})); const server=createServer((request,response)=>{const pathname=new URL(request.url ?? '/','http://localhost').pathname;const archivePath='/archive/'+basename(archive);if(decodeURIComponent(pathname.slice(1))===manifest.name){const tarball='http://127.0.0.1:'+server.address().port+archivePath;const integrity='sha512-'+createHash('sha512').update(readFileSync(archive)).digest('base64');response.writeHead(200,{'content-type':'application/json'});return response.end(JSON.stringify({name:manifest.name,'dist-tags':{latest:manifest.version},versions:{[manifest.version]:{...manifest,dist:{tarball,integrity}}}}));}if(pathname===archivePath){response.writeHead(200);return createReadStream(archive).pipe(response);}response.writeHead(404);response.end();});server.listen(0,'127.0.0.1',()=>process.stdout.write('http://127.0.0.1:'+server.address().port+'\\n'));process.on('SIGTERM',()=>server.close(()=>process.exit(0)));";
async function startRegistry() {
  if (!archive) throw new Error('RUNIC_CURRENT_HOST_TRANSPORT_NPM_ARCHIVE must name a freshly packed application-bridge archive');
  const result = await run('tar', ['-xOf', archive, 'package/package.json'], root); requireSuccess('read npm candidate', result);
  const manifest = JSON.parse(result.output);
  if (manifest.name !== NPM_CANDIDATE.identity || manifest.version !== NPM_CANDIDATE.version) throw new Error('npm candidate identity mismatch');
  const child = spawn('node', ['--input-type=module', '--eval', registry, archive], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] });
  const url = await new Promise((resolveUrl, reject) => { const timeout = setTimeout(() => reject(new Error('npm registry did not bind')), 5000); child.stdout.once('data', (value) => { clearTimeout(timeout); resolveUrl(value.toString('utf8').trim()); }); child.once('error', reject); });
  return { child, url, manifest };
}
async function candidateMetadata(directory) {
  const assets = JSON.parse(await readFile(join(directory, 'obj', 'project.assets.json'), 'utf8')); const packageRoot = join(directory, '.nuget', 'packages');
  return Promise.all(CANDIDATES.map(async (candidate) => { const key = (candidate.identity + '/' + candidate.version).toLowerCase(); const library = Object.entries(assets.libraries ?? {}).find(([name]) => name.toLowerCase() === key)?.[1]; const metadata = JSON.parse(await readFile(join(packageRoot, candidate.identity.toLowerCase(), candidate.version.toLowerCase(), '.nupkg.metadata'), 'utf8')); if (!library || library.type !== 'package' || metadata.source !== FEED_PATH || metadata.contentHash !== library.sha512) throw new Error('NuGet provenance failed closed for ' + candidate.identity); return { ...candidate, source: NUGET_FEED, contentHash: metadata.contentHash }; }));
}
async function startHost(directory, environment) {
  const child = spawn('dotnet', ['run', '--project', 'HostConsumer.csproj', '--no-build', '--configuration', 'Release'], { cwd: directory, env: { ...process.env, ...environment }, stdio: ['ignore', 'pipe', 'pipe'] }); const output = [];
  child.stdout.on('data', (value) => output.push(value)); child.stderr.on('data', (value) => output.push(value));
  const url = await new Promise((resolveUrl, reject) => { const timeout = setTimeout(() => reject(new Error('package host did not start')), 10000); child.stdout.on('data', (value) => { const match = value.toString('utf8').match(/READY (http:\/\/[^\s]+)/); if (match) { clearTimeout(timeout); resolveUrl(match[1]); } }); child.once('error', reject); child.once('exit', (code) => reject(new Error('package host exited ' + code + ': ' + Buffer.concat(output).toString('utf8')))); });
  return { child, url };
}
export function verifyReceipt(receipt, authority) {
  const errors = [];
  if (receipt?.schema !== RECEIPT_SCHEMA) errors.push('receipt schema mismatch');
  if (!same(receipt?.feeds, { nuget: NUGET_FEED, npm: NPM_FEED })) errors.push('feed mismatch');
  if (!same(receipt?.isolation, isolation)) errors.push('cache isolation mismatch');
  if (!same(receipt?.releaseManifest, authority)) errors.push('release authority mismatch');
  if (!same(receipt?.bridgeManifest, MANIFEST)) errors.push('bridge manifest mismatch');
  if (!Array.isArray(receipt?.fixtures) || receipt.fixtures.length !== fixtureNames.length || receipt.fixtures.some((item) => typeof item.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(item.sha256))) errors.push('conformance fixture mismatch');
  if (!Array.isArray(receipt?.nugetCandidates) || receipt.nugetCandidates.length !== CANDIDATES.length || receipt.nugetCandidates.some((item, index) => !same({ identity: item?.identity, version: item?.version, source: item?.source }, { ...CANDIDATES[index], source: NUGET_FEED }) || !item.contentHash)) errors.push('NuGet provenance mismatch');
  if (!same({ identity: receipt?.npmCandidate?.identity, version: receipt?.npmCandidate?.version, source: receipt?.npmCandidate?.source }, { ...NPM_CANDIDATE, source: NPM_FEED }) || !receipt?.npmCandidate?.integrity || !/^[a-f0-9]{64}$/.test(receipt?.npmCandidate?.archiveSha256 ?? '')) errors.push('npm provenance mismatch');
  const expected = [['generated-contract', ['node', '<authoritative-generator>', 'check', '--source', 'application.bridge.ts', '--ir', 'generated/bridge.ir.json', '--facade', 'generated/application.bridge.generated.ts']], ['generated-fixtures', ['node', 'verify-fixtures.mjs']], ['restore', ['dotnet', 'restore', 'HostConsumer.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']], ['build', ['dotnet', 'build', 'HostConsumer.csproj', '--no-restore', '--configuration', 'Release', '--nologo']], ['npm-install', ['npm', 'install', '--ignore-scripts']], ['transport', ['node', 'client.mjs', '<local-host>', 'generated/bridge.ir.json', 'fixtures']], ['controlled-teardown', ['POST', '<local-host>/shutdown']]];
  if (!Array.isArray(receipt?.phases) || receipt.phases.length !== expected.length) errors.push('phase evidence malformed');
  else expected.forEach(([name, argv], index) => { const item = receipt.phases[index]; if (!item || item.name !== name || !same(item.argv, argv) || item.status !== 'passed' || item.exitCode !== 0 || item.reasonCode !== null) errors.push(name + ' evidence malformed'); });
  return { ok: errors.length === 0, errors };
}
export function verifyRepeatedReceipt(receipt, authority) {
  const errors = [];
  if (receipt?.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push('repeat receipt schema mismatch');
  else { receipt.journeys.forEach((journey, index) => errors.push(...verifyReceipt(journey, authority).errors.map((error) => 'journey ' + (index + 1) + ': ' + error))); if (!same(receipt.journeys[0], receipt.journeys[1])) errors.push('host transport journeys are not deterministic'); }
  return { ok: errors.length === 0, errors };
}
export async function runCurrentHostTransport(manifestPath) {
  requireCandidateFeed();
  const authority = await releaseManifestFacts(manifestPath); const directory = await mkdtemp(join(tmpdir(), 'runic-current-host-transport-')); const environment = { DOTNET_CLI_HOME: join(directory, '.dotnet'), NUGET_PACKAGES: join(directory, '.nuget', 'packages'), NUGET_HTTP_CACHE_PATH: join(directory, '.nuget', 'http-cache'), npm_config_cache: join(directory, '.npm-cache') }; let localRegistry; let host;
  try {
    localRegistry = await startRegistry(); await mkdir(join(directory, 'fixtures'));
    await Promise.all([writeFile(join(directory, 'NuGet.config'), config(environment.NUGET_PACKAGES)), writeFile(join(directory, 'HostConsumer.csproj'), project), writeFile(join(directory, 'Program.cs'), program), cp(join(import.meta.dirname, 'generated'), join(directory, 'generated'), { recursive: true }), writeFile(join(directory, 'client.mjs'), client), writeFile(join(directory, 'package.json'), JSON.stringify({ private: true, type: 'module', dependencies: { '@runic-artifex/application-bridge': localRegistry.manifest.version, effect: '3.22.1' } }, null, 2) + '\n'), writeFile(join(directory, '.npmrc'), '@runic-artifex:registry=' + localRegistry.url + '\n')]);
    for (const name of fixtureNames) await writeFile(join(directory, 'fixtures', name), await readFile(join(import.meta.dirname, 'fixtures', name)));
    const phases = [];
    const generationArgs = [contractGenerator, 'check', '--source', join(import.meta.dirname, 'application.bridge.ts'), '--ir', join(import.meta.dirname, 'generated', 'bridge.ir.json'), '--facade', join(import.meta.dirname, 'generated', 'application.bridge.generated.ts')];
    const generated = await run('node', generationArgs, root, environment);
    phases.push(phase('generated-contract', ['node', '<authoritative-generator>', 'check', '--source', 'application.bridge.ts', '--ir', 'generated/bridge.ir.json', '--facade', 'generated/application.bridge.generated.ts'], generated)); requireSuccess('generated contract', generated);
    const fixtureCheck = await run('node', ['verify-fixtures.mjs'], import.meta.dirname, environment);
    phases.push(phase('generated-fixtures', ['node', 'verify-fixtures.mjs'], fixtureCheck)); requireSuccess('generated fixtures', fixtureCheck);
    for (const [name, command, args] of [['restore', 'dotnet', ['restore', 'HostConsumer.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']], ['build', 'dotnet', ['build', 'HostConsumer.csproj', '--no-restore', '--configuration', 'Release', '--nologo']], ['npm-install', 'npm', ['install', '--ignore-scripts']]]) { const result = await run(command, args, directory, environment); phases.push(phase(name, [command, ...args], result)); requireSuccess(name, result); }
    const nugetCandidates = await candidateMetadata(directory); host = await startHost(directory, environment);
    const clientResult = await run('node', ['client.mjs', host.url.replace(/^http/, 'ws') + '/bridge', join(directory, 'generated', 'bridge.ir.json'), join(directory, 'fixtures')], directory, environment); phases.push(phase('transport', ['node', 'client.mjs', '<local-host>', 'generated/bridge.ir.json', 'fixtures'], clientResult)); requireSuccess('transport', clientResult);
    const stop = await run('node', ['--input-type=module', '--eval', "const response=await fetch(process.argv[1],{method:'POST'});if(!response.ok)process.exit(1);", host.url + '/shutdown'], directory, environment); phases.push(phase('controlled-teardown', ['POST', '<local-host>/shutdown'], stop)); requireSuccess('controlled teardown', stop);
    const exitCode = await new Promise((resolveExit) => host.child.once('exit', resolveExit)); if (exitCode !== 0) throw new Error('package host teardown exited ' + exitCode);
    const fixtures = await Promise.all(fixtureNames.map(async (name) => ({ name, sha256: await hash(join(directory, 'fixtures', name)) })));
    const lock = JSON.parse(await readFile(join(directory, 'package-lock.json'), 'utf8')); const entry = lock.packages?.['node_modules/@runic-artifex/application-bridge'];
    if (!entry || entry.version !== localRegistry.manifest.version || !entry.resolved?.startsWith(localRegistry.url) || !entry.integrity) throw new Error('npm provenance failed closed');
    const receipt = { schema: RECEIPT_SCHEMA, feeds: { nuget: NUGET_FEED, npm: NPM_FEED }, isolation, releaseManifest: await releaseManifestAfter(manifestPath, authority), bridgeManifest: MANIFEST, fixtures, nugetCandidates, npmCandidate: { ...NPM_CANDIDATE, source: NPM_FEED, integrity: entry.integrity, archiveSha256: await hash(archive) }, phases };
    const report = verifyReceipt(receipt, receipt.releaseManifest); if (!report.ok) throw new Error(report.errors.join('\n')); return receipt;
  } finally { if (host?.child.exitCode === null) host.child.kill('SIGTERM'); if (localRegistry) localRegistry.child.kill('SIGTERM'); await rm(directory, { recursive: true, force: true }); }
}
export async function runCurrentHostTransportTwice(manifestPath) {
  const journeys = [await runCurrentHostTransport(manifestPath), await runCurrentHostTransport(manifestPath)]; const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys }; const report = verifyRepeatedReceipt(receipt, journeys[0].releaseManifest); if (!report.ok) throw new Error(report.errors.join('\n')); return receipt;
}
async function main() {
  const [command, manifestPath, receiptPath] = process.argv.slice(2);
  if (command === 'run-twice' && manifestPath && !receiptPath) return process.stdout.write(JSON.stringify(await runCurrentHostTransportTwice(manifestPath), null, 2) + '\n');
  if (command === 'verify-twice' && manifestPath && receiptPath) { const report = verifyRepeatedReceipt(JSON.parse(await readFile(receiptPath, 'utf8')), await releaseManifestFacts(manifestPath)); if (!report.ok) throw new Error(report.errors.join('\n')); return; }
  throw new Error('Usage: node eng/current-host-transport/verify.mjs run-twice <runic.release.json> | verify-twice <runic.release.json> <receipt.json>');
}
if (import.meta.main) main().catch((error) => { process.stderr.write(error.message + '\n'); process.exitCode = 1; });
