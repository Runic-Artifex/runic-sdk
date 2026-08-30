#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmod, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import process from 'node:process';
import { releaseManifestAfter, releaseManifestFacts } from '../v0.2-baselines/metrics.mjs';

export const RECEIPT_SCHEMA = 'runic.current-csharp-composition/1';
export const REPEAT_RECEIPT_SCHEMA = 'runic.current-csharp-composition-repeat/1';
const configuredCandidateFeed = process.env.RUNIC_CURRENT_CSHARP_CANDIDATE_FEED;
const candidateFeed = configuredCandidateFeed && resolve(configuredCandidateFeed);
export const CANDIDATES = [
  { identity: 'Runic.Application', version: '0.2.0-preview.1e8fff0' },
  { identity: 'Runic.Application.Testing', version: '0.2.0-preview.1e8fff0' },
  { identity: 'Runic.Assets', version: '0.1.0-preview.8d22423' },
];
export const TOOL_CANDIDATE = { identity: 'dotnet-runic', version: process.env.RUNIC_CURRENT_CSHARP_TOOL_VERSION ?? '0.2.0-preview.1e8fff0' };
export const FEED = 'w10-003-local-candidate-feed';
export const APPLICATION_ARGUMENTS = ['--safe-mode', 'profile a', '-1'];
export function requireCandidateFeed(path = candidateFeed) {
  if (!path) throw new Error('RUNIC_CURRENT_CSHARP_CANDIDATE_FEED must name an explicit isolated NuGet candidate feed.');
  return path;
}
export const MANIFEST = {
  schema: 'runic.application/1',
  entryPoint: 'runic.current-csharp-composition',
  version: '1.0.0',
  provenance: 'local-candidate',
  capabilities: ['desktop', 'headless'],
  artifacts: [
    { kind: 'assets', identity: 'runic.assets/1:fixture', fingerprint: 'assets-fixture' },
    { kind: 'bridge-contract', identity: 'runic.application.fixture/1', fingerprint: 'bridge-fixture' },
  ],
};

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

function nugetConfig(globalPackagesFolder) {
  return `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="candidate" value="${candidateFeed}" /></packageSources>
  <config><add key="globalPackagesFolder" value="${globalPackagesFolder}" /></config>
  <packageSourceMapping>
    <packageSource key="candidate"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
`;
}

function project(name, candidates = CANDIDATES) {
  return `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <RunicAssetsDist>$(MSBuildProjectDirectory)/Frontend/dist</RunicAssetsDist>
    <RunicToolkitFrontendContractSource>$(MSBuildProjectDirectory)/Contracts/contract.json</RunicToolkitFrontendContractSource>
    <RunicToolkitFrontendContractCSharpOutput>$(MSBuildProjectDirectory)/Contracts/generated/Contract.g.cs</RunicToolkitFrontendContractCSharpOutput>
    <RunicToolkitFrontendContractTypeScriptOutput>$(MSBuildProjectDirectory)/Contracts/generated/contract.g.ts</RunicToolkitFrontendContractTypeScriptOutput>
    <RunicToolkitFrontendContractTool>$(MSBuildProjectDirectory)/Contracts/generate-contract.mjs</RunicToolkitFrontendContractTool>
  </PropertyGroup>
  <ItemGroup>${candidates.map((candidate) => `<PackageReference Include="${candidate.identity}" Version="${candidate.version}" />`).join('')}</ItemGroup>
  ${name === 'Negative' ? '' : '<ItemGroup><Compile Remove="negative/**/*.cs" /></ItemGroup>'}
  ${name === 'Negative' ? '<ItemGroup><Compile Remove="Program.cs" /></ItemGroup>' : ''}
</Project>
`;
}

const validProgram = `using Runic.Application;
using Runic.Application.Testing;

[assembly: RunicApplicationManifest("runic.current-csharp-composition", Version = "1.0.0", Provenance = "local-candidate")]
[assembly: RunicApplicationCapability("headless")]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationArtifact("bridge-contract", "runic.application.fixture/1", "bridge-fixture")]
[assembly: RunicApplicationArtifact("assets", "runic.assets/1:fixture", "assets-fixture")]

var host = new DeterministicApplicationTestHost();
await using ApplicationHost application = RunicApplication.CreateBuilder([]).UseHost(host).Build();
await application.RunAsync();
if (host.Lifecycle.Length != 3 || host.Lifecycle[0] != "start" || host.Lifecycle[1] != "wait" || host.Lifecycle[2] != "stop") return 1;
Console.WriteLine(application.Manifest.ToJson());
return 0;
`;

