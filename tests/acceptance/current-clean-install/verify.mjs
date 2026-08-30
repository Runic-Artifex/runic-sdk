#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import process from 'node:process';

export const RECEIPT_SCHEMA = 'runic.current-clean-install/2';
export const REPEAT_RECEIPT_SCHEMA = 'runic.current-clean-install-repeat/2';
export const TEMPLATE = 'runic-app-svelte';
export const REQUIRED_NUGET_IDENTITIES = ['Runic.Application.Templates', 'dotnet-runic', 'Runic.Application', 'Runic.Application.Bridge', 'Runic.Application.Desktop', 'Runic.Assets', 'Runic.Assets.Desktop', 'Runic.Desktop'];
export const REQUIRED_NPM_IDENTITIES = ['@runic-artifex/application-bridge', '@runic-artifex/desktop', '@runic-artifex/svelte', '@runic-artifex/vite-plugin-runic'];

const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const sha256 = (value) => createHash('sha256').update(value).digest('hex');
const phase = (name, argv, result) => ({ name, argv, status: result.ok ? 'passed' : 'failed', exitCode: result.exitCode, reasonCode: result.ok ? null : result.reasonCode });
const fail = (message) => { throw new Error(`current clean-install: ${message}`); };

function run(command, args, cwd, env = {}) {
  return new Promise((done) => {
    const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
    const output = [];
    child.stdout.on('data', (chunk) => output.push(chunk));
    child.stderr.on('data', (chunk) => output.push(chunk));
    child.on('error', (error) => done({ ok: false, exitCode: null, reasonCode: error.code === 'ENOENT' ? 'command-not-found' : 'command-spawn-failed', message: error.message }));
    child.on('close', (exitCode) => done({ ok: exitCode === 0, exitCode, reasonCode: exitCode === 0 ? null : 'command-exit-nonzero', message: Buffer.concat(output).toString('utf8').slice(-4096) }));
  });
}

function exactPackage(authority, ecosystem, identity) {
  const matches = authority.packages.filter((item) => item?.ecosystem === ecosystem && item?.identity === identity);
  if (matches.length !== 1 || typeof matches[0].version !== 'string' || !/^\d+\.\d+\.\d+-preview\.\d+$/.test(matches[0].version)) fail(`compatibility set must contain exactly one ordered preview package '${ecosystem}:${identity}'`);
  return { identity, version: matches[0].version };
}

export function compatibilityFacts(authority, bytes) {
  if (!authority || authority.schemaVersion !== 1 || authority.publication !== 'forbidden' || typeof authority.id !== 'string' || !authority.id || !Array.isArray(authority.packages)) fail('compatibility set is not a publication-forbidden v1 package authority');
  if (!authority.toolchain || !/^\d+\.\d+\.\d+$/.test(authority.toolchain.dotnetSdk ?? '') || !/^\d+\.\d+\.\d+$/.test(authority.toolchain.node ?? '') || !/^\d+\.\d+\.\d+$/.test(authority.toolchain.npm ?? '')) fail('compatibility set has no exact toolchain');
  const nuget = Object.fromEntries(REQUIRED_NUGET_IDENTITIES.map((identity) => [identity, exactPackage(authority, 'nuget', identity)]));
  const npm = Object.fromEntries(REQUIRED_NPM_IDENTITIES.map((identity) => [identity, exactPackage(authority, 'npm', identity)]));
  const versions = new Set([...Object.values(nuget), ...Object.values(npm)].map((item) => item.version));
  if (versions.size !== 1 || !versions.has(authority.releaseTrainVersion)) fail('selected package versions must exactly equal the release-train version');
  return { id: authority.id, releaseTrainVersion: authority.releaseTrainVersion, sha256: sha256(bytes), toolchain: authority.toolchain, nuget, npm };
}

async function readCompatibilitySet(path) {
  const bytes = await readFile(path);
  try { return compatibilityFacts(JSON.parse(bytes), bytes); } catch (error) { if (error.message.startsWith('current clean-install:')) throw error; fail('compatibility set must be JSON'); }
}

function registry(value, name) {
  let parsed;
  try { parsed = new URL(value); } catch { fail(`${name} must be an absolute HTTP(S) registry URL`); }
  if (!['http:', 'https:'].includes(parsed.protocol) || /npm\.pkg\.github\.com/i.test(parsed.hostname)) fail(`${name} must be an explicit non-GitHub HTTP(S) registry URL`);
  return parsed.href.replace(/\/$/, '');
}

