#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import process from 'node:process';

export const RECEIPT_SCHEMA = 'runic.current-service-declaration/1';
export const REPEAT_RECEIPT_SCHEMA = 'runic.current-service-declaration-repeat/1';
export const FEED = 'w30-001-local-candidate-feed';
export const CANDIDATE = { identity: 'Runic.Application.Hosting', version: process.env.RUNIC_CURRENT_SERVICE_DECLARATION_VERSION ?? '0.2.0-w30.1' };
const candidateFeed = process.env.RUNIC_CURRENT_SERVICE_DECLARATION_NUGET_FEED && resolve(process.env.RUNIC_CURRENT_SERVICE_DECLARATION_NUGET_FEED);
const declarationPath = join(import.meta.dirname, 'policy.json');
export const DECLARATION = JSON.parse(await readFile(declarationPath, 'utf8'));
export const DECLARATION_SHA256 = createHash('sha256').update(await readFile(declarationPath)).digest('hex');
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);

const project = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="${CANDIDATE.identity}" Version="${CANDIDATE.version}" /></ItemGroup>
</Project>
`;
const program = `using System.Net;
using System.Text.Json;
using Runic.Application;
using Runic.Application.Hosting;

[assembly: RunicApplicationManifest("runic.current-service-declaration", Version = "1.0.0", Provenance = "local-candidate")]

HostedServiceAdmissionPolicy policy = HostedServiceAdmissionPolicy.CreateInitial(
    new Uri("https://app.example.test"),
    new HashSet<IPAddress> { IPAddress.Parse("10.0.0.10") });
