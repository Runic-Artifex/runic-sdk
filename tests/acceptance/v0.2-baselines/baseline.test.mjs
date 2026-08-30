import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { EventEmitter } from 'node:events';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { BrowserCleanupError, startupSample, warmReloadSamples } from './browser-probe.mjs';
import { dynamicMeasurements, helpText, measure } from './measure.mjs';
import { EXAMPLES_REVISION, exec, isReleaseAuthorityRoot, releaseManifestFacts } from './metrics.mjs';
import { verifyReceipt } from './verify.mjs';

const examplesRoot = path.resolve(import.meta.dirname, '../..');
const authorityFixturePackages = [
  { identity: 'Runic.Application', ecosystem: 'nuget', state: 'approved' },
  { identity: 'Runic.Assets', ecosystem: 'nuget', state: 'approved' },
  { identity: '@runic-artifex/application-bridge', ecosystem: 'npm', state: 'approved' },
  { identity: 'Runic.Conditional', ecosystem: 'nuget', state: 'conditional' }
];
let authorityFixture;
function publicPackageCounts(canonicalPackages) {
  return Object.fromEntries(['nuget', 'npm'].map(ecosystem => [ecosystem, canonicalPackages.filter(item => item.state === 'approved' && item.ecosystem === ecosystem).length]));
}
function child() { const value = new EventEmitter(); value.pid = undefined; value.closed = false; value.kill = () => queueMicrotask(() => { value.closed = true; value.emit('close', 0); }); return value; }
const fetchOk = async () => ({ ok: true });
function commonProbe(calls, children = []) { return { chromePath: 'chromium', serverCommand: 'npm', serverArgs: ['run', 'dev:mock'], cwd: process.cwd(), spawnFn: (command, args) => { calls.push([command, ...args]); const value = child(); children.push(value); return value; }, fetchFn: fetchOk, cdpFn: async (_port, expression) => { calls.push(['cdp', expression]); return true; } }; }
async function extractHistoricalExamples(destination) {
  const archive = path.join(destination, 'examples.tar');
  assert.equal((await exec('git', ['archive', '--format=tar', '--output', archive, EXAMPLES_REVISION], examplesRoot)).ok, true);
  assert.equal((await exec('tar', ['-xf', archive, '-C', destination], examplesRoot)).ok, true);
  await rm(archive, { force: true });
}

test('startup timing starts with launch and stops at rendered visibility', async () => {
  const calls = []; const children = []; let tick = 0n; const clock = () => { calls.push(['clock']); tick += 10n; return tick; }; const result = await startupSample({ ...commonProbe(calls, children), clock });
  assert.equal(result.nanoseconds, 10);
  assert.deepEqual(result.argv, ['browser-dom-probe', 'startup', '--server-port=5173', '--cdp-port=9222', '--strictPort', '--isolated-profile']);
  assert.equal(calls[0][0], 'clock');
  assert.equal(calls[1][0], 'npm');
  const visible = calls.findIndex(call => call[0] === 'cdp' && call[1].includes('[data-e2e-view]'));
  assert.ok(visible > 0);
  assert.ok(calls.slice(visible + 1).some(call => call[0] === 'clock'));
  assert.ok(children.every(value => value.closed));
});

test('browser teardown fails closed when a child never confirms close', async () => {
  const calls = []; const nonClosing = new EventEmitter(); nonClosing.pid = undefined; nonClosing.kill = () => {};
  let spawns = 0;
  await assert.rejects(startupSample({ ...commonProbe(calls), closeTimeoutMs: 5, spawnFn: (command, args) => { calls.push([command, ...args]); return ++spawns === 1 ? nonClosing : child(); } }), error => error instanceof BrowserCleanupError && error.code === 'browser-cleanup-failed');
  assert.equal(spawns, 2);
});

test('warm HMR waits for the exact rendered token in one browser session', async () => {
  const calls = []; const children = []; const tokens = []; let tick = 0n; const result = await warmReloadSamples({ ...commonProbe(calls, children), clock: () => ++tick, warmups: 1, samples: 2, writeToken: async token => tokens.push(token) });
  assert.equal(result.observations.length, 2);
  assert.equal(tokens.length, 3);
  assert.equal(calls.filter(call => call[0] === 'npm').length, 1);
  for (const token of tokens) assert.ok(calls.some(call => call[0] === 'cdp' && call[1].includes(JSON.stringify(token))));
  assert.ok(children.every(value => value.closed));
});

