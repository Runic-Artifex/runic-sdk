#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import process from 'node:process';
import { releaseManifestAfter, releaseManifestFacts } from '../v0.2-baselines/metrics.mjs';

export const RECEIPT_SCHEMA = 'runic.current-svelte-template/1';
export const REPEAT_RECEIPT_SCHEMA = 'runic.current-svelte-template-repeat/1';
const root = resolve(import.meta.dirname, '../..');
const configuredNugetFeed = process.env.RUNIC_CURRENT_SVELTE_TEMPLATE_NUGET_FEED;
export const CANDIDATE_FEED_PATH = configuredNugetFeed && resolve(configuredNugetFeed);
const packageVersion = process.env.RUNIC_CURRENT_SVELTE_TEMPLATE_APPLICATION_VERSION ?? '0.2.0-preview.1e8fff0';
const assetsVersion = process.env.RUNIC_CURRENT_SVELTE_TEMPLATE_ASSETS_VERSION ?? '0.1.0-preview.8d22423';
const archives = (process.env.RUNIC_CURRENT_SVELTE_TEMPLATE_NPM_ARCHIVES ?? '').split(',').filter(Boolean).map((archive) => resolve(archive));
export const NUGET_FEED = 'w10-005-local-candidate-feed';
export function requireCandidateFeed(path = CANDIDATE_FEED_PATH) {
  if (!path) throw new Error('RUNIC_CURRENT_SVELTE_TEMPLATE_NUGET_FEED must name an explicit isolated NuGet candidate feed.');
  return path;
}
export const NPM_FEED = 'w10-005-local-candidate-npm-feed';
export const NUGET_CANDIDATES = [
  { identity: 'Runic.Application.Templates', version: packageVersion },
  { identity: 'Runic.Application', version: packageVersion },
  { identity: 'Runic.Application.Desktop', version: packageVersion },
  { identity: 'Runic.Application.Bridge', version: packageVersion },
  { identity: 'Runic.Assets', version: assetsVersion },
  { identity: 'Runic.Assets.Desktop', version: assetsVersion },
  { identity: 'dotnet-runic', version: packageVersion },
];
export const NPM_CANDIDATE_NAMES = ['@runic-artifex/application-bridge', '@runic-artifex/svelte', '@runic-artifex/vite-plugin-runic'];
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);

function run(command, args, cwd, env = {}) {
  return new Promise((resolveResult) => {
    const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
    const output = [];
    child.stdout.on('data', (chunk) => output.push(chunk));
    child.stderr.on('data', (chunk) => output.push(chunk));
    child.on('error', (error) => resolveResult({ ok: false, exitCode: null, reasonCode: error.code === 'ENOENT' ? 'command-not-found' : 'command-spawn-failed', message: '' }));
    child.on('close', (exitCode) => resolveResult({ ok: exitCode === 0, exitCode, reasonCode: exitCode === 0 ? null : 'command-exit-nonzero', message: Buffer.concat(output).toString('utf8').slice(-4096) }));
  });
}

const phase = (name, argv, result) => ({ name, argv, status: result.ok ? 'passed' : 'failed', exitCode: result.exitCode, reasonCode: result.ok ? null : result.reasonCode });
function assertSucceeded(name, result) { if (!result.ok) throw new Error(`${name} failed: ${result.reasonCode}\n${result.message}`); }
function nugetConfig(globalPackagesFolder) {
  return `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="candidate" value="${CANDIDATE_FEED_PATH}" /></packageSources><config><add key="globalPackagesFolder" value="${globalPackagesFolder}" /></config><packageSourceMapping><packageSource key="candidate"><package pattern="*" /></packageSource></packageSourceMapping></configuration>\n`;
}

async function archiveManifest(archive) {
  const result = await run('tar', ['-xOf', archive, 'package/package.json'], root);
  assertSucceeded(`read ${basename(archive)}`, result);
  const manifest = JSON.parse(result.message);
  if (!NPM_CANDIDATE_NAMES.includes(manifest.name) || typeof manifest.version !== 'string') throw new Error(`archive ${basename(archive)} is not a supported Runic npm candidate`);
  return manifest;
}