Console.WriteLine(JsonSerializer.Serialize(new {
    schema = "runic.hosted-service-admission/1",
    authentication = new { flow = HostedServiceAdmissionPolicy.AuthenticationFlow, carrier = HostedServiceAdmissionPolicy.SessionCarrier, sessionCookieName = HostedServiceAdmissionPolicy.SessionCookieName, browserBearerTokens = false },
    topology = new { publicOrigin = policy.PublicOrigin.AbsoluteUri.TrimEnd('/'), tlsTerminator = HostedServiceAdmissionPolicy.TlsTerminator, serviceRoutePrefix = HostedServiceAdmissionPolicy.ServiceRoutePrefix, oidcCallbackRoute = HostedServiceAdmissionPolicy.OidcCallbackRoute, frontendProcess = HostedServiceAdmissionPolicy.FrontendProcess },
    proxy = new { trustedAddresses = policy.TrustedProxyAddresses.Select(address => address.ToString()).OrderBy(address => address) },
    csrf = new { antiforgeryHeaderName = HostedServiceAdmissionPolicy.AntiforgeryHeaderName, unsafeRequestOrigin = HostedServiceAdmissionPolicy.UnsafeRequestOriginPolicy },
    ownership = new { serviceIdentitySessionEndpointPolicy = HostedServiceAdmissionPolicy.ServicePolicyOwner, frontendMayForwardOpaqueCookieOnly = HostedServiceAdmissionPolicy.FrontendMayForwardOpaqueCookieOnly, w20WebSocketRemainsLocalOnly = HostedServiceAdmissionPolicy.W20WebSocketRemainsLocalOnly },
}));
`;

function run(command, args, cwd, env = {}) {
  return new Promise((resolveResult) => {
    const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
    const output = [];
    child.stdout.on('data', (chunk) => output.push(chunk));
    child.stderr.on('data', (chunk) => output.push(chunk));
    child.on('error', (error) => resolveResult({ ok: false, exitCode: null, reasonCode: error.code === 'ENOENT' ? 'command-not-found' : 'command-spawn-failed', output: '' }));
    child.on('close', (exitCode) => resolveResult({ ok: exitCode === 0, exitCode, reasonCode: exitCode === 0 ? null : 'command-exit-nonzero', output: Buffer.concat(output).toString('utf8').slice(-4096) }));
  });
}

function phase(name, argv, result) {
  return { name, argv, status: result.ok ? 'passed' : 'failed', exitCode: result.exitCode, reasonCode: result.reasonCode };
}
function requireSuccess(name, result) {
  if (!result.ok) throw new Error(`${name} failed: ${result.reasonCode}\n${result.output}`);
}
function nugetConfig(packages) {
  return `<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="candidate" value="${candidateFeed}"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="*"/></packageSource></packageSourceMapping><config><add key="globalPackagesFolder" value="${packages}"/></config></configuration>\n`;
}

export function verifyDeclaration(value) {
  const errors = [];
  if (value?.schema !== 'runic.hosted-service-admission/1') errors.push('policy schema mismatch');
  if (!same(value?.authentication, DECLARATION.authentication)) errors.push('authentication carrier mismatch');
  if (!same(value?.topology, DECLARATION.topology)) errors.push('hosted topology mismatch');
  if (!same(value?.proxy, DECLARATION.proxy)) errors.push('trusted proxy policy mismatch');
  if (!same(value?.csrf, DECLARATION.csrf)) errors.push('CSRF policy mismatch');
  if (!same(value?.ownership, DECLARATION.ownership)) errors.push('C# ownership policy mismatch');
  return { ok: errors.length === 0, errors };
}

export function verifyReceipt(receipt) {
  const errors = [...verifyDeclaration(receipt?.declaration).errors];
  if (receipt?.schema !== RECEIPT_SCHEMA) errors.push('receipt schema mismatch');
  if (receipt?.feed !== FEED || !same(receipt?.isolation, { nugetGlobalPackagesFolder: '.nuget/packages', dotnetCliHome: '.dotnet' })) errors.push('isolated candidate feed mismatch');
  if (receipt?.declarationSha256 !== DECLARATION_SHA256) errors.push('committed policy hash mismatch');
  if (!same(receipt?.candidate && { identity: receipt.candidate.identity, version: receipt.candidate.version, source: receipt.candidate.source }, { ...CANDIDATE, source: FEED }) || typeof receipt?.candidate?.contentHash !== 'string' || receipt.candidate.contentHash.length === 0) errors.push('candidate provenance mismatch');
  if (!same(receipt?.servicePolicy, DECLARATION)) errors.push('packaged service declaration mismatch');
  const expected = [['restore', ['dotnet', 'restore', 'ServiceDeclaration.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']], ['build', ['dotnet', 'build', 'ServiceDeclaration.csproj', '--no-restore', '--configuration', 'Release', '--nologo']], ['run', ['dotnet', 'run', '--project', 'ServiceDeclaration.csproj', '--no-build', '--configuration', 'Release']]];
  if (!Array.isArray(receipt?.phases) || receipt.phases.length !== expected.length) errors.push('phase evidence malformed');
  else expected.forEach(([name, argv], index) => { const item = receipt.phases[index]; if (!item || item.name !== name || !same(item.argv, argv) || item.status !== 'passed' || item.exitCode !== 0 || item.reasonCode !== null) errors.push(`${name} evidence malformed`); });
  return { ok: errors.length === 0, errors };
}

export function verifyRepeatedReceipt(receipt) {
  const errors = [];
  if (receipt?.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push('repeat receipt malformed');
  else {
    for (const journey of receipt.journeys) errors.push(...verifyReceipt(journey).errors);
    if (!same(receipt.journeys[0], receipt.journeys[1])) errors.push('service declaration journeys are not deterministic');
  }
  return { ok: errors.length === 0, errors };
}

async function candidateMetadata(directory) {
  const assets = JSON.parse(await readFile(join(directory, 'obj', 'project.assets.json'), 'utf8'));
  const key = `${CANDIDATE.identity}/${CANDIDATE.version}`.toLowerCase();
  const library = Object.entries(assets.libraries ?? {}).find(([name]) => name.toLowerCase() === key)?.[1];
  const packageDirectory = join(directory, '.nuget', 'packages', CANDIDATE.identity.toLowerCase(), CANDIDATE.version.toLowerCase());
  const metadata = JSON.parse(await readFile(join(packageDirectory, '.nupkg.metadata'), 'utf8'));
  if (!library || library.type !== 'package' || metadata.source !== candidateFeed || metadata.contentHash !== library.sha512) throw new Error('NuGet provenance failed closed for hosted-service declaration');
  return { ...CANDIDATE, source: FEED, contentHash: metadata.contentHash };
}

export async function runServiceDeclaration() {
  if (!candidateFeed) throw new Error('RUNIC_CURRENT_SERVICE_DECLARATION_NUGET_FEED must name the exact local candidate feed');
  const directory = await mkdtemp(join(tmpdir(), 'runic-current-service-declaration-'));
  const environment = { NUGET_PACKAGES: join(directory, '.nuget', 'packages'), NUGET_HTTP_CACHE_PATH: join(directory, '.nuget', 'http-cache'), DOTNET_CLI_HOME: join(directory, '.dotnet') };
  try {
    await Promise.all([writeFile(join(directory, 'NuGet.config'), nugetConfig(environment.NUGET_PACKAGES)), writeFile(join(directory, 'ServiceDeclaration.csproj'), project), writeFile(join(directory, 'Program.cs'), program)]);
    const phases = []; let emittedPolicy;
    for (const [name, command, args] of [['restore', 'dotnet', ['restore', 'ServiceDeclaration.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']], ['build', 'dotnet', ['build', 'ServiceDeclaration.csproj', '--no-restore', '--configuration', 'Release', '--nologo']], ['run', 'dotnet', ['run', '--project', 'ServiceDeclaration.csproj', '--no-build', '--configuration', 'Release']]]) {
      const result = await run(command, args, directory, environment); phases.push(phase(name, [command, ...args], result)); requireSuccess(name, result);
      if (name === 'run') {
        const line = result.output.trim().split('\n').find((item) => item.startsWith('{'));
        if (!line) throw new Error('packaged service declaration did not emit JSON');
        emittedPolicy = JSON.parse(line); if (!same(emittedPolicy, DECLARATION)) throw new Error('packaged service declaration mismatch');
      }
    }
    const receipt = { schema: RECEIPT_SCHEMA, feed: FEED, isolation: { nugetGlobalPackagesFolder: '.nuget/packages', dotnetCliHome: '.dotnet' }, declaration: DECLARATION, declarationSha256: DECLARATION_SHA256, candidate: await candidateMetadata(directory), servicePolicy: emittedPolicy, phases };
    const report = verifyReceipt(receipt); if (!report.ok) throw new Error(report.errors.join('\n'));
    return receipt;
  } finally { await rm(directory, { recursive: true, force: true }); }
}

async function main() {
  const [command, receiptPath] = process.argv.slice(2);
  if (command === 'run-twice' && !receiptPath) {
    const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys: [await runServiceDeclaration(), await runServiceDeclaration()] };
    const report = verifyRepeatedReceipt(receipt); if (!report.ok) throw new Error(report.errors.join('\n'));
    process.stdout.write(JSON.stringify(receipt, null, 2) + '\n'); return;
  }
  if (command === 'verify-twice' && receiptPath) {
    const report = verifyRepeatedReceipt(JSON.parse(await readFile(receiptPath, 'utf8'))); if (!report.ok) throw new Error(report.errors.join('\n'));
    return;
  }
  throw new Error('Usage: node eng/current-service-declaration/verify.mjs <run-twice|verify-twice> [receipt.json]');
}
if (import.meta.main) main().catch((error) => { process.stderr.write(error.message + '\n'); process.exitCode = 1; });