test('archived dynamic measurements are disabled before a package command can run', async () => {
  const scratch = await mkdtemp(path.join(tmpdir(), 'runic-v02-sequence-'));
  try {
    await extractHistoricalExamples(scratch);
    const calls = []; const runner = async (command, args) => { calls.push([command, ...args]); return { ok: true }; };
    await assert.rejects(dynamicMeasurements({ directory: scratch }, runner), /disabled/);
    assert.deepEqual(calls, []);
    assert.match(helpText, /Dynamic measurement is disabled/);
  } finally { await rm(scratch, { recursive: true, force: true }); }
});

test('release authority root is derived from Git rather than host path spelling', () => {
  assert.equal(isReleaseAuthorityRoot('runic.release.json', ''), true);
  assert.equal(isReleaseAuthorityRoot('runic.release.json', 'nested/'), false);
  assert.equal(isReleaseAuthorityRoot('other.release.json', ''), false);
});


function passedMetric(id, category, unit, argv, details, warmups = 0, samples = 0) { const observations = Array.from({ length: samples }, (_, index) => index + 1); return { id, category, status: 'passed', required: true, unit, argv, cwd: 'examples-archive', clock: samples ? 'process.hrtime.bigint-monotonic-nanoseconds' : 'none', warmups, samples, observations, summary: samples ? { min: 1, p50: Math.ceil(samples / 2), p95: Math.ceil(samples * .95), max: samples } : null, details, reasonCode: null }; }
function passingCanary(name, packageReferences) { const project = `integrations/${name}/${name}.csproj`; const restore = ['dotnet', 'restore', project, '--no-cache', '--force-evaluate', '--configfile', 'eng/v0.2-baselines/scratch.NuGet.config']; const phases = [['restore', restore], ['build', ['dotnet', 'build', project, '--no-restore', '--configuration', 'Release']], ['run', ['dotnet', 'run', '--project', project, '--no-build', '--configuration', 'Release']]].map(([name, argv]) => ({ name, status: 'passed', reasonCode: null, exitCode: 0, argv, cwd: 'examples-archive' })); return { name: project.split('/')[1], project, packageReferences, projectReferences: 0, status: 'passed', phases }; }
async function fixtureAuthorityPublicPackageCounts() {
  authorityFixture ??= (async () => {
    const root = await mkdtemp(path.join(tmpdir(), 'runic-release-authority-'));
    await initializeRepository(root);
    await writeReleaseAuthority(root);
    await commit(root, 'valid');
    const facts = await releaseManifestFacts(path.join(root, 'runic.release.json'));
    const manifest = JSON.parse(await readFile(path.join(root, 'runic.release.json'), 'utf8'));
    assert.equal(manifest.canonicalPackages.some(item => /(?:Flow|Operations)/i.test(item.identity)), false);
    return { root, facts, manifest };
  })();
  return (await authorityFixture).facts.publicPackageCounts;
}
test.after(async () => {
  if (authorityFixture) await rm((await authorityFixture).root, { recursive: true, force: true });
});
async function archivedDirectoryPackages() {
  const result = await exec('git', ['show', `${EXAMPLES_REVISION}:Directory.Packages.props`], examplesRoot, { captureStdout: true });
  assert.equal(result.ok, true);
  return result.stdout;
}
async function archivedPackageLock() {
  return JSON.parse(execFileSync('git', ['show', `${EXAMPLES_REVISION}:package-lock.json`], { cwd: examplesRoot, encoding: 'utf8' }));
}
async function plausiblePassedReceipt() {
  const lock = await archivedPackageLock(); const props = await archivedDirectoryPackages();
  const releaseSnapshot = { revision: 'c'.repeat(40), tree: 'e'.repeat(40), status: '' };
  const releaseManifest = { path: 'runic.release.json', revision: releaseSnapshot.revision, tree: releaseSnapshot.tree, digest: 'd'.repeat(64), publicPackageCounts: await fixtureAuthorityPublicPackageCounts(), before: releaseSnapshot, after: structuredClone(releaseSnapshot) };
  const npm = Object.entries(lock.packages).filter(([location, value]) => location.startsWith('node_modules/') && value.version).map(([location, value]) => `${location.slice('node_modules/'.length)}@${value.version}`).sort(); const nuget = [...props.matchAll(/<PackageVersion Include="([^"]+)" Version="([^"]+)"/g)].map(match => `${match[1]}@${match[2]}`).sort();
  const sourceSnapshot = { revision: 'a'.repeat(40), tree: 'b'.repeat(40), status: '' };
  const value = { schema: 'runic.v0.2-baseline/2', source: { examples: { revision: '494b7325d08ba405713f6ea0fe26680772caa3f1', tree: '773e3e19cfceb365bd3feb1357f61645b31594b4', before: sourceSnapshot, after: structuredClone(sourceSnapshot) }, editor: { revision: '5bcb157004deaf196a3dc8e6c7d911d7c6f881d7', tree: '7741959d4342bbe9c486e9018b9668e1f866346e', before: sourceSnapshot, after: structuredClone(sourceSnapshot) }, releaseManifest: structuredClone(releaseManifest) }, environment: { os: 'linux', arch: 'x64', tools: Object.fromEntries(['node', 'npm', 'dotnet', 'chromium', 'git', 'tar', 'cc', 'linker', 'file', 'readelf'].map(name => [name, { path: `/tools/${name}`, version: name === 'node' ? 'v24.18.9' : name === 'npm' ? '11.16.9' : name === 'dotnet' ? '10.0.302' : 'fixture 1' }])), packageIdentities: { npm, nuget }, hashes: { 'package-lock.json': 'b54c5c8a43ac6c01dbf31a3ed9ae112557710a9579741071c7621e8b14f78add', 'NuGet.config': 'ce7dcf4ab8aa97ba4256835be27f960bf53eacf56a02d15e3cd57093aa415b3e', 'Directory.Packages.props': '1aa5fa401787d1bcd3a61f35cbfcf76ae6cfa794843d4f3f2dd95c4f843fbdd6', 'global.json': '0588e32d44bdf884e0305ded21820b13a59b1518efd39976c34d909eff3b1044', 'eng/v0.2-baselines/scratch.NuGet.config': '7760ea6315dc1cf1f8714197dd316a225bd8372c5b93bb3375d24409521f04fd', 'editor/NuGet.config': 'f7773717237080b7dbc6ea6ffea4fbe2cf368555e6979db479524766656fb799', 'editor/global.json': '3d84f5a3e38455517663d0c8369deb32b5d76b02b1d012040d6c7cb29d0316fa' } }, methodology: { version: 2, clock: 'process.hrtime.bigint-monotonic-nanoseconds', volatilePaths: ['measurements.clean-frontend-build.observations', 'measurements.clean-frontend-build.summary', 'measurements.change-to-visible-reload.observations', 'measurements.change-to-visible-reload.summary', 'measurements.launch-to-visible-startup.observations', 'measurements.launch-to-visible-startup.summary'] }, measurements: [] };
  const passedStatic = (id, unit, details) => ({ id, category: 'static', status: 'passed', required: true, unit, argv: ['static-source-inspection'], cwd: 'archive', clock: 'none', warmups: 0, samples: 0, observations: [], summary: null, details, reasonCode: null });
  const statics = [passedStatic('creation-ceremony', 'commands', { expected: 3, commands: ['npm ci', 'npm run verify', 'dotnet run --project samples/04-SvelteKitSetupApplication'], noTemplate: true }), passedStatic('host-wiring', 'markers', { expected: 7, markers: ['string webRoot =', 'new FrontendAssetManifestBuilder()', 'new DirectoryFrontendAssetProvider(', 'WebUiApp.CreateBuilder(args)', 'new ApplicationBridgeFrontendApplicationOptions(', 'builder.UseApplicationBridge(', 'builder.RunAsync()'] }), passedStatic('starter-package-references', 'references', { dotnet: ['RunicToolkit.ApplicationBridge', 'RunicToolkit.ApplicationBridge.Generators', 'RunicToolkit.Hosting.Build', 'RunicToolkit.Hosting.CsWebUi.App', 'RunicToolkit.Hosting.CsWebUi.ApplicationBridge'], npm: ['@runic-artifex/application-bridge', '@runic-artifex/svelte', '@runic-artifex/sveltekit', '@runic-artifex/vite-plugin-runic-toolkit'], expectedDotnet: 5, expectedNpm: 4 }), passedStatic('consumer-package-pins', 'pins', { pins: ['RunicAssets', 'RunicAssets.AspNetCore', 'RunicAssets.CsWebUi', 'RunicAssets.RunicToolkit', 'RunicCommandLine', 'RunicCommandLine.Abstractions', 'RunicCommandLine.Hosting', 'RunicCommandLine.Processes', 'RunicFlow', 'RunicFlow.ApplicationBridge', 'RunicToolkit.ApplicationBridge', 'RunicToolkit.ApplicationBridge.Generators', 'RunicToolkit.Collections', 'RunicToolkit.Hosting', 'RunicToolkit.Hosting.Abstractions', 'RunicToolkit.Hosting.Build', 'RunicToolkit.Hosting.CsWebUi', 'RunicToolkit.Hosting.CsWebUi.App', 'RunicToolkit.Hosting.CsWebUi.ApplicationBridge', 'RunicToolkit.Hosting.GenericHost', 'RunicToolkit.Hosting.WebUi', 'RunicTranslations', 'RunicTranslations.Build', 'RunicTranslations.Generator'], expected: 24 }), passedStatic('editor-orchestration', 'bindings', { bindings: 15, parserHelpers: 2, frontendExecs: 2, directPackageReferences: ['CsWebUi', 'RunicTranslations.Authoring', 'RunicTranslations.Build', 'RunicTranslations.Compiler'], expected: { bindings: 15, parserHelpers: 2, frontendExecs: 2, directPackageReferences: 4 } })];
  const canaries = [passingCanary('RunicAssets.Canary', ['RunicAssets', 'RunicAssets.CsWebUi', 'RunicAssets.AspNetCore', 'RunicAssets.RunicToolkit', 'RunicToolkit.Hosting.Abstractions']), passingCanary('RunicCommandLine.Canary', ['RunicCommandLine.Abstractions', 'RunicCommandLine', 'RunicCommandLine.Hosting', 'RunicCommandLine.Processes']), passingCanary('RunicFlow.Canary', ['RunicFlow', 'RunicFlow.ApplicationBridge']), passingCanary('RunicTranslations.Canary', ['RunicTranslations', 'RunicTranslations.Build', 'RunicTranslations.Generator'])];
  const aotPublish = ['dotnet', 'publish', 'samples/04-SvelteKitSetupApplication/SvelteKitSetupApplication.csproj', '--no-restore', '--runtime', 'linux-x64', '--self-contained', 'true', '--configuration', 'Release', '--output', '.baseline-nativeaot', '-p:PublishAot=true', '-p:PublishTrimmed=true', '-p:TrimMode=full', '-p:IlcTreatWarningsAsErrors=true', '-p:RunicToolkitFrontendBuildCommand='];
  const aotRestore = ['dotnet', 'restore', 'samples/04-SvelteKitSetupApplication/SvelteKitSetupApplication.csproj', '--runtime', 'linux-x64', '--no-cache', '--force-evaluate', '--configfile', 'eng/v0.2-baselines/scratch.NuGet.config', '-p:PublishAot=true', '-p:PublishTrimmed=true', '-p:TrimMode=full', '-p:IlcTreatWarningsAsErrors=true'];
  const phase = (name, argv) => ({ name, status: 'passed', reasonCode: null, exitCode: 0, argv, cwd: 'examples-archive' });
  const dynamics = [passedMetric('typescript-consumer', 'typescript', 'exit-code', ['npm', 'run', 'typecheck'], { strictConsumer: true, exitCode: 0 }), passedMetric('clean-frontend-build', 'build', 'nanoseconds', ['npm', 'run', 'build', '--workspace', '@runic-artifex/sveltekit-setup-application'], { preparation: ['npm', 'ci'], cleanOutputs: ['Frontend/build', 'Frontend/.svelte-kit'] }, 1, 5), passedMetric('change-to-visible-reload', 'reload', 'nanoseconds', ['browser-dom-probe', 'reload', '--server-port=5173', '--cdp-port=9222', '--strictPort', '--isolated-profile'], { signal: 'unique-file-write-to-exact-rendered-token', renderedVisibility: 'element+computed-style+nonzero-rect', port: 5173 }, 3, 20), passedMetric('launch-to-visible-startup', 'startup', 'nanoseconds', ['browser-dom-probe', 'startup', '--server-port=5173', '--cdp-port=9222', '--strictPort', '--isolated-profile'], { signal: 'process-launch-to-rendered-visible-DOM', renderedVisibility: 'element+computed-style+nonzero-rect', port: 5173 }, 3, 10), passedMetric('package-only-canaries', 'canary', 'canaries', ['dotnet', 'restore/build/run'], { expectedCanaries: 4, expectedPackageReferences: 14, expectedProjectReferences: 0, totalPackageReferences: 14, canaries }), passedMetric('nativeaot-linux-x64', 'nativeaot', 'bytes', aotPublish, { rid: 'linux-x64', inspection: { file: 'ELF 64-bit LSB pie executable, x86-64', readelf: 'Class: ELF64\nMachine: Advanced Micro Devices X86-64' }, inventory: { files: [{ path: 'SvelteKitSetupApplication', bytes: 1, mode: 493 }], totalBytes: 1, entryBytes: 1, entryMode: 493 }, phases: [phase('restore', aotRestore), phase('publish', aotPublish), phase('smoke', ['.baseline-nativeaot/SvelteKitSetupApplication', '--smoke-test']), phase('file', ['file', '--brief', '.baseline-nativeaot/SvelteKitSetupApplication']), phase('readelf', ['readelf', '--file-header', '.baseline-nativeaot/SvelteKitSetupApplication'])] })];
  value.measurements = [...statics, ...dynamics].sort((a, b) => a.id.localeCompare(b.id));
  return { value, releaseManifest };
}

