#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import process from 'node:process';
import { releaseManifestAfter, releaseManifestFacts } from '../v0.2-baselines/metrics.mjs';
import { MANIFEST } from './verify.mjs';

export { MANIFEST };

export const RECEIPT_SCHEMA = 'runic.current-csharp-testing/1';
export const REPEAT_RECEIPT_SCHEMA = 'runic.current-csharp-testing-repeat/1';
const configuredCandidateFeed = process.env.RUNIC_CURRENT_CSHARP_TESTING_CANDIDATE_FEED;
const candidateFeed = configuredCandidateFeed && resolve(configuredCandidateFeed);
export const FEED = 'w10-004-local-candidate-feed';
export const APPLICATION_VERSION = process.env.RUNIC_CURRENT_CSHARP_TESTING_APPLICATION_VERSION ?? '0.2.0-preview.1e8fff0';
export function requireCandidateFeed(path = candidateFeed) {
  if (!path) throw new Error('RUNIC_CURRENT_CSHARP_TESTING_CANDIDATE_FEED must name an explicit isolated NuGet candidate feed.');
  return path;
}
export const CANDIDATES = [
  { identity: 'Runic.Application', version: APPLICATION_VERSION },
  { identity: 'Runic.Application.Testing', version: APPLICATION_VERSION },
  { identity: 'Runic.Assets', version: '0.1.0-preview.8d22423' },
];
export const INPUTS = {
  initialTime: '1970-01-01T00:00:00.0000000+00:00',
  idSeed: 41,
  environment: [['MODE', 'package-test']],
  bridge: { operation: 'ping', payload: [1, 2, 3] },
  asset: { path: 'app.js', bytes: [4, 5, 6], mediaType: 'application/javascript' },
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
  <packageSourceMapping><packageSource key="candidate"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
`;
}

const project = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="Runic.Application" Version="${APPLICATION_VERSION}" /><PackageReference Include="Runic.Application.Testing" Version="${APPLICATION_VERSION}" /></ItemGroup>
</Project>
`;

const program = `using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application;
using Runic.Application.Testing;

[assembly: RunicApplicationManifest("runic.current-csharp-composition", Version = "1.0.0", Provenance = "local-candidate")]
[assembly: RunicApplicationCapability("headless")]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationArtifact("bridge-contract", "runic.application.fixture/1", "bridge-fixture")]
[assembly: RunicApplicationArtifact("assets", "runic.assets/1:fixture", "assets-fixture")]

var host = new DeterministicApplicationTestHost(
    DateTimeOffset.UnixEpoch,
    41,
    [new("MODE", "package-test")],
    capabilities: [
        ApplicationCapabilityStatus.Available("headless"),
        ApplicationCapabilityStatus.Unavailable("desktop", "headless-package-test"),
    ]);
var timerFired = 0;
host.Timers.Schedule(TimeSpan.FromSeconds(5), () => timerFired++);
host.Timers.Advance(TimeSpan.FromSeconds(5));
if (host.Clock.GetUtcNow() != DateTimeOffset.UnixEpoch.AddSeconds(5) || timerFired != 1 ||
    host.Ids.Next("job") != "job-00000042" || host.Environment.Get("MODE") != "package-test") return 10;
host.Bridge.Send("ping", new byte[] { 1, 2, 3 });
if (!host.Bridge.TryReceive(out ApplicationBridgeMessage? message) || message?.Operation != "ping" || !message.Payload.SequenceEqual(new byte[] { 1, 2, 3 })) return 11;
host.Assets.Set("app.js", new byte[] { 4, 5, 6 }, mediaType: "application/javascript");
await host.Assets.ValidateAsync();
await using (var asset = await host.Assets.OpenReadAsync("app.js"))
{
    if (asset.Length != 3 || host.Assets.Manifest.EntryPoint.RelativePath != "index.html") return 12;
}
try
{
    host.Bridge.Send(new string('x', 257), Array.Empty<byte>());
    return 13;
}
catch (InvalidOperationException)
{
}

var application = RunicApplication.CreateBuilder([]).UseHost(host).Build();
await application.RunAsync();
if (!ReferenceEquals(host.Manifest, application.Manifest) || !host.Lifecycle.SequenceEqual(["start", "wait", "stop"]) ||
    application.Capabilities.GetRequired("headless").Availability != ApplicationCapabilityAvailability.Available ||
    application.Capabilities.GetRequired("desktop").UnavailableReason != "headless-package-test") return 14;

var faultHost = new DeterministicApplicationTestHost { WaitFailure = new InvalidOperationException("primary"), StopFailure = new InvalidOperationException("cleanup") };
await using (var faultApplication = new ApplicationHost(application.Manifest, [], faultHost))
{
    try
    {
        await faultApplication.RunAsync();
        return 15;
    }
    catch (InvalidOperationException exception) when (exception.Message == "primary")
    {
        if (!faultHost.Lifecycle.SequenceEqual(["start", "wait", "stop"])) return 16;
    }
}

var cancelledHost = new DeterministicApplicationTestHost(completeShutdownOnWait: false);
await using (var cancelledApplication = new ApplicationHost(application.Manifest, [], cancelledHost))
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await cancelledApplication.RunAsync(cancellation.Token);
        return 17;
    }
    catch (OperationCanceledException)
    {
        if (!cancelledHost.Lifecycle.SequenceEqual(["start", "wait", "stop"])) return 18;
    }
}

Console.WriteLine(application.Manifest.ToJson());
return 0;
`;

function phase(name, argv, result) {
  return { name, argv, status: result.ok ? 'passed' : 'failed', exitCode: result.exitCode, reasonCode: result.ok ? null : result.reasonCode };
}

function assertSucceeded(name, result) {
  if (!result.ok) throw new Error(`${name} failed: ${result.reasonCode}\n${result.message}`);
}

function manifestFromOutput(message) {
  let manifest;
  try { manifest = JSON.parse(message.trim()); } catch { throw new Error('testing profile did not emit a generated manifest'); }
  if (!same(manifest, MANIFEST)) throw new Error('testing profile did not consume the current generated manifest');
  return manifest;
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

export function verifyReceipt(receipt, authority) {
  const errors = [];
  if (!receipt || receipt.schema !== RECEIPT_SCHEMA) errors.push('receipt schema mismatch');
  if (receipt?.feed !== FEED) errors.push('candidate feed mismatch');
  if (!same(receipt?.isolation, { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet' })) errors.push('cache isolation mismatch');
  if (!same(receipt?.releaseManifest, authority)) errors.push('release authority identity mismatch');
  if (!same(receipt?.projectReferences, [])) errors.push('product-source project references are prohibited');
  if (!same(receipt?.manifest, MANIFEST)) errors.push('generated manifest mismatch');
  if (!same(receipt?.inputs, INPUTS)) errors.push('deterministic input mismatch');
  if (!Array.isArray(receipt?.candidates) || receipt.candidates.length !== CANDIDATES.length || receipt.candidates.some((candidate, index) => !same({ identity: candidate?.identity, version: candidate?.version, source: candidate?.source }, { ...CANDIDATES[index], source: FEED }) || typeof candidate?.contentHash !== 'string' || candidate.contentHash.length === 0)) errors.push('candidate metadata mismatch');
  const expectedPhases = [
    ['restore', ['dotnet', 'restore', 'TestingProfile.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']],
    ['build', ['dotnet', 'build', 'TestingProfile.csproj', '--no-restore', '--configuration', 'Release', '--nologo']],
    ['test', ['dotnet', 'run', '--project', 'TestingProfile.csproj', '--no-build', '--configuration', 'Release']],
  ];
  if (!Array.isArray(receipt?.phases) || receipt.phases.length !== expectedPhases.length) errors.push('test phase evidence malformed');
  else expectedPhases.forEach(([name, argv], index) => {
    const actual = receipt.phases[index];
    if (!actual || actual.name !== name || !same(actual.argv, argv) || actual.status !== 'passed' || actual.exitCode !== 0 || actual.reasonCode !== null) errors.push(`${name} evidence malformed`);
  });
  return { ok: errors.length === 0, errors };
}

export function verifyRepeatedReceipt(receipt, authority) {
  const errors = [];
  if (!receipt || receipt.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt.journeys) || receipt.journeys.length !== 2) errors.push('repeat receipt schema mismatch');
  else {
    receipt.journeys.forEach((journey, index) => errors.push(...verifyReceipt(journey, authority).errors.map((error) => `journey ${index + 1}: ${error}`)));
    if (!same(receipt.journeys[0], receipt.journeys[1])) errors.push('testing journeys are not deterministic');
  }
  return { ok: errors.length === 0, errors };
}

export async function runCurrentCSharpTesting(manifestPath) {
  requireCandidateFeed();
  const authority = await releaseManifestFacts(manifestPath);
  const directory = await mkdtemp(join(tmpdir(), 'runic-current-csharp-testing-'));
  const environment = {
    NUGET_PACKAGES: join(directory, '.nuget', 'packages'),
    NUGET_HTTP_CACHE_PATH: join(directory, '.nuget', 'http-cache'),
    DOTNET_CLI_HOME: join(directory, '.dotnet'),
  };
  try {
    await Promise.all([
      writeFile(join(directory, 'NuGet.config'), nugetConfig(join(directory, '.nuget', 'packages'))),
      writeFile(join(directory, 'TestingProfile.csproj'), project),
      writeFile(join(directory, 'Program.cs'), program),
    ]);
    const phases = [];
    for (const [name, args] of [
      ['restore', ['restore', 'TestingProfile.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']],
      ['build', ['build', 'TestingProfile.csproj', '--no-restore', '--configuration', 'Release', '--nologo']],
      ['test', ['run', '--project', 'TestingProfile.csproj', '--no-build', '--configuration', 'Release']],
    ]) {
      const result = await run('dotnet', args, directory, environment);
      phases.push(phase(name, ['dotnet', ...args], result));
      assertSucceeded(name, result);
      if (name === 'test') manifestFromOutput(result.message);
    }
    const source = await readFile(join(directory, 'TestingProfile.csproj'), 'utf8');
    const projectReferences = [...source.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)].map((match) => match[1]);
    if (projectReferences.length) throw new Error('testing profile must not use product-source project references');
    const receipt = {
      schema: RECEIPT_SCHEMA,
      feed: FEED,
      isolation: { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet' },
      releaseManifest: await releaseManifestAfter(manifestPath, authority),
      projectReferences,
      manifest: MANIFEST,
      inputs: INPUTS,
      candidates: await candidateMetadata(directory),
      phases,
    };
    const report = verifyReceipt(receipt, receipt.releaseManifest);
    if (!report.ok) throw new Error(report.errors.join('\n'));
    return receipt;
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

export async function runCurrentCSharpTestingTwice(manifestPath) {
  const journeys = [await runCurrentCSharpTesting(manifestPath), await runCurrentCSharpTesting(manifestPath)];
  const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys };
  const report = verifyRepeatedReceipt(receipt, journeys[0].releaseManifest);
  if (!report.ok) throw new Error(report.errors.join('\n'));
  return receipt;
}

async function main() {
  const [command, manifestPath, receiptPath] = process.argv.slice(2);
  if (command === 'run-twice' && manifestPath && !receiptPath) return process.stdout.write(`${JSON.stringify(await runCurrentCSharpTestingTwice(manifestPath), null, 2)}\n`);
  if (command === 'verify-twice' && manifestPath && receiptPath) {
    const authority = await releaseManifestFacts(manifestPath);
    const receipt = JSON.parse(await readFile(receiptPath, 'utf8'));
    const report = verifyRepeatedReceipt(receipt, authority);
    if (!report.ok) throw new Error(report.errors.join('\n'));
    return;
  }
  throw new Error('Usage: node eng/current-csharp-composition/testing-profile.mjs run-twice <runic.release.json> | verify-twice <runic.release.json> <receipt.json>');
}

if (import.meta.main) main().catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