async function feedPackages(feed, facts) {
  let entries;
  try { entries = new Set(await readdir(feed)); } catch { fail(`NuGet feed '${feed}' is not readable`); }
  const result = {};
  for (const item of Object.values(facts.nuget)) {
    const expected = `${item.identity}.${item.version}.nupkg`.toLowerCase();
    const actual = [...entries].find((entry) => entry.toLowerCase() === expected);
    if (!actual) fail(`explicit NuGet feed is missing '${item.identity}.${item.version}.nupkg'`);
    result[item.identity] = join(feed, actual);
  }
  return result;
}

function nugetConfig(feed, publicSource, globalPackagesFolder) {
  const localPatterns = REQUIRED_NUGET_IDENTITIES.map((identity) => `      <package pattern="${identity}" />`).join('\n');
  return `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="runic-local" value="${feed}" /><add key="public" value="${publicSource}" protocolVersion="3" /></packageSources>
  <packageSourceMapping><packageSource key="runic-local">
${localPatterns}
    </packageSource><packageSource key="public"><package pattern="*" /></packageSource></packageSourceMapping>
  <config><add key="globalPackagesFolder" value="${globalPackagesFolder}" /></config>
</configuration>
`;
}

function packageVersions(project, packageJson, facts) {
  for (const item of ['Runic.Application', 'Runic.Application.Bridge', 'Runic.Application.Desktop', 'Runic.Assets', 'Runic.Assets.Desktop', 'Runic.Desktop']) {
    const escaped = item.replaceAll('.', '\\.');
    const match = new RegExp(`<PackageReference\\s+Include="${escaped}"\\s+Version="([^"]+)"`, 'i').exec(project);
    if (!match || match[1] !== facts.nuget[item].version) fail(`generated project does not pin '${item}' to the compatibility set`);
  }
  const declared = { ...(packageJson.dependencies ?? {}), ...(packageJson.devDependencies ?? {}) };
  for (const item of REQUIRED_NPM_IDENTITIES) if (declared[item] !== facts.npm[item].version) fail(`generated frontend does not pin '${item}' to the compatibility set`);
}

async function npmLockEvidence(frontend, facts, npmRegistry) {
  const lock = JSON.parse(await readFile(join(frontend, 'package-lock.json'), 'utf8'));
  const packages = lock.packages ?? {};
  const evidence = {};
  for (const item of REQUIRED_NPM_IDENTITIES) {
    const entry = packages[`node_modules/${item}`];
    if (!entry || entry.version !== facts.npm[item].version || typeof entry.resolved !== 'string' || !entry.resolved.startsWith(`${npmRegistry}/`)) fail(`frontend lockfile does not prove '${item}' came from the explicit Runic registry`);
    evidence[item] = { version: entry.version, resolved: entry.resolved };
  }
  return evidence;
}

async function nugetEvidence(projectDirectory, facts, feed) {
  const assets = JSON.parse(await readFile(join(projectDirectory, 'obj', 'project.assets.json'), 'utf8'));
  const evidence = {};
  for (const item of ['Runic.Application', 'Runic.Application.Bridge', 'Runic.Application.Desktop', 'Runic.Assets', 'Runic.Assets.Desktop', 'Runic.Desktop']) {
    const selectedPackage = facts.nuget[item];
    const key = Object.keys(assets.libraries ?? {}).find((value) => value.toLowerCase() === `${selectedPackage.identity}/${selectedPackage.version}`.toLowerCase());
    const library = key && assets.libraries[key];
    const metadata = JSON.parse(await readFile(join(projectDirectory, '.nuget', 'packages', selectedPackage.identity.toLowerCase(), selectedPackage.version.toLowerCase(), '.nupkg.metadata'), 'utf8'));
    if (!library || library.type !== 'package' || !library.sha512 || metadata.source !== feed || metadata.contentHash !== library.sha512) fail(`restore provenance for '${item}' is not the exact supplied feed`);
    evidence[item] = { version: selectedPackage.version, contentHash: metadata.contentHash };
  }
  return evidence;
}

const expectedPhases = () => ['template-install', 'tool-install', 'create', 'npm-install', 'frontend-typecheck', 'frontend-build', 'restore', 'build', 'doctor', 'inspect', 'develop', 'package', 'run'];

