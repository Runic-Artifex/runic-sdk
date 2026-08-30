#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import process from 'node:process';

export const RECEIPT_SCHEMA = 'runic.current-authenticated-service/1';
export const REPEAT_RECEIPT_SCHEMA = 'runic.current-authenticated-service-repeat/1';
export const NUGET_CANDIDATE = { identity: 'Runic.Application.Hosting', version: process.env.RUNIC_CURRENT_AUTHENTICATED_SERVICE_NUGET_VERSION ?? '0.2.0-w30.2' };
export const NUGET_FEED = 'w30-002-local-candidate-feed';
export const NPM_CANDIDATE = { identity: 'runic-current-authenticated-service-client', version: '1.0.0', source: 'w30-002-local-candidate-npm-feed' };
const feed = process.env.RUNIC_CURRENT_AUTHENTICATED_SERVICE_NUGET_FEED && resolve(process.env.RUNIC_CURRENT_AUTHENTICATED_SERVICE_NUGET_FEED);
const clientPackage = resolve(import.meta.dirname, 'client');
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const hash = async (path) => createHash('sha256').update(await readFile(path)).digest('hex');

const project = `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><PackageReference Include="${NUGET_CANDIDATE.identity}" Version="${NUGET_CANDIDATE.version}" /></ItemGroup></Project>\n`;
const program = `using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Runic.Application;
using Runic.Application.Hosting;

[assembly: RunicApplicationManifest("runic.current-authenticated-service", Version = "1.0.0", Provenance = "local-candidate")]

var policy = HostedServiceAdmissionPolicy.CreateInitial(new Uri("https://app.example.test"), new HashSet<IPAddress> { IPAddress.Parse("10.0.0.10") });
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddRunicHostedServiceAdmission(policy);
var app = builder.Build();
app.UseRunicHostedServiceForwardedHeaders(policy);
app.UseAuthentication();
var service = app.MapRunicHostedService(policy);
service.MapRunicServiceCommand("/commands/advance", "operator", (HttpContext context) => Results.Ok(new { receipt = "accepted", @event = "advanced", subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) }));
app.MapGet("/test/signin/{kind}", async (HttpContext context, string kind) => {
    var identity = new ClaimsIdentity(HostedServiceAdmissionTransport.AuthenticationScheme);
    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, kind == "malformed" ? new string('x', 129) : kind));
    identity.AddClaim(new Claim(ClaimTypes.Name, kind));
    if (kind == "operator") identity.AddClaim(new Claim(ClaimTypes.Role, "operator"));
    await context.SignInAsync(HostedServiceAdmissionTransport.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(kind == "expired" ? -1 : 5) });
    return Results.NoContent();
});
app.MapPost("/test/shutdown", (IHostApplicationLifetime lifetime) => { lifetime.StopApplication(); return Results.NoContent(); });
await app.StartAsync(); Console.WriteLine("READY " + app.Urls.Single()); await app.WaitForShutdownAsync();
`;
const client = `import assert from 'node:assert/strict';
import { clientIdentity } from 'runic-current-authenticated-service-client';
const base = process.argv[2]; assert.equal(clientIdentity, 'runic.current-authenticated-service-client/1');
const request = (path, options = {}) => fetch(base + path, { redirect: 'manual', ...options });
const cookie = (response) => (response.headers.getSetCookie?.() ?? []).map(value => value.split(';', 1)[0]).join('; ');
const signedIn = async (kind) => { const response = await request('/test/signin/' + kind); assert.equal(response.status, 204); return cookie(response); };
assert.equal((await request('/runic/service/session')).status, 401);
const malformed = await signedIn('malformed'); assert.equal((await request('/runic/service/session', { headers: { Cookie: malformed } })).status, 401);
const expired = await signedIn('expired'); assert.equal((await request('/runic/service/session', { headers: { Cookie: expired } })).status, 401);
const viewer = await signedIn('viewer'); const viewerCsrf = await request('/runic/service/csrf', { headers: { Cookie: viewer } }); assert.equal(viewerCsrf.status, 200); const viewerToken = (await viewerCsrf.json()).requestToken; const viewerCookies = [viewer, cookie(viewerCsrf)].filter(Boolean).join('; '); assert.equal((await request('/runic/service/commands/advance', { method: 'POST', headers: { Cookie: viewerCookies, Origin: 'https://app.example.test', 'X-Runic-CSRF': viewerToken } })).status, 403);
const operator = await signedIn('operator'); const session = await request('/runic/service/session', { headers: { Cookie: operator } }); assert.equal(session.status, 200); assert.deepEqual(await session.json(), { subject: 'operator', displayName: 'operator', roles: ['operator'] }); const csrf = await request('/runic/service/csrf', { headers: { Cookie: operator } }); assert.equal(csrf.status, 200); const token = (await csrf.json()).requestToken; const cookies = [operator, cookie(csrf)].filter(Boolean).join('; ');
assert.equal((await request('/runic/service/commands/advance', { method: 'POST', headers: { Cookie: cookies, Origin: 'https://evil.example.test', 'X-Runic-CSRF': token } })).status, 403);
assert.equal((await request('/runic/service/commands/advance', { method: 'POST', headers: { Cookie: cookies, Origin: 'https://app.example.test' } })).status, 403);
assert.equal((await request('/runic/service/session', { headers: { Authorization: 'Bearer forged', Cookie: cookies } })).status, 401);
const accepted = await request('/runic/service/commands/advance', { method: 'POST', headers: { Cookie: cookies, Origin: 'https://app.example.test', 'X-Runic-CSRF': token } }); assert.equal(accepted.status, 200); assert.deepEqual(await accepted.json(), { receipt: 'accepted', event: 'advanced', subject: 'operator' });
assert.equal((await request('/test/shutdown', { method: 'POST' })).status, 204); console.log('authenticated-service-client-ok');
`;