const negativePrograms = {
  missing: 'using Runic.Application;\nConsole.WriteLine("missing");\n',
  duplicate: 'using Runic.Application;\n[assembly: RunicApplicationManifest("first")]\n[assembly: RunicApplicationManifest("second")]\nConsole.WriteLine("duplicate");\n',
  invalid: 'using Runic.Application;\n[assembly: RunicApplicationManifest(" ", Version = "1.0.0", Provenance = "local-candidate")]\nConsole.WriteLine("invalid");\n',
};

const frontendPackage = JSON.stringify({
  name: 'runic-current-csharp-composition',
  private: true,
  packageManager: 'npm@11.16.0',
  scripts: {
    build: 'node -e "process.stdout.write(\'build\\n\')"',
    dev: 'node -e "process.stdout.write(\'dev\\n\')"',
  },
}, null, 2) + '\n';

const frontendLock = JSON.stringify({
  name: 'runic-current-csharp-composition',
  lockfileVersion: 3,
  requires: true,
  packages: { '': { name: 'runic-current-csharp-composition' } },
}, null, 2) + '\n';

const contractSource = JSON.stringify({ schema: 'runic.fixture.contract/1', value: 'current' }) + '\n';
const contractGenerator = `import { readFile, writeFile } from 'node:fs/promises';
const [sourceFlag, sourcePath, csharpFlag, csharpPath, typescriptFlag, typescriptPath, verifyFlag] = process.argv.slice(2);
if (sourceFlag !== '--source' || csharpFlag !== '--csharp' || typescriptFlag !== '--typescript' || (verifyFlag && verifyFlag !== '--verify')) process.exit(2);
const source = await readFile(sourcePath, 'utf8');
const csharp = \`// generated contract: \${source.trim()}\\n\`;
const typescript = \`// generated contract: \${source.trim()}\\n\`;
if (verifyFlag) {
  const [actualCsharp, actualTypescript] = await Promise.all([readFile(csharpPath, 'utf8'), readFile(typescriptPath, 'utf8')]);
  if (actualCsharp !== csharp || actualTypescript !== typescript) process.exit(1);
} else await Promise.all([writeFile(csharpPath, csharp), writeFile(typescriptPath, typescript)]);
`;

async function writeFixtureFiles(directory) {
  await Promise.all([
    mkdir(join(directory, 'Frontend', 'dist'), { recursive: true }),
    mkdir(join(directory, 'Contracts', 'generated'), { recursive: true }),
    mkdir(join(directory, 'native'), { recursive: true }),
    mkdir(join(directory, 'bin'), { recursive: true }),
  ]);
  await Promise.all([
    writeFile(join(directory, 'Frontend', 'package.json'), frontendPackage),
    writeFile(join(directory, 'Frontend', 'package-lock.json'), frontendLock),
    writeFile(join(directory, 'Frontend', 'dist', 'index.html'), '<!doctype html><title>Runic fixture</title>\n'),
    writeFile(join(directory, 'Contracts', 'contract.json'), contractSource),
    writeFile(join(directory, 'Contracts', 'generate-contract.mjs'), contractGenerator),
    writeFile(join(directory, 'Contracts', 'generated', 'Contract.g.cs'), `// generated contract: ${contractSource.trim()}\n`),
    writeFile(join(directory, 'Contracts', 'generated', 'contract.g.ts'), `// generated contract: ${contractSource.trim()}\n`),
    writeFile(join(directory, 'native', 'libwebui-fixture.so'), 'fixture native library\n'),
    writeFile(join(directory, 'bin', 'runic-browser'), '#!/usr/bin/env sh\nprintf "Runic Fixture Browser 1\\n"\n'),
  ]);
  await chmod(join(directory, 'bin', 'runic-browser'), 0o755);
}