export function verifyReceipt(receipt, facts) {
  const errors = [];
  if (receipt?.schema !== RECEIPT_SCHEMA) errors.push('receipt schema mismatch');
  if (!same(receipt?.compatibilitySet, facts)) errors.push('compatibility authority mismatch');
  if (!same(receipt?.feeds, { nuget: 'explicit-local-directory', npm: 'explicit-runic-registry', githubPackages: 'prohibited' })) errors.push('feed policy mismatch');
  if (!same(receipt?.isolation, { dotnetCliHome: '.dotnet', nugetPackages: '.nuget/packages', nugetHttpCache: '.nuget/http-cache', npmCache: '.npm-cache' })) errors.push('cache isolation mismatch');
  if (!same(receipt?.template, { shortName: TEMPLATE, framework: 'svelte' })) errors.push('template selection mismatch');
  if (!Array.isArray(receipt?.phases) || !same(receipt.phases.map((item) => item.name), expectedPhases()) || receipt.phases.some((item) => item.status !== 'passed' || item.exitCode !== 0 || item.reasonCode !== null)) errors.push('golden-path phase evidence mismatch');
  for (const item of ['Runic.Application', 'Runic.Application.Bridge', 'Runic.Application.Desktop', 'Runic.Assets', 'Runic.Assets.Desktop', 'Runic.Desktop']) if (receipt?.nugetPackages?.[item]?.version !== facts.nuget[item].version || !receipt.nugetPackages[item].contentHash) errors.push(`NuGet evidence malformed for '${item}'`);
  for (const item of REQUIRED_NPM_IDENTITIES) if (receipt?.npmPackages?.[item]?.version !== facts.npm[item].version || typeof receipt.npmPackages[item].resolved !== 'string') errors.push(`npm evidence malformed for '${item}'`);
  return { ok: errors.length === 0, errors };
}

export function verifyRepeatedReceipt(receipt, facts) {
  const errors = [];
  if (receipt?.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push('repeat receipt schema mismatch');
  else {
    receipt.journeys.forEach((journey, index) => errors.push(...verifyReceipt(journey, facts).errors.map((error) => `journey ${index + 1}: ${error}`)));
    if (!same(receipt.journeys[0], receipt.journeys[1])) errors.push('clean journeys are not repeatable');
  }
  return { ok: errors.length === 0, errors };
}

export async function runCurrentCleanInstall(options) {
  const compatibilitySet = resolve(options.compatibilitySet);
  const facts = await readCompatibilitySet(compatibilitySet);
  const nugetFeed = resolve(options.nugetFeed);
  const packages = await feedPackages(nugetFeed, facts);
  const npmRegistry = registry(options.npmRegistry, 'Runic npm registry');
  const npmPublicRegistry = registry(options.npmPublicRegistry, 'public npm registry');
  const nugetPublicSource = registry(options.nugetPublicSource, 'public NuGet source');
  const directory = await mkdtemp(join(tmpdir(), 'runic-current-clean-install-'));
  const projectDirectory = join(directory, 'GoldenPath');
  const frontend = join(projectDirectory, 'Frontend');
  const environment = { DOTNET_CLI_HOME: join(directory, '.dotnet'), NUGET_PACKAGES: join(directory, '.nuget', 'packages'), NUGET_HTTP_CACHE_PATH: join(directory, '.nuget', 'http-cache'), npm_config_cache: join(directory, '.npm-cache'), npm_config_update_notifier: 'false' };
  const phases = [];
  const execute = async (name, command, args, cwd = directory) => {
    const result = await run(command, args, cwd, environment);
    phases.push(phase(name, [command, ...args], result));
    if (!result.ok) fail(`${name} failed: ${result.reasonCode}${result.message ? `\n${result.message}` : ''}`);
  };
  try {
    await writeFile(join(directory, 'NuGet.config'), nugetConfig(nugetFeed, nugetPublicSource, environment.NUGET_PACKAGES));
    await execute('template-install', 'dotnet', ['new', 'install', packages['Runic.Application.Templates'], '--force']);
    await execute('tool-install', 'dotnet', ['tool', 'install', 'dotnet-runic', '--tool-path', join(directory, '.tools'), '--version', facts.nuget['dotnet-runic'].version, '--add-source', nugetFeed, '--configfile', 'NuGet.config', '--ignore-failed-sources']);
    await execute('create', 'dotnet', ['new', TEMPLATE, '--name', 'GoldenPath', '--output', projectDirectory]);
    const project = await readFile(join(projectDirectory, 'GoldenPath.csproj'), 'utf8');
    const packageJson = JSON.parse(await readFile(join(frontend, 'package.json'), 'utf8'));
    packageVersions(project, packageJson, facts);
    await writeFile(join(frontend, '.npmrc'), `@runic-artifex:registry=${npmRegistry}\nregistry=${npmPublicRegistry}\n`);
    await execute('npm-install', 'npm', ['install', '--ignore-scripts', '--no-audit', '--no-fund', '--cache', '.npm-cache'], frontend);
    const npmPackages = await npmLockEvidence(frontend, facts, npmRegistry);
    await execute('frontend-typecheck', 'npm', ['run', 'typecheck'], frontend);
    await execute('frontend-build', 'npm', ['run', 'build'], frontend);
    await execute('restore', 'dotnet', ['restore', 'GoldenPath.csproj', '--configfile', join(directory, 'NuGet.config'), '--no-cache', '--force-evaluate', '--nologo'], projectDirectory);
    const nugetPackages = await nugetEvidence(projectDirectory, facts, nugetFeed);
    await execute('build', 'dotnet', ['build', 'GoldenPath.csproj', '--configuration', 'Release', '--no-restore', '--nologo'], projectDirectory);
    const tool = join(directory, '.tools', process.platform === 'win32' ? 'dotnet-runic.exe' : 'dotnet-runic');
    await execute('doctor', tool, ['doctor', '--project', 'GoldenPath.csproj', '--configuration', 'Release'], projectDirectory);
    await execute('inspect', tool, ['inspect', '--project', 'GoldenPath.csproj', '--configuration', 'Release'], projectDirectory);
    await execute('develop', tool, ['dev', '--project', 'GoldenPath.csproj', '--configuration', 'Release', '--dry-run'], projectDirectory);
    await execute('package', 'dotnet', ['publish', 'GoldenPath.csproj', '--configuration', 'Release', '--no-build', '--output', join(directory, 'package'), '--nologo'], projectDirectory);
    await execute('run', 'dotnet', ['run', '--project', 'GoldenPath.csproj', '--configuration', 'Release', '--no-build', '--', '--smoke-test'], projectDirectory);
    const receipt = { schema: RECEIPT_SCHEMA, compatibilitySet: facts, feeds: { nuget: 'explicit-local-directory', npm: 'explicit-runic-registry', githubPackages: 'prohibited' }, isolation: { dotnetCliHome: '.dotnet', nugetPackages: '.nuget/packages', nugetHttpCache: '.nuget/http-cache', npmCache: '.npm-cache' }, template: { shortName: TEMPLATE, framework: 'svelte' }, phases, nugetPackages, npmPackages };
    const report = verifyReceipt(receipt, facts);
    if (!report.ok) fail(report.errors.join('; '));
    return receipt;
  } finally { await rm(directory, { recursive: true, force: true }); }
}

export async function runCurrentCleanInstallTwice(options) {
  const facts = await readCompatibilitySet(resolve(options.compatibilitySet));
  const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys: [await runCurrentCleanInstall(options), await runCurrentCleanInstall(options)] };
  const report = verifyRepeatedReceipt(receipt, facts);
  if (!report.ok) fail(report.errors.join('; '));
  return receipt;
}

