#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { promises as fs } from 'node:fs';
import process from 'node:process';
import { decodeReceipt, stableJson } from './contract.mjs';
import { CLOCK, EDITOR_REVISION, EDITOR_TREE, EXAMPLES_REVISION, EXAMPLES_TREE, REQUIRED_METRICS, releaseManifestFacts, timingSummary } from './metrics.mjs';

const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const digest = value => createHash('sha256').update(JSON.stringify(value)).digest('hex');
const timing = new Set(['clean-frontend-build', 'change-to-visible-reload', 'launch-to-visible-startup']);
const volatilePaths = ['measurements.clean-frontend-build.observations', 'measurements.clean-frontend-build.summary', 'measurements.change-to-visible-reload.observations', 'measurements.change-to-visible-reload.summary', 'measurements.launch-to-visible-startup.observations', 'measurements.launch-to-visible-startup.summary'];
const hashes = { 'package-lock.json': 'b54c5c8a43ac6c01dbf31a3ed9ae112557710a9579741071c7621e8b14f78add', 'NuGet.config': 'ce7dcf4ab8aa97ba4256835be27f960bf53eacf56a02d15e3cd57093aa415b3e', 'Directory.Packages.props': '1aa5fa401787d1bcd3a61f35cbfcf76ae6cfa794843d4f3f2dd95c4f843fbdd6', 'global.json': '0588e32d44bdf884e0305ded21820b13a59b1518efd39976c34d909eff3b1044', 'eng/v0.2-baselines/scratch.NuGet.config': '7760ea6315dc1cf1f8714197dd316a225bd8372c5b93bb3375d24409521f04fd', 'editor/NuGet.config': 'f7773717237080b7dbc6ea6ffea4fbe2cf368555e6979db479524766656fb799', 'editor/global.json': '3d84f5a3e38455517663d0c8369deb32b5d76b02b1d012040d6c7cb29d0316fa' };
const staticDetails = {
  'creation-ceremony': { expected: 3, commands: ['npm ci', 'npm run verify', 'dotnet run --project samples/04-SvelteKitSetupApplication'], noTemplate: true },
  'host-wiring': { expected: 7, markers: ['string webRoot =', 'new FrontendAssetManifestBuilder()', 'new DirectoryFrontendAssetProvider(', 'WebUiApp.CreateBuilder(args)', 'new ApplicationBridgeFrontendApplicationOptions(', 'builder.UseApplicationBridge(', 'builder.RunAsync()'] },
  'starter-package-references': { dotnet: ['RunicToolkit.ApplicationBridge', 'RunicToolkit.ApplicationBridge.Generators', 'RunicToolkit.Hosting.Build', 'RunicToolkit.Hosting.CsWebUi.App', 'RunicToolkit.Hosting.CsWebUi.ApplicationBridge'], npm: ['@runic-artifex/application-bridge', '@runic-artifex/svelte', '@runic-artifex/sveltekit', '@runic-artifex/vite-plugin-runic-toolkit'], expectedDotnet: 5, expectedNpm: 4 },
  'consumer-package-pins': { pins: ['RunicAssets', 'RunicAssets.AspNetCore', 'RunicAssets.CsWebUi', 'RunicAssets.RunicToolkit', 'RunicCommandLine', 'RunicCommandLine.Abstractions', 'RunicCommandLine.Hosting', 'RunicCommandLine.Processes', 'RunicFlow', 'RunicFlow.ApplicationBridge', 'RunicToolkit.ApplicationBridge', 'RunicToolkit.ApplicationBridge.Generators', 'RunicToolkit.Collections', 'RunicToolkit.Hosting', 'RunicToolkit.Hosting.Abstractions', 'RunicToolkit.Hosting.Build', 'RunicToolkit.Hosting.CsWebUi', 'RunicToolkit.Hosting.CsWebUi.App', 'RunicToolkit.Hosting.CsWebUi.ApplicationBridge', 'RunicToolkit.Hosting.GenericHost', 'RunicToolkit.Hosting.WebUi', 'RunicTranslations', 'RunicTranslations.Build', 'RunicTranslations.Generator'], expected: 24 },
  'editor-orchestration': { bindings: 15, parserHelpers: 2, frontendExecs: 2, directPackageReferences: ['CsWebUi', 'RunicTranslations.Authoring', 'RunicTranslations.Build', 'RunicTranslations.Compiler'], expected: { bindings: 15, parserHelpers: 2, frontendExecs: 2, directPackageReferences: 4 } }
};
const canaries = [
  ['RunicAssets.Canary', ['RunicAssets', 'RunicAssets.CsWebUi', 'RunicAssets.AspNetCore', 'RunicAssets.RunicToolkit', 'RunicToolkit.Hosting.Abstractions']],
  ['RunicCommandLine.Canary', ['RunicCommandLine.Abstractions', 'RunicCommandLine', 'RunicCommandLine.Hosting', 'RunicCommandLine.Processes']],
  ['RunicFlow.Canary', ['RunicFlow', 'RunicFlow.ApplicationBridge']],
  ['RunicTranslations.Canary', ['RunicTranslations', 'RunicTranslations.Build', 'RunicTranslations.Generator']]
];
const aotProject = 'samples/04-SvelteKitSetupApplication/SvelteKitSetupApplication.csproj';
const aotRestore = ['dotnet', 'restore', aotProject, '--runtime', 'linux-x64', '--no-cache', '--force-evaluate', '--configfile', 'eng/v0.2-baselines/scratch.NuGet.config', '-p:PublishAot=true', '-p:PublishTrimmed=true', '-p:TrimMode=full', '-p:IlcTreatWarningsAsErrors=true'];
const aotPublish = ['dotnet', 'publish', aotProject, '--no-restore', '--runtime', 'linux-x64', '--self-contained', 'true', '--configuration', 'Release', '--output', '.baseline-nativeaot', '-p:PublishAot=true', '-p:PublishTrimmed=true', '-p:TrimMode=full', '-p:IlcTreatWarningsAsErrors=true', '-p:RunicToolkitFrontendBuildCommand='];
const dynamicSpecs = {
  'typescript-consumer': ['typescript', 'exit-code', ['npm', 'run', 'typecheck'], 0, 0, 'none'],
  'clean-frontend-build': ['build', 'nanoseconds', ['npm', 'run', 'build', '--workspace', '@runic-artifex/sveltekit-setup-application'], 1, 5, CLOCK],
  'change-to-visible-reload': ['reload', 'nanoseconds', ['browser-dom-probe', 'reload', '--server-port=5173', '--cdp-port=9222', '--strictPort', '--isolated-profile'], 3, 20, CLOCK],
  'launch-to-visible-startup': ['startup', 'nanoseconds', ['browser-dom-probe', 'startup', '--server-port=5173', '--cdp-port=9222', '--strictPort', '--isolated-profile'], 3, 10, CLOCK],
  'package-only-canaries': ['canary', 'canaries', ['dotnet', 'restore/build/run'], 0, 0, 'none'],
  'nativeaot-linux-x64': ['nativeaot', 'bytes', aotPublish, 0, 0, 'none']
};
function phaseErrors(phases, expected, id, errors) {
  if (!Array.isArray(phases) || phases.length !== expected.length) return errors.push(`${id}: phase evidence malformed`);
  for (let index = 0; index < expected.length; index += 1) {
    const phase = phases[index]; const [name, argv] = expected[index];
    if (!phase || phase.name !== name || phase.status !== 'passed' || phase.reasonCode !== null || phase.exitCode !== 0 || phase.cwd !== 'examples-archive' || !same(phase.argv, argv)) errors.push(`${id}: ${name} phase malformed`);
  }
}
function snapshotErrors(source, label, errors) {
  const valid = value => value && typeof value === 'object' && !Array.isArray(value) && same(Object.keys(value).sort(), ['revision', 'status', 'tree']) && /^[0-9a-f]{40}$/.test(value.revision) && /^[0-9a-f]{40}$/.test(value.tree) && value.status === '';
  if (!valid(source.before) || !valid(source.after) || !same(source.before, source.after)) errors.push(`${label}: live source snapshot must be clean, closed, and unchanged`);
}
function metricShape(item, errors) {
  if (staticDetails[item.id]) {
    const units = { 'creation-ceremony': 'commands', 'host-wiring': 'markers', 'starter-package-references': 'references', 'consumer-package-pins': 'pins', 'editor-orchestration': 'bindings' };
    if (item.category !== 'static' || item.unit !== units[item.id] || !same(item.argv, ['static-source-inspection']) || item.cwd !== 'archive' || item.clock !== 'none' || item.warmups !== 0 || item.samples !== 0 || !same(item.details, staticDetails[item.id])) errors.push(`${item.id}: static evidence malformed`);
    return;
  }
  const spec = dynamicSpecs[item.id]; if (!spec) return errors.push(`unexpected metric: ${item.id}`);
  const [category, unit, argv, warmups, samples, clock] = spec;
  if (item.category !== category || item.unit !== unit || !same(item.argv, argv) || item.cwd !== 'examples-archive' || item.clock !== clock || item.warmups !== warmups || item.samples !== samples) errors.push(`${item.id}: metric command/category/unit/clock malformed`);
}
function dynamicDetails(item, errors) {
  const detail = item.details;
  if (!detail || typeof detail !== 'object') return errors.push(`${item.id}: details malformed`);
  if (item.id === 'typescript-consumer' && (detail.strictConsumer !== true || detail.exitCode !== 0)) errors.push('typescript-consumer details malformed');
  if (item.id === 'clean-frontend-build' && !same(detail, { preparation: ['npm', 'ci'], cleanOutputs: ['Frontend/build', 'Frontend/.svelte-kit'] })) errors.push('clean-frontend-build details malformed');
  if (item.id === 'change-to-visible-reload' && !same(detail, { signal: 'unique-file-write-to-exact-rendered-token', renderedVisibility: 'element+computed-style+nonzero-rect', port: 5173 })) errors.push('change-to-visible-reload details malformed');
  if (item.id === 'launch-to-visible-startup' && !same(detail, { signal: 'process-launch-to-rendered-visible-DOM', renderedVisibility: 'element+computed-style+nonzero-rect', port: 5173 })) errors.push('launch-to-visible-startup details malformed');
  if (item.id === 'package-only-canaries') {
    if (detail.expectedCanaries !== 4 || detail.expectedPackageReferences !== 14 || detail.expectedProjectReferences !== 0 || detail.totalPackageReferences !== 14 || !Array.isArray(detail.canaries) || detail.canaries.length !== canaries.length) return errors.push('package-only-canaries details malformed');
    canaries.forEach(([name, packages], index) => { const actual = detail.canaries[index]; const project = `integrations/${name}/${name}.csproj`; const restore = ['dotnet', 'restore', project, '--no-cache', '--force-evaluate', '--configfile', 'eng/v0.2-baselines/scratch.NuGet.config']; const build = ['dotnet', 'build', project, '--no-restore', '--configuration', 'Release']; const run = ['dotnet', 'run', '--project', project, '--no-build', '--configuration', 'Release']; if (!actual || actual.name !== name || actual.project !== project || actual.status !== 'passed' || actual.projectReferences !== 0 || !same(actual.packageReferences, packages)) errors.push(`package-only-canaries: ${name} identity malformed`); phaseErrors(actual?.phases, [['restore', restore], ['build', build], ['run', run]], `package-only-canaries: ${name}`, errors); });
  }
  if (item.id === 'nativeaot-linux-x64') {
    const inventory = detail.inventory; const file = detail.inspection?.file ?? ''; const readelf = detail.inspection?.readelf ?? '';
    if (detail.rid !== 'linux-x64' || !/ELF\s+64-bit.*(?:x86-64|x86_64)/i.test(file) || !/Class:\s*ELF64/i.test(readelf) || !/Machine:\s*(?:Advanced Micro Devices X86-64|x86-64)/i.test(readelf) || !inventory || !Array.isArray(inventory.files) || inventory.totalBytes !== inventory.files.reduce((total, entry) => total + entry.bytes, 0) || !inventory.files.some(entry => entry.path === 'SvelteKitSetupApplication' && entry.bytes === inventory.entryBytes && (entry.mode & 0o111))) errors.push('nativeaot-linux-x64 details malformed');
    phaseErrors(detail.phases, [['restore', aotRestore], ['publish', aotPublish], ['smoke', ['.baseline-nativeaot/SvelteKitSetupApplication', '--smoke-test']], ['file', ['file', '--brief', '.baseline-nativeaot/SvelteKitSetupApplication']], ['readelf', ['readelf', '--file-header', '.baseline-nativeaot/SvelteKitSetupApplication']]], 'nativeaot-linux-x64', errors);
  }
}
function environmentErrors(environment, errors) {
  if (environment.os !== 'linux' || environment.arch !== 'x64') errors.push('receipt requires linux x64');
  const expectedTools = ['node', 'npm', 'dotnet', 'chromium', 'git', 'tar', 'cc', 'linker', 'file', 'readelf'];
  if (!same(Object.keys(environment.tools).sort(), expectedTools.sort())) errors.push('required tool set mismatch');
  for (const name of expectedTools) if (!environment.tools[name]?.path || !environment.tools[name]?.version) errors.push(`${name}: tool evidence missing`);
  if (!/^v24\.18\.\d+$/.test(environment.tools.node?.version ?? '')) errors.push('Node 24.18.x required');
  if (!/^11\.16\.\d+$/.test(environment.tools.npm?.version ?? '')) errors.push('npm 11.16.x required');
  if (!/^10\.0\.302$/.test(environment.tools.dotnet?.version ?? '')) errors.push('.NET SDK 10.0.302 required');
  if (!same(environment.hashes, hashes)) errors.push('required source/scratch hashes mismatch');
  if (!Array.isArray(environment.packageIdentities.npm) || !Array.isArray(environment.packageIdentities.nuget) || digest(environment.packageIdentities.npm) !== '86c785ed053c0b670c2a7ef89db85a8c7aed2a055defeff1b8b0e344420d5554' || digest(environment.packageIdentities.nuget) !== '749575dfff2f6e6aa28f14a4aa3cdb53bb40dd4b93883c4ec3eb41133b1bf2da') errors.push('package identity digest mismatch');
}
export function verifyReceipt(receipt, expectedReleaseManifest) {
  const decoded = decodeReceipt(receipt); const errors = decoded.ok ? [] : [...decoded.errors]; if (!decoded.ok) return { ok: false, errors };
  const ids = receipt.measurements.map(item => item.id); if (!same(ids, [...REQUIRED_METRICS].sort()) || new Set(ids).size !== ids.length) errors.push('receipt must contain the exact ordered metric set');
  if (receipt.source.examples.revision !== EXAMPLES_REVISION || receipt.source.examples.tree !== EXAMPLES_TREE) errors.push('examples fixed revision/tree mismatch');
  if (receipt.source.editor.revision !== EDITOR_REVISION || receipt.source.editor.tree !== EDITOR_TREE) errors.push('editor fixed revision/tree mismatch');
  if (!expectedReleaseManifest || !same(receipt.source.releaseManifest, expectedReleaseManifest)) errors.push('release manifest identity or authoritative public package counts mismatch');
  snapshotErrors(receipt.source.examples, 'examples', errors); snapshotErrors(receipt.source.editor, 'editor', errors);
  snapshotErrors(receipt.source.releaseManifest, 'release manifest', errors);
  if (!same(receipt.methodology.volatilePaths, volatilePaths)) errors.push('volatile paths mismatch'); environmentErrors(receipt.environment, errors);
  for (const item of receipt.measurements) {
    if (item.required !== true || item.status !== 'passed' || item.reasonCode !== null) errors.push(`${item.id}: required metric is not passed`);
    metricShape(item, errors); dynamicDetails(item, errors);
    if (item.samples !== item.observations.length || (item.samples ? !item.observations.every(value => Number.isSafeInteger(value) && value > 0) || !same(item.summary, timingSummary(item.observations)) : item.observations.length || item.summary !== null)) errors.push(`${item.id}: dynamic evidence malformed`);
  }
  return { ok: errors.length === 0, errors };
}
export function canonicalReceipt(receipt) { const clone = structuredClone(receipt); for (const metric of clone.measurements ?? []) if (timing.has(metric.id)) { delete metric.observations; delete metric.summary; } return stableJson(clone); }
export function compareReceipts(left, right, expectedReleaseManifest) { const a = verifyReceipt(left, expectedReleaseManifest); const b = verifyReceipt(right, expectedReleaseManifest); if (!a.ok || !b.ok) return { ok: false, errors: [...a.errors.map(error => `left: ${error}`), ...b.errors.map(error => `right: ${error}`)] }; return { ok: same(canonicalReceipt(left), canonicalReceipt(right)), errors: same(canonicalReceipt(left), canonicalReceipt(right)) ? [] : ['canonical receipts differ'] }; }
async function main() { const [command, flag, manifestPath, ...paths] = process.argv.slice(2); if ((command !== 'verify' && command !== 'compare') || flag !== '--release-manifest' || !manifestPath || paths.length !== (command === 'verify' ? 1 : 2)) { process.stderr.write('Usage: node verify.mjs verify --release-manifest <runic.release.json> <receipt.json> | compare --release-manifest <runic.release.json> <left.json> <right.json>\n'); process.exitCode = 2; return; } const [releaseManifest, receipts] = await Promise.all([releaseManifestFacts(manifestPath), Promise.all(paths.map(file => fs.readFile(file, 'utf8').then(JSON.parse)))]); const report = command === 'verify' ? verifyReceipt(receipts[0], releaseManifest) : compareReceipts(receipts[0], receipts[1], releaseManifest); if (!report.ok) { process.stderr.write(`${report.errors.join('\n')}\n`); process.exitCode = 1; } }
if (import.meta.main) await main();