async function createConsumer(directory) {
  await mkdir(join(directory, 'negative'));
  await Promise.all([
    writeFile(join(directory, 'NuGet.config'), nugetConfig(join(directory, '.nuget', 'packages'))),
    writeFile(join(directory, 'CurrentComposition.csproj'), project('CurrentComposition')),
    writeFile(join(directory, 'Program.cs'), validProgram),
    writeFile(join(directory, 'negative', 'Negative.csproj'), project('Negative', [CANDIDATES[0]])),
  ]);
  await writeFixtureFiles(directory);
}

async function candidateMetadata(directory) {
  const assets = JSON.parse(await readFile(join(directory, 'obj', 'project.assets.json'), 'utf8'));
  const packageRoot = join(directory, '.nuget', 'packages');
  return Promise.all(CANDIDATES.map(async (candidate) => {
    const library = Object.entries(assets.libraries ?? {}).find(([key]) => key.toLowerCase() === `${candidate.identity}/${candidate.version}`.toLowerCase())?.[1];
    const packageDirectory = Object.keys(assets.packageFolders ?? {})
      .map((folder) => join(folder, candidate.identity.toLowerCase(), candidate.version.toLowerCase()))
      .find((folder) => folder.startsWith(packageRoot));
    if (!library || library.type !== 'package' || typeof library.sha512 !== 'string' || !packageDirectory) throw new Error(`candidate package metadata is malformed for ${candidate.identity}`);
    const metadata = JSON.parse(await readFile(join(packageDirectory, '.nupkg.metadata'), 'utf8'));
    if (metadata.version !== 2 || metadata.source !== candidateFeed || metadata.contentHash !== library.sha512) throw new Error(`candidate package source metadata is malformed for ${candidate.identity}`);
    return { ...candidate, source: FEED, contentHash: metadata.contentHash };
  }));
}

async function projectReferenceEvidence(directory) {
  const source = await readFile(join(directory, 'CurrentComposition.csproj'), 'utf8');
  const references = [...source.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)].map((match) => match[1]);
  if (references.length) throw new Error('current C# composition fixture must not use product-source project references');
  return references;
}

function manifestFromOutput(message) {
  const source = message.trim();
  let manifest;
  try { manifest = JSON.parse(source); } catch { throw new Error('generated manifest output is malformed'); }
  if (!same(manifest, MANIFEST)) throw new Error('generated manifest facts differ from the fixture contract');
  return manifest;
}

function phase(name, argv, result) {
  return { name, argv, status: result.ok ? 'passed' : 'failed', exitCode: result.exitCode, reasonCode: result.ok ? null : result.reasonCode };
}

async function candidateDigest(candidate) {
  const archive = await readFile(join(candidateFeed, `${candidate.identity}.${candidate.version}.nupkg`));
  return createHash('sha256').update(archive).digest('hex');
}

function assertSucceeded(name, result) {
  if (!result.ok) throw new Error(`${name} failed: ${result.reasonCode}\n${result.message}`);
}

function assertFailed(name, result, expected) {
  if (result.ok || !result.message.includes(expected)) throw new Error(`${name} did not fail closed with ${expected}`);
}

const toolInstallReceiptArguments = [
  'dotnet', 'tool', 'install', TOOL_CANDIDATE.identity, '--tool-path', '../.tools', '--version', TOOL_CANDIDATE.version,
  '--configfile', '../NuGet.config', '--no-cache',
];

function toolReceipt(candidate, contentHash) {
  return { ...candidate, source: FEED, contentHash };
}