const registryScript = `import { createReadStream, readFileSync } from 'node:fs'; import { createServer } from 'node:http'; import { basename } from 'node:path'; import { execFileSync } from 'node:child_process'; import { createHash } from 'node:crypto';
const packages = new Map(process.argv.slice(1).map((archive) => { const manifest = JSON.parse(execFileSync('tar', ['-xOf', archive, 'package/package.json'], { encoding: 'utf8' })); return [manifest.name, { archive, manifest }]; }));
const server = createServer((request, response) => { const path = new URL(request.url ?? '/', 'http://127.0.0.1').pathname; for (const [name, entry] of packages) { const archivePath = '/archives/' + basename(entry.archive); if (decodeURIComponent(path.slice(1)) === name) { const tarball = 'http://127.0.0.1:' + server.address().port + archivePath; const integrity = 'sha512-' + createHash('sha512').update(readFileSync(entry.archive)).digest('base64'); response.writeHead(200, {'content-type':'application/json'}); response.end(JSON.stringify({name, 'dist-tags': {latest: entry.manifest.version}, versions: {[entry.manifest.version]: {...entry.manifest, dist: {tarball, integrity}}}})); return; } if (path === archivePath) { response.writeHead(200, {'content-type':'application/octet-stream'}); createReadStream(entry.archive).pipe(response); return; }} response.writeHead(404); response.end(); });
server.listen(0, '127.0.0.1', () => process.stdout.write('http://127.0.0.1:' + server.address().port + '\\n')); process.on('SIGTERM', () => server.close(() => process.exit(0)));`;

async function startRegistry() {
  if (archives.length !== NPM_CANDIDATE_NAMES.length) throw new Error('RUNIC_CURRENT_SVELTE_TEMPLATE_NPM_ARCHIVES must name the three current Runic npm archives');
  const manifests = await Promise.all(archives.map(archiveManifest));
  if (!same(manifests.map(({ name }) => name).sort(), [...NPM_CANDIDATE_NAMES].sort())) throw new Error('npm candidate archives must contain exactly the supported Runic packages');
  const child = spawn('node', ['--input-type=module', '--eval', registryScript, ...archives], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] });
  const url = await new Promise((resolveUrl, reject) => {
    const timer = setTimeout(() => reject(new Error('local npm registry did not bind')), 5000);
    child.stdout.once('data', (chunk) => { clearTimeout(timer); resolveUrl(chunk.toString('utf8').trim()); });
    child.once('error', reject);
    child.stderr.once('data', (chunk) => reject(new Error(chunk.toString('utf8'))));
  });
  return { child, url, manifests };
}

async function sha256(path) { return createHash('sha256').update(await readFile(path)).digest('hex'); }
async function nugetMetadata(directory) {
  const assets = JSON.parse(await readFile(join(directory, 'CurrentSvelteTemplate', 'obj', 'project.assets.json'), 'utf8'));
  const packageRoot = join(directory, '.nuget', 'packages');
  return Promise.all(NUGET_CANDIDATES.slice(1, -1).map(async (candidate) => {
    const key = `${candidate.identity}/${candidate.version}`.toLowerCase();
    const library = Object.entries(assets.libraries ?? {}).find(([entry]) => entry.toLowerCase() === key)?.[1];
    const packageDirectory = join(packageRoot, candidate.identity.toLowerCase(), candidate.version.toLowerCase());
    const metadata = JSON.parse(await readFile(join(packageDirectory, '.nupkg.metadata'), 'utf8'));
    if (!library || library.type !== 'package' || metadata.version !== 2 || metadata.source !== CANDIDATE_FEED_PATH || metadata.contentHash !== library.sha512) throw new Error(`NuGet provenance failed closed for ${candidate.identity}`);
    return { ...candidate, source: NUGET_FEED, contentHash: metadata.contentHash };
  }));
}
async function npmMetadata(directory, registryUrl, manifests) {
  const lock = JSON.parse(await readFile(join(directory, 'CurrentSvelteTemplate', 'package-lock.json'), 'utf8'));
  return Promise.all(manifests.map(async (manifest) => {
    const entry = lock.packages?.[`Frontend/node_modules/${manifest.name}`] ?? lock.packages?.[`node_modules/${manifest.name}`];
    if (!entry || entry.version !== manifest.version || typeof entry.resolved !== 'string' || !entry.resolved.startsWith(registryUrl) || typeof entry.integrity !== 'string') throw new Error(`npm provenance failed closed for ${manifest.name}`);
    const archive = archives[manifests.indexOf(manifest)];
    return { identity: manifest.name, version: manifest.version, source: NPM_FEED, integrity: entry.integrity, archiveSha256: await sha256(archive) };
  }));
}