function run(command, args, cwd, env = {}) { return new Promise(resolveResult => { const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] }); const chunks = []; child.stdout.on('data', value => chunks.push(value)); child.stderr.on('data', value => chunks.push(value)); child.on('error', error => resolveResult({ ok: false, exitCode: null, reasonCode: error.code === 'ENOENT' ? 'command-not-found' : 'command-spawn-failed', output: '' })); child.on('close', exitCode => resolveResult({ ok: exitCode === 0, exitCode, reasonCode: exitCode === 0 ? null : 'command-exit-nonzero', output: Buffer.concat(chunks).toString('utf8') })); }); }
function config(packages) { return `<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="candidate" value="${feed}"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="*"/></packageSource></packageSourceMapping><config><add key="globalPackagesFolder" value="${packages}"/></config></configuration>\n`; }
function phase(name, argv, result) { return { name, argv, status: result.ok ? 'passed' : 'failed', exitCode: result.exitCode, reasonCode: result.reasonCode }; }
function requireSuccess(name, result) { if (!result.ok) throw new Error(`${name} failed: ${result.reasonCode}\n${result.output.slice(-4096)}`); }

export function verifyReceipt(receipt) {
  const errors = [];
  if (receipt?.schema !== RECEIPT_SCHEMA) errors.push('receipt schema mismatch');
  if (!same(receipt?.isolation, { nugetGlobalPackagesFolder: '.nuget/packages', npmCache: '.npm-cache', dotnetCliHome: '.dotnet' })) errors.push('cache isolation mismatch');
  if (!same(receipt?.nugetCandidate && { identity: receipt.nugetCandidate.identity, version: receipt.nugetCandidate.version, source: receipt.nugetCandidate.source }, { ...NUGET_CANDIDATE, source: NUGET_FEED }) || !receipt?.nugetCandidate?.contentHash) errors.push('NuGet provenance mismatch');
  if (!same(receipt?.npmCandidate && { identity: receipt.npmCandidate.identity, version: receipt.npmCandidate.version, source: receipt.npmCandidate.source }, NPM_CANDIDATE) || !/^[a-f0-9]{64}$/.test(receipt?.npmCandidate?.archiveSha256 ?? '')) errors.push('npm provenance mismatch');
  const expected = [['restore', ['dotnet', 'restore', 'AuthenticatedService.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo']], ['build', ['dotnet', 'build', 'AuthenticatedService.csproj', '--no-restore', '--configuration', 'Release', '--nologo']], ['npm-pack', ['npm', 'pack', '--pack-destination', '<client-archive>']], ['npm-install', ['npm', 'install', '--ignore-scripts', '<client-archive>']], ['service-client', ['node', 'client.mjs', '<local-service>']]];
  if (!Array.isArray(receipt?.phases) || receipt.phases.length !== expected.length) errors.push('phase evidence malformed'); else expected.forEach(([name, argv], index) => { const item = receipt.phases[index]; if (!item || item.name !== name || !same(item.argv, argv) || item.status !== 'passed' || item.exitCode !== 0 || item.reasonCode !== null) errors.push(`${name} evidence malformed`); });
  return { ok: errors.length === 0, errors };
}
export function verifyRepeatedReceipt(receipt) { const errors = []; if (receipt?.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push('repeat receipt malformed'); else { receipt.journeys.forEach(journey => errors.push(...verifyReceipt(journey).errors)); if (!same(receipt.journeys[0], receipt.journeys[1])) errors.push('authenticated service journeys are not deterministic'); } return { ok: errors.length === 0, errors }; }

async function one() {
  if (!feed) throw new Error('RUNIC_CURRENT_AUTHENTICATED_SERVICE_NUGET_FEED must name the exact local candidate feed');
  const directory = await mkdtemp(join(tmpdir(), 'runic-current-authenticated-service-')); const env = { NUGET_PACKAGES: join(directory, '.nuget', 'packages'), NUGET_HTTP_CACHE_PATH: join(directory, '.nuget', 'http-cache'), DOTNET_CLI_HOME: join(directory, '.dotnet'), npm_config_cache: join(directory, '.npm-cache') };
  try {
    await Promise.all([writeFile(join(directory, 'NuGet.config'), config(env.NUGET_PACKAGES)), writeFile(join(directory, 'AuthenticatedService.csproj'), project), writeFile(join(directory, 'Program.cs'), program), writeFile(join(directory, 'package.json'), JSON.stringify({ private: true, type: 'module' }) + '\n'), writeFile(join(directory, 'client.mjs'), client)]);
    const phases = []; for (const [name, command, args, cwd] of [['restore', 'dotnet', ['restore', 'AuthenticatedService.csproj', '--configfile', 'NuGet.config', '--no-cache', '--force-evaluate', '--nologo'], directory], ['build', 'dotnet', ['build', 'AuthenticatedService.csproj', '--no-restore', '--configuration', 'Release', '--nologo'], directory], ['npm-pack', 'npm', ['pack', '--pack-destination', directory], clientPackage]]) { const result = await run(command, args, cwd, env); phases.push(phase(name, [command, ...args.map(arg => arg === directory ? '<client-archive>' : arg)], result)); requireSuccess(name, result); }
    const archive = join(directory, 'runic-current-authenticated-service-client-1.0.0.tgz'); const install = await run('npm', ['install', '--ignore-scripts', archive], directory, env); phases.push(phase('npm-install', ['npm', 'install', '--ignore-scripts', '<client-archive>'], install)); requireSuccess('npm-install', install);
    const service = spawn('dotnet', ['run', '--project', 'AuthenticatedService.csproj', '--no-build', '--configuration', 'Release'], { cwd: directory, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] }); const output = []; service.stdout.on('data', value => output.push(value)); service.stderr.on('data', value => output.push(value)); const url = await new Promise((resolveUrl, reject) => { const timeout = setTimeout(() => reject(new Error('service did not start')), 10000); service.stdout.on('data', value => { const match = value.toString().match(/READY (http:\/\/\S+)/); if (match) { clearTimeout(timeout); resolveUrl(match[1]); } }); service.on('error', reject); service.on('exit', code => reject(new Error(`service exited ${code}: ${Buffer.concat(output).toString('utf8')}`))); });
    const clientResult = await run('node', ['client.mjs', url], directory, env); phases.push(phase('service-client', ['node', 'client.mjs', '<local-service>'], clientResult)); requireSuccess('service-client', clientResult); await new Promise(resolveExit => service.once('exit', resolveExit));
    const assets = JSON.parse(await readFile(join(directory, 'obj', 'project.assets.json'), 'utf8')); const key = `${NUGET_CANDIDATE.identity}/${NUGET_CANDIDATE.version}`.toLowerCase(); const library = Object.entries(assets.libraries).find(([name]) => name.toLowerCase() === key)?.[1]; const metadata = JSON.parse(await readFile(join(directory, '.nuget', 'packages', NUGET_CANDIDATE.identity.toLowerCase(), NUGET_CANDIDATE.version.toLowerCase(), '.nupkg.metadata'), 'utf8')); if (!library || metadata.source !== feed || metadata.contentHash !== library.sha512) throw new Error('NuGet provenance failed closed');
    const receipt = { schema: RECEIPT_SCHEMA, isolation: { nugetGlobalPackagesFolder: '.nuget/packages', npmCache: '.npm-cache', dotnetCliHome: '.dotnet' }, nugetCandidate: { ...NUGET_CANDIDATE, source: NUGET_FEED, contentHash: metadata.contentHash }, npmCandidate: { ...NPM_CANDIDATE, archiveSha256: await hash(archive) }, phases }; const report = verifyReceipt(receipt); if (!report.ok) throw new Error(report.errors.join('\n')); return receipt;
  } finally { await rm(directory, { recursive: true, force: true }); }
}
async function main() { const [command, path] = process.argv.slice(2); if (command === 'run-twice' && !path) { const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys: [await one(), await one()] }; const report = verifyRepeatedReceipt(receipt); if (!report.ok) throw new Error(report.errors.join('\n')); process.stdout.write(JSON.stringify(receipt, null, 2) + '\n'); return; } if (command === 'verify-twice' && path) { const report = verifyRepeatedReceipt(JSON.parse(await readFile(path, 'utf8'))); if (!report.ok) throw new Error(report.errors.join('\n')); return; } throw new Error('Usage: node eng/current-authenticated-service/verify.mjs <run-twice|verify-twice> [receipt.json]'); }
if (import.meta.main) main().catch(error => { process.stderr.write(error.message + '\n'); process.exitCode = 1; });