export function verifyReceipt(receipt, authority) {
  const errors = [];
  if (!receipt || receipt.schema !== RECEIPT_SCHEMA) errors.push('receipt schema mismatch');
  if (receipt?.feed !== FEED) errors.push('candidate feed mismatch');
  if (!same(receipt?.isolation, { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet' })) errors.push('cache isolation mismatch');
  if (!same(receipt?.releaseManifest, authority)) errors.push('release authority identity mismatch');
  if (!same(receipt?.projectReferences, [])) errors.push('product-source project references are prohibited');
  if (!same(receipt?.manifest, MANIFEST)) errors.push('generated manifest mismatch');
  if (!Array.isArray(receipt?.candidates) || receipt.candidates.length !== CANDIDATES.length || receipt.candidates.some((candidate, index) => !same({ identity: candidate?.identity, version: candidate?.version, source: candidate?.source }, { ...CANDIDATES[index], source: FEED }) || typeof candidate?.contentHash !== 'string' || candidate.contentHash.length === 0)) errors.push('candidate metadata mismatch');
  if (!same({ identity: receipt?.tool?.identity, version: receipt?.tool?.version, source: receipt?.tool?.source }, { ...TOOL_CANDIDATE, source: FEED }) || typeof receipt?.tool?.contentHash !== 'string' || receipt.tool.contentHash.length !== 64) errors.push('tool candidate metadata mismatch');
  const expected = [
    ['restore', ['dotnet', 'restore', 'CurrentComposition.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']],
    ['build', ['dotnet', 'build', 'CurrentComposition.csproj', '--no-restore', '--configuration', 'Release', '--nologo']],
    ['run', ['dotnet', 'run', '--project', 'CurrentComposition.csproj', '--no-build', '--configuration', 'Release']],
    ['negative-restore', ['dotnet', 'restore', 'negative/Negative.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']],
    ['tool-install', toolInstallReceiptArguments],
    ['doctor-healthy', ['dotnet-runic', 'doctor', '--project', 'CurrentComposition.csproj']],
    ['doctor-absent-contract-output', ['dotnet-runic', 'doctor', '--project', 'CurrentComposition.csproj']],
    ['doctor-stale-contract-output', ['dotnet-runic', 'doctor', '--project', 'CurrentComposition.csproj']],
    ['dev-dry-run', ['dotnet-runic', 'dev', '--project', 'CurrentComposition.csproj', '--dry-run', '--', ...APPLICATION_ARGUMENTS]],
    ['inspect-first', ['dotnet-runic', 'inspect', '--project', 'CurrentComposition.csproj', '--configuration', 'Release']],
    ['inspect-second', ['dotnet-runic', 'inspect', '--project', 'CurrentComposition.csproj', '--configuration', 'Release']],
  ];
  if (!Array.isArray(receipt?.phases) || receipt.phases.length !== expected.length) errors.push('phase evidence malformed');
  else expected.forEach(([name, argv], index) => {
    const actual = receipt.phases[index];
    const expectedStatus = name.startsWith('doctor-absent') || name.startsWith('doctor-stale') ? 'failed' : 'passed';
    if (!actual || actual.name !== name || !same(actual.argv, argv) || actual.status !== expectedStatus || (expectedStatus === 'passed' && (actual.exitCode !== 0 || actual.reasonCode !== null)) || (expectedStatus === 'failed' && actual.exitCode === 0)) errors.push(`${name} evidence malformed`);
  });
  if (!same(receipt?.development, {
    configuredCommands: [['npm', 'run', 'build'], ['npm', 'run', 'dev']],
    applicationArguments: APPLICATION_ARGUMENTS,
  })) errors.push('development plan mismatch');
  if (!same(receipt?.inspectManifest, MANIFEST)) errors.push('inspect manifest mismatch');
  const expectedNegatives = [['missing', 'RAPP0000'], ['duplicate', 'RAPP0002'], ['invalid', 'RAPP0002']];
  if (!Array.isArray(receipt?.negativeDeclarations) || receipt.negativeDeclarations.length !== expectedNegatives.length) errors.push('negative declaration evidence malformed');
  else expectedNegatives.forEach(([kind, diagnostic], index) => {
    const actual = receipt.negativeDeclarations[index];
    if (!actual || actual.kind !== kind || actual.diagnostic !== diagnostic || actual.exitCode === 0) errors.push(`${kind} declaration did not fail closed`);
  });
  return { ok: errors.length === 0, errors };
}

export function verifyRepeatedReceipt(receipt, authority) {
  const errors = [];
  if (!receipt || receipt.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt.journeys) || receipt.journeys.length !== 2) errors.push('repeat receipt schema mismatch');
  else {
    receipt.journeys.forEach((journey, index) => errors.push(...verifyReceipt(journey, authority).errors.map((error) => `journey ${index + 1}: ${error}`)));
    if (!same(receipt.journeys[0], receipt.journeys[1])) errors.push('composition journeys are not deterministic');
  }
  return { ok: errors.length === 0, errors };
}

export async function runCurrentCSharpComposition(manifestPath) {
  requireCandidateFeed();
  const authority = await releaseManifestFacts(manifestPath);
  const directory = await mkdtemp(join(tmpdir(), 'runic-current-csharp-composition-'));
  const nugetEnvironment = {
    NUGET_PACKAGES: join(directory, '.nuget', 'packages'),
    NUGET_HTTP_CACHE_PATH: join(directory, '.nuget', 'http-cache'),
    DOTNET_CLI_HOME: join(directory, '.dotnet'),
  };
  try {
    await createConsumer(directory);
    const phases = [];
    for (const [name, args] of [
      ['restore', ['restore', 'CurrentComposition.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']],
      ['build', ['build', 'CurrentComposition.csproj', '--no-restore', '--configuration', 'Release', '--nologo']],
      ['run', ['run', '--project', 'CurrentComposition.csproj', '--no-build', '--configuration', 'Release']],
    ]) {
      const result = await run('dotnet', args, directory, nugetEnvironment);
      phases.push(phase(name, ['dotnet', ...args], result));
      assertSucceeded(name, result);
      if (name === 'run') manifestFromOutput(result.message);
    }
    const candidates = await candidateMetadata(directory);
    const negativeRestore = await run('dotnet', ['restore', 'negative/Negative.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo'], directory, nugetEnvironment);
    phases.push(phase('negative-restore', ['dotnet', 'restore', 'negative/Negative.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo'], negativeRestore));
    assertSucceeded('negative-restore', negativeRestore);
    const negativeDeclarations = [];
    for (const [kind, source] of Object.entries(negativePrograms)) {
      await writeFile(join(directory, 'negative', 'Negative.cs'), source);
      const result = await run('dotnet', ['build', 'negative/Negative.csproj', '--no-restore', '--configuration', 'Release', '--nologo'], directory, nugetEnvironment);
      const diagnostic = kind === 'missing' ? 'RAPP0000' : 'RAPP0002';
      assertFailed(`${kind} declaration`, result, diagnostic);
      negativeDeclarations.push({ kind, diagnostic, exitCode: result.exitCode });
    }
    await rm(join(directory, 'negative', 'Negative.cs'));
    const toolInstallDirectory = join(directory, 'tool-install');
    await mkdir(toolInstallDirectory);
    const installResult = await run('dotnet', [
      'tool', 'install', TOOL_CANDIDATE.identity, '--tool-path', '../.tools', '--version', TOOL_CANDIDATE.version,
      '--configfile', '../NuGet.config', '--no-cache',
    ], toolInstallDirectory, nugetEnvironment);
    phases.push(phase('tool-install', toolInstallReceiptArguments, installResult));
    assertSucceeded('tool-install', installResult);
    const tool = join(directory, '.tools', 'dotnet-runic');
    const doctorEnvironment = {
      ...nugetEnvironment,
      CSWEBUI_NATIVE_LIBRARY: join(directory, 'native', 'libwebui-fixture.so'),
      WEBUI_BROWSER_PATH: join(directory, 'bin', 'runic-browser'),
    };
    const healthyDoctor = await run(tool, ['doctor', '--project', 'CurrentComposition.csproj'], directory, doctorEnvironment);
    phases.push(phase('doctor-healthy', ['dotnet-runic', 'doctor', '--project', 'CurrentComposition.csproj'], healthyDoctor));
    assertSucceeded('doctor-healthy', healthyDoctor);
    await rm(join(directory, 'Contracts', 'generated', 'contract.g.ts'));
    const absentDoctor = await run(tool, ['doctor', '--project', 'CurrentComposition.csproj'], directory, doctorEnvironment);
    phases.push(phase('doctor-absent-contract-output', ['dotnet-runic', 'doctor', '--project', 'CurrentComposition.csproj'], absentDoctor));
    assertFailed('doctor absent contract output', absentDoctor, 'contract-outputs');
    await writeFile(join(directory, 'Contracts', 'generated', 'contract.g.ts'), `// generated contract: ${contractSource.trim()}\n`);
    await writeFile(join(directory, 'Contracts', 'generated', 'Contract.g.cs'), '// stale contract\n');
    const staleDoctor = await run(tool, ['doctor', '--project', 'CurrentComposition.csproj'], directory, doctorEnvironment);
    phases.push(phase('doctor-stale-contract-output', ['dotnet-runic', 'doctor', '--project', 'CurrentComposition.csproj'], staleDoctor));
    assertFailed('doctor stale contract output', staleDoctor, 'contract-verify');
    await writeFile(join(directory, 'Contracts', 'generated', 'Contract.g.cs'), `// generated contract: ${contractSource.trim()}\n`);
    const dryRun = await run(tool, ['dev', '--project', 'CurrentComposition.csproj', '--dry-run', '--', ...APPLICATION_ARGUMENTS], directory, nugetEnvironment);
    phases.push(phase('dev-dry-run', ['dotnet-runic', 'dev', '--project', 'CurrentComposition.csproj', '--dry-run', '--', ...APPLICATION_ARGUMENTS], dryRun));
    assertSucceeded('dev-dry-run', dryRun);
    if (!dryRun.message.includes('[dev] Frontend: npm workspace .') || !dryRun.message.includes('Dry run complete')) throw new Error('dev dry-run did not use the configured application workspace');
    const inspectArguments = ['inspect', '--project', 'CurrentComposition.csproj', '--configuration', 'Release'];
    const firstInspect = await run(tool, inspectArguments, directory, nugetEnvironment);
    phases.push(phase('inspect-first', ['dotnet-runic', ...inspectArguments], firstInspect));
    assertSucceeded('inspect-first', firstInspect);
    const secondInspect = await run(tool, inspectArguments, directory, nugetEnvironment);
    phases.push(phase('inspect-second', ['dotnet-runic', ...inspectArguments], secondInspect));
    assertSucceeded('inspect-second', secondInspect);
    const inspectManifest = manifestFromOutput(firstInspect.message);
    if (!same(inspectManifest, manifestFromOutput(secondInspect.message))) throw new Error('inspect manifest output is not deterministic');
    const receipt = {
      schema: RECEIPT_SCHEMA,
      feed: FEED,
      isolation: { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet' },
      releaseManifest: await releaseManifestAfter(manifestPath, authority),
      projectReferences: await projectReferenceEvidence(directory),
      manifest: MANIFEST,
      candidates,
      tool: toolReceipt(TOOL_CANDIDATE, await candidateDigest(TOOL_CANDIDATE)),
      phases,
      development: { configuredCommands: [['npm', 'run', 'build'], ['npm', 'run', 'dev']], applicationArguments: APPLICATION_ARGUMENTS },
      inspectManifest,
      negativeDeclarations,
    };
    const report = verifyReceipt(receipt, receipt.releaseManifest);
    if (!report.ok) throw new Error(report.errors.join('\n'));
    return receipt;
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

export async function runCurrentCSharpCompositionTwice(manifestPath) {
  const journeys = [await runCurrentCSharpComposition(manifestPath), await runCurrentCSharpComposition(manifestPath)];
  const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys };
  const report = verifyRepeatedReceipt(receipt, journeys[0].releaseManifest);
  if (!report.ok) throw new Error(report.errors.join('\n'));
  return receipt;
}

async function main() {
  const [command, manifestPath, receiptPath] = process.argv.slice(2);
  if (command === 'run-twice' && manifestPath && !receiptPath) return process.stdout.write(`${JSON.stringify(await runCurrentCSharpCompositionTwice(manifestPath), null, 2)}\n`);
  if (command === 'verify-twice' && manifestPath && receiptPath) {
    const authority = await releaseManifestFacts(manifestPath);
    const receipt = JSON.parse(await readFile(receiptPath, 'utf8'));
    const report = verifyRepeatedReceipt(receipt, authority);
    if (!report.ok) throw new Error(report.errors.join('\n'));
    return;
  }
  throw new Error('Usage: node eng/current-csharp-composition/verify.mjs run-twice <runic.release.json> | verify-twice <runic.release.json> <receipt.json>');
}

if (import.meta.main) main().catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