export function verifyReceipt(receipt, authority) {
  const errors = [];
  if (!receipt || receipt.schema !== RECEIPT_SCHEMA) errors.push('receipt schema mismatch');
  if (!same(receipt?.feeds, { nuget: NUGET_FEED, npm: NPM_FEED })) errors.push('candidate feed mismatch');
  if (!same(receipt?.isolation, { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet', npmCache: '.npm-cache' })) errors.push('cache isolation mismatch');
  if (!same(receipt?.releaseManifest, authority)) errors.push('release authority identity mismatch');
  if (!receipt?.template || receipt.template.identity !== 'runic-app-svelte' || receipt.template.version !== packageVersion || receipt.template.assetsVersion !== assetsVersion) errors.push('template identity mismatch');
  if (!receipt?.manifest || receipt.manifest.schema !== 'runic.application/1' || receipt.manifest.provenance !== 'template') errors.push('generated manifest mismatch');
  if (!receipt?.bridgeManifest || receipt.bridgeManifest.protocol !== 'runic.artifex.counter' || receipt.bridgeManifest.version !== 1 || typeof receipt.bridgeManifest.fingerprint !== 'string') errors.push('bridge manifest mismatch');
  if (!Array.isArray(receipt?.nugetCandidates) || receipt.nugetCandidates.length !== NUGET_CANDIDATES.length || receipt.nugetCandidates.some((candidate, index) => !same({ identity: candidate?.identity, version: candidate?.version, source: candidate?.source }, { ...NUGET_CANDIDATES[index], source: NUGET_FEED }) || typeof candidate?.contentHash !== 'string' || !candidate.contentHash)) errors.push('NuGet provenance mismatch');
  if (!Array.isArray(receipt?.npmCandidates) || receipt.npmCandidates.length !== NPM_CANDIDATE_NAMES.length || receipt.npmCandidates.some((candidate) => !NPM_CANDIDATE_NAMES.includes(candidate?.identity) || candidate.source !== NPM_FEED || typeof candidate.version !== 'string' || !candidate.version || typeof candidate.integrity !== 'string' || !candidate.integrity || !/^[a-f0-9]{64}$/.test(candidate.archiveSha256 ?? ''))) errors.push('npm provenance mismatch');
  const expected = [
    ['template-install', ['dotnet', 'new', 'install', `Runic.Application.Templates::${packageVersion}`, '--nuget-source', CANDIDATE_FEED_PATH]],
    ['template-create', ['dotnet', 'new', 'runic-app-svelte', '--name', 'CurrentSvelteTemplate', '--output', 'CurrentSvelteTemplate', '--runicApplicationVersion', packageVersion, '--runicAssetsVersion', assetsVersion]],
    ['npm-install', ['npm', 'install', '--ignore-scripts']], ['frontend-build', ['npm', 'run', 'build']],
    ['restore', ['dotnet', 'restore', 'CurrentSvelteTemplate.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']],
    ['build', ['dotnet', 'build', 'CurrentSvelteTemplate.csproj', '--no-restore', '--configuration', 'Release', '--nologo']],
    ['smoke', ['dotnet', 'run', '--project', 'CurrentSvelteTemplate.csproj', '--no-build', '--configuration', 'Release', '--', '--smoke-test']],
    ['tool-install', ['dotnet', 'tool', 'install', 'dotnet-runic', '--tool-path', '../.tools', '--version', packageVersion, '--configfile', '../NuGet.config', '--no-cache']],
    ['inspect-first', ['dotnet-runic', 'inspect', '--project', 'CurrentSvelteTemplate.csproj', '--configuration', 'Release']],
    ['inspect-second', ['dotnet-runic', 'inspect', '--project', 'CurrentSvelteTemplate.csproj', '--configuration', 'Release']],
  ];
  if (!Array.isArray(receipt?.phases) || receipt.phases.length !== expected.length) errors.push('phase evidence malformed');
  else expected.forEach(([name, argv], index) => { const actual = receipt.phases[index]; if (!actual || actual.name !== name || !same(actual.argv, argv) || actual.status !== 'passed' || actual.exitCode !== 0 || actual.reasonCode !== null) errors.push(`${name} evidence malformed`); });
  return { ok: errors.length === 0, errors };
}

export function verifyRepeatedReceipt(receipt, authority) {
  const errors = [];
  if (!receipt || receipt.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt.journeys) || receipt.journeys.length !== 2) errors.push('repeat receipt schema mismatch');
  else { receipt.journeys.forEach((journey, index) => errors.push(...verifyReceipt(journey, authority).errors.map((error) => `journey ${index + 1}: ${error}`))); if (!same(receipt.journeys[0], receipt.journeys[1])) errors.push('Svelte template journeys are not deterministic'); }
  return { ok: errors.length === 0, errors };
}

export async function runCurrentSvelteTemplate(manifestPath) {
  requireCandidateFeed();
  const authority = await releaseManifestFacts(manifestPath);
  const directory = await mkdtemp(join(tmpdir(), 'runic-current-svelte-template-'));
  const environment = { DOTNET_CLI_HOME: join(directory, '.dotnet'), NUGET_PACKAGES: join(directory, '.nuget', 'packages'), NUGET_HTTP_CACHE_PATH: join(directory, '.nuget', 'http-cache'), npm_config_cache: join(directory, '.npm-cache') };
  let registry;
  try {
    registry = await startRegistry();
    await writeFile(join(directory, 'NuGet.config'), nugetConfig(join(directory, '.nuget', 'packages')));
    const phases = [];
    const invoke = async (name, command, args, cwd) => { const result = await run(command, args, cwd, environment); phases.push(phase(name, [command, ...args], result)); assertSucceeded(name, result); return result; };
    await invoke('template-install', 'dotnet', ['new', 'install', `Runic.Application.Templates::${packageVersion}`, '--nuget-source', CANDIDATE_FEED_PATH], directory);
    await invoke('template-create', 'dotnet', ['new', 'runic-app-svelte', '--name', 'CurrentSvelteTemplate', '--output', 'CurrentSvelteTemplate', '--runicApplicationVersion', packageVersion, '--runicAssetsVersion', assetsVersion], directory);
    const projectDirectory = join(directory, 'CurrentSvelteTemplate');
    const frontendDirectory = join(projectDirectory, 'Frontend');
    await writeFile(join(projectDirectory, 'NuGet.config'), nugetConfig(join(directory, '.nuget', 'packages')));
    const frontend = JSON.parse(await readFile(join(frontendDirectory, 'package.json'), 'utf8'));
    const expectedVersions = new Map(registry.manifests.map((manifest) => [manifest.name, manifest.version]));
    if ([...expectedVersions].some(([name, version]) => (frontend.dependencies?.[name] ?? frontend.devDependencies?.[name]) !== version)) throw new Error('template did not retain exact current npm candidate versions');
    await writeFile(join(projectDirectory, '.npmrc'), `@runic-artifex:registry=${registry.url}\n`);
    await invoke('npm-install', 'npm', ['install', '--ignore-scripts'], frontendDirectory);
    await invoke('frontend-build', 'npm', ['run', 'build'], frontendDirectory);
    await invoke('restore', 'dotnet', ['restore', 'CurrentSvelteTemplate.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo'], projectDirectory);
    await invoke('build', 'dotnet', ['build', 'CurrentSvelteTemplate.csproj', '--no-restore', '--configuration', 'Release', '--nologo'], projectDirectory);
    await invoke('smoke', 'dotnet', ['run', '--project', 'CurrentSvelteTemplate.csproj', '--no-build', '--configuration', 'Release', '--', '--smoke-test'], projectDirectory);
    const nugetCandidates = await nugetMetadata(directory);
    const toolDirectory = join(directory, 'tool-install');
    await mkdir(toolDirectory);
    await writeFile(join(projectDirectory, 'NuGet.config'), nugetConfig(join(directory, '.nuget', 'packages')));
    const toolResult = await run('dotnet', ['tool', 'install', 'dotnet-runic', '--tool-path', '../.tools', '--version', packageVersion, '--configfile', '../NuGet.config', '--no-cache'], toolDirectory, environment);
    phases.push(phase('tool-install', ['dotnet', 'tool', 'install', 'dotnet-runic', '--tool-path', '../.tools', '--version', packageVersion, '--configfile', '../NuGet.config', '--no-cache'], toolResult)); assertSucceeded('tool-install', toolResult);
    const tool = join(directory, '.tools', 'dotnet-runic');
    const inspectArgs = ['inspect', '--project', 'CurrentSvelteTemplate.csproj', '--configuration', 'Release'];
    const first = await run(tool, inspectArgs, projectDirectory, environment); phases.push(phase('inspect-first', ['dotnet-runic', ...inspectArgs], first)); assertSucceeded('inspect-first', first);
    const second = await run(tool, inspectArgs, projectDirectory, environment); phases.push(phase('inspect-second', ['dotnet-runic', ...inspectArgs], second)); assertSucceeded('inspect-second', second);
    const manifest = JSON.parse(first.message.trim()); if (!same(manifest, JSON.parse(second.message.trim()))) throw new Error('dotnet runic inspection is not deterministic');
    const bridge = JSON.parse(await readFile(join(projectDirectory, 'Contract', 'bridge.manifest.json'), 'utf8'));
    const toolCandidate = NUGET_CANDIDATES.at(-1);
    const templateCandidate = NUGET_CANDIDATES[0];
    const receipt = { schema: RECEIPT_SCHEMA, feeds: { nuget: NUGET_FEED, npm: NPM_FEED }, isolation: { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet', npmCache: '.npm-cache' }, releaseManifest: await releaseManifestAfter(manifestPath, authority), template: { identity: 'runic-app-svelte', version: packageVersion, assetsVersion }, manifest, bridgeManifest: { protocol: bridge.protocol?.identity, version: bridge.protocol?.version, fingerprint: bridge.contractFingerprint }, nugetCandidates: [{ ...templateCandidate, source: NUGET_FEED, contentHash: await sha256(join(CANDIDATE_FEED_PATH, `${templateCandidate.identity}.${templateCandidate.version}.nupkg`)) }, ...nugetCandidates, { ...toolCandidate, source: NUGET_FEED, contentHash: await sha256(join(CANDIDATE_FEED_PATH, `${toolCandidate.identity}.${toolCandidate.version}.nupkg`)) }], npmCandidates: await npmMetadata(directory, registry.url, registry.manifests), phases };
    const report = verifyReceipt(receipt, receipt.releaseManifest); if (!report.ok) throw new Error(report.errors.join('\n'));
    return receipt;
  } finally { if (registry) registry.child.kill('SIGTERM'); await rm(directory, { recursive: true, force: true }); }
}

export async function runCurrentSvelteTemplateTwice(manifestPath) { const journeys = [await runCurrentSvelteTemplate(manifestPath), await runCurrentSvelteTemplate(manifestPath)]; const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys }; const report = verifyRepeatedReceipt(receipt, journeys[0].releaseManifest); if (!report.ok) throw new Error(report.errors.join('\n')); return receipt; }
async function main() { const [command, manifestPath, receiptPath] = process.argv.slice(2); if (command === 'run-twice' && manifestPath && !receiptPath) return process.stdout.write(`${JSON.stringify(await runCurrentSvelteTemplateTwice(manifestPath), null, 2)}\n`); if (command === 'verify-twice' && manifestPath && receiptPath) { const authority = await releaseManifestFacts(manifestPath); const report = verifyRepeatedReceipt(JSON.parse(await readFile(receiptPath, 'utf8')), authority); if (!report.ok) throw new Error(report.errors.join('\n')); return; } throw new Error('Usage: node eng/current-svelte-template/verify.mjs run-twice <runic.release.json> | verify-twice <runic.release.json> <receipt.json>'); }
if (import.meta.main) main().catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