test('archived receipt measurement rejects dynamic mode before it reads a checkout', async () => {
  await assert.rejects(measure({ dynamic: true }), /disabled/);
});

test('receipt verification rejects a plausible all-passed forged canary receipt', async () => {
  const { value, releaseManifest } = await plausiblePassedReceipt();
  assert.deepEqual(verifyReceipt(value, releaseManifest).errors, []);
  // This retains every former coarse count/status but replaces the package
  // evidence and restore command; the prior verifier accepted that forgery.
  value.measurements.find(item => item.id === 'package-only-canaries').details.canaries[0].packageReferences[0] = 'Fabricated.Package';
  const forgedArgv = value.measurements.find(item => item.id === 'package-only-canaries').details.canaries[0].phases[0].argv; forgedArgv[forgedArgv.length - 1] = '/tmp/forged.NuGet.config';
  const report = verifyReceipt(value, releaseManifest);
  assert.equal(report.ok, false);
  assert.match(report.errors.join('\n'), /RunicAssets\.Canary (identity|restore phase) malformed/);
});

test('release manifest identity and public package counts are exact and independent of consumer pins', async () => {
  const { value, releaseManifest } = await plausiblePassedReceipt();
  assert.deepEqual(releaseManifest.publicPackageCounts, await fixtureAuthorityPublicPackageCounts());
  const expected = structuredClone(releaseManifest);
  value.source.releaseManifest.publicPackageCounts.nuget += 1;
  assert.match(verifyReceipt(value, expected).errors.join('\n'), /release manifest identity or authoritative public package counts mismatch/);
  value.source.releaseManifest.publicPackageCounts.nuget = expected.publicPackageCounts.nuget;
  value.source.releaseManifest.revision = '0'.repeat(40);
  assert.match(verifyReceipt(value, expected).errors.join('\n'), /release manifest identity or authoritative public package counts mismatch/);
  value.source.releaseManifest.revision = releaseManifest.revision;
  value.source.releaseManifest.digest = '0'.repeat(64);
  assert.match(verifyReceipt(value, expected).errors.join('\n'), /release manifest identity or authoritative public package counts mismatch/);
});