function optionsFrom(argv) {
  const result = {};
  for (let index = 0; index < argv.length; index += 2) { const key = argv[index]; const value = argv[index + 1]; if (!key?.startsWith('--') || !value || result[key.slice(2)]) fail('usage'); result[key.slice(2)] = value; }
  const required = ['compatibility-set', 'nuget-feed', 'npm-registry', 'npm-public-registry', 'nuget-public-source'];
  if (!same(Object.keys(result).filter((key) => key !== 'receipt').sort(), required.sort())) fail('all explicit authority and feed inputs are required');
  return { compatibilitySet: result['compatibility-set'], nugetFeed: result['nuget-feed'], npmRegistry: result['npm-registry'], npmPublicRegistry: result['npm-public-registry'], nugetPublicSource: result['nuget-public-source'], receipt: result.receipt };
}

async function main(argv) {
  const [command, ...rest] = argv; const options = optionsFrom(rest);
  if (command === 'run' && !options.receipt) return process.stdout.write(`${JSON.stringify(await runCurrentCleanInstall(options), null, 2)}\n`);
  if (command === 'run-twice' && !options.receipt) return process.stdout.write(`${JSON.stringify(await runCurrentCleanInstallTwice(options), null, 2)}\n`);
  const facts = await readCompatibilitySet(resolve(options.compatibilitySet));
  if (command === 'verify' && options.receipt) { const report = verifyReceipt(JSON.parse(await readFile(options.receipt, 'utf8')), facts); if (!report.ok) fail(report.errors.join('; ')); return; }
  if (command === 'verify-twice' && options.receipt) { const report = verifyRepeatedReceipt(JSON.parse(await readFile(options.receipt, 'utf8')), facts); if (!report.ok) fail(report.errors.join('; ')); return; }
  fail('usage');
}

if (import.meta.main) main(process.argv.slice(2)).catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