test('release manifest receipt fields reject malformed revisions, digests, and negative counts', async () => {
  const { value, releaseManifest } = await plausiblePassedReceipt();
  value.source.releaseManifest.revision = 'not-a-revision';
  assert.match(verifyReceipt(value, releaseManifest).errors.join('\n'), /revision/);
  value.source.releaseManifest.revision = releaseManifest.revision;
  value.source.releaseManifest.digest = 'not-a-digest';
  assert.match(verifyReceipt(value, releaseManifest).errors.join('\n'), /digest/);
  value.source.releaseManifest.digest = releaseManifest.digest;
  value.source.releaseManifest.publicPackageCounts.nuget = -1;
  assert.match(verifyReceipt(value, releaseManifest).errors.join('\n'), /nuget/);
});

async function initializeRepository(root) {
  assert.equal((await exec('git', ['init', '--quiet'], root)).ok, true);
  assert.equal((await exec('git', ['config', 'user.name', 'Baseline'], root)).ok, true);
  assert.equal((await exec('git', ['config', 'user.email', 'baseline@example.invalid'], root)).ok, true);
}
async function commit(root, message) {
  assert.equal((await exec('git', ['add', '.'], root)).ok, true);
  assert.equal((await exec('git', ['commit', '--quiet', '-m', message], root)).ok, true);
}
async function writeReleaseAuthority(root, semanticValid = true) {
  const canonicalPackages = structuredClone(authorityFixturePackages);
  await mkdir(path.join(root, 'eng'), { recursive: true });
  await writeFile(path.join(root, 'runic.release.json'), JSON.stringify({ $schema: './runic.release.schema.json', schemaVersion: 1, semanticValid, canonicalPackages }));
  await writeFile(path.join(root, 'runic.release.schema.json'), JSON.stringify({ contract: 'release-fixture-v1', required: ['schemaVersion', 'semanticValid', 'canonicalPackages'] }));
  await writeFile(path.join(root, 'eng/verify-release-manifest.mjs'), `import { readFileSync } from 'node:fs';\nconst [manifestPath, schemaPath] = process.argv.slice(2);\nconst manifest = JSON.parse(readFileSync(manifestPath)); const schema = JSON.parse(readFileSync(schemaPath));\nif (schema.contract !== 'release-fixture-v1' || !Array.isArray(schema.required) || manifest.schemaVersion !== 1 || manifest.semanticValid !== true || !Array.isArray(manifest.canonicalPackages)) process.exitCode = 1;\n`);
}

test('release manifest facts reject incomplete and semantically invalid committed fixtures', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'runic-release-manifest-'));
  try {
    await initializeRepository(root);
    await writeFile(path.join(root, 'runic.release.json'), JSON.stringify({ canonicalPackages: [] }));
    await commit(root, 'incomplete');
    await assert.rejects(releaseManifestFacts(path.join(root, 'runic.release.json')), /authority-inputs-must-be-tracked/);
    await writeReleaseAuthority(root, false);
    await commit(root, 'invalid');
    await assert.rejects(releaseManifestFacts(path.join(root, 'runic.release.json')), /semantic-verification-failed/);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('public NuGet and npm counts derive from a clean, semantically valid committed release authority', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'runic-release-manifest-'));
  try {
    await initializeRepository(root);
    await writeReleaseAuthority(root);
    await commit(root, 'valid');
    const facts = await releaseManifestFacts(path.join(root, 'runic.release.json'));
    assert.equal(facts.path, 'runic.release.json');
    assert.match(facts.revision, /^[0-9a-f]{40}$/);
    assert.match(facts.tree, /^[0-9a-f]{40}$/);
    assert.match(facts.digest, /^[0-9a-f]{64}$/);
    assert.equal(JSON.stringify(JSON.parse(await readFile(path.join(root, 'runic.release.json'), 'utf8')).canonicalPackages).includes('Flow'), false);
    assert.deepEqual(facts.publicPackageCounts, publicPackageCounts(authorityFixturePackages));
    assert.deepEqual(facts.before, facts.after);
    await writeFile(path.join(root, 'runic.release.json'), `${await readFile(path.join(root, 'runic.release.json'), 'utf8')}\n`);
    await assert.rejects(releaseManifestFacts(path.join(root, 'runic.release.json')), /source-not-clean/);
  } finally { await rm(root, { recursive: true, force: true }); }
});
