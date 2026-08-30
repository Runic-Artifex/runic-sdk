#!/usr/bin/env node
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { createServer } from "node:net";
import { mkdtemp, readFile, rm, writeFile, mkdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";
import process from "node:process";

export const RECEIPT_SCHEMA = "runic.current-hosted-deployment/1";
export const REPEAT_RECEIPT_SCHEMA = "runic.current-hosted-deployment-repeat/1";
export const NUGET_FEED = "w30-004-local-candidate-nuget-feed";
export const NPM_FEED = "w30-004-local-candidate-npm-feed";
const version = process.env.RUNIC_CURRENT_HOSTED_DEPLOYMENT_VERSION ?? "0.2.0-w30.4";
export const NUGET_CANDIDATES = ["Runic.Application", "Runic.Application.Bridge", "Runic.Application.Hosting"]
  .map((identity) => ({ identity, version }));
export const NPM_CANDIDATES = [
  { identity: "@runic-artifex/svelte", version: "0.1.0-preview.0" },
  { identity: "@runic-artifex/sveltekit", version: "0.1.0-preview.0" },
];
const root = resolve(import.meta.dirname, "../..");
const topologyPath = join(import.meta.dirname, "topology.json");
export const TOPOLOGY = JSON.parse(await readFile(topologyPath, "utf8"));
export const TOPOLOGY_SHA256 = createHash("sha256").update(await readFile(topologyPath)).digest("hex");
const nugetFeed = process.env.RUNIC_CURRENT_HOSTED_DEPLOYMENT_NUGET_FEED && resolve(process.env.RUNIC_CURRENT_HOSTED_DEPLOYMENT_NUGET_FEED);
const archives = (process.env.RUNIC_CURRENT_HOSTED_DEPLOYMENT_NPM_ARCHIVES ?? "")
  .split(",").filter(Boolean).map((path) => resolve(path));
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const hash = async (path) => createHash("sha256").update(await readFile(path)).digest("hex");

const project = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="Runic.Application.Hosting" Version="${version}" /></ItemGroup>
</Project>
`;

const program = `using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Runic.Application;
using Runic.Application.Hosting;

[assembly: RunicApplicationManifest("runic.current-hosted-deployment", Version = "1.0.0", Provenance = "local-candidate")]

var builder = WebApplication.CreateBuilder(args);
HostedDeploymentConfiguration deployment = HostedDeploymentConfiguration.Load(builder.Configuration);
HostedServiceAdmissionPolicy policy = deployment.CreateAdmissionPolicy();
builder.Services.AddRunicHostedServiceAdmission(policy);
var app = builder.Build();
app.UseRunicHostedServiceForwardedHeaders(policy);
app.UseAuthentication();
app.MapRunicHostedDeploymentHealth(deployment);
app.MapRunicHostedService(policy);
await app.StartAsync();
Console.WriteLine("READY " + app.Urls.Single());
await app.WaitForShutdownAsync();
`;

const svelteConfig = `import adapter from "@sveltejs/adapter-node";
export default { kit: { adapter: adapter({ out: "../../ejected/frontend/build", precompress: false }) } };
`;
const viteConfig = `import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";
export default defineConfig({ plugins: [sveltekit()] });
`;
const appHtml = `<!doctype html><html lang="en"><head><meta charset="utf-8" />%sveltekit.head%</head><body><div style="display:contents">%sveltekit.body%</div></body></html>\n`;
const pageServer = `import { RUNIC_HOSTED_SESSION_PATH } from "@runic-artifex/sveltekit/hosted";
export const load = () => ({ sessionPath: RUNIC_HOSTED_SESSION_PATH });
`;
const page = `<script lang="ts">
  let { data } = $props();
</script>

<h1>Runic hosted deployment</h1>
<p>{data.sessionPath}</p>
`;

function run(command, args, cwd, env = {}) {
  return new Promise((done) => {
    const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ["ignore", "pipe", "pipe"] });
    const output = [];
    child.stdout.on("data", (value) => output.push(value));
    child.stderr.on("data", (value) => output.push(value));
    child.on("error", (error) => done({ ok: false, exitCode: null, reasonCode: error.code === "ENOENT" ? "command-not-found" : "command-spawn-failed", output: "" }));
    child.on("close", (exitCode) => done({ ok: exitCode === 0, exitCode, reasonCode: exitCode === 0 ? null : "command-exit-nonzero", output: Buffer.concat(output).toString("utf8") }));
  });
}

function phase(name, argv, result) {
  return { name, argv, status: result.ok ? "passed" : "failed", exitCode: result.exitCode, reasonCode: result.reasonCode };
}

function requireSuccess(name, result) {
  if (!result.ok) throw new Error(`${name} failed: ${result.reasonCode}\n${result.output.slice(-4096)}`);
}

function requireFailure(name, result, expected) {
  if (result.ok || !result.output.includes(expected)) throw new Error(`${name} did not fail closed with '${expected}'.\n${result.output.slice(-4096)}`);
}

function nugetConfig(packages) {
  return `<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="candidate" value="${nugetFeed}"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="*"/></packageSource></packageSourceMapping><config><add key="globalPackagesFolder" value="${packages}"/></config></configuration>\n`;
}

function deploymentSettings() {
  return {
    Runic: {
      HostedDeployment: {
        PublicOrigin: TOPOLOGY.publicOrigin,
        TrustedProxyAddresses: TOPOLOGY.trustedProxyAddresses.join(","),
        ServiceUpstream: TOPOLOGY.serviceUpstream,
        FrontendUpstream: TOPOLOGY.frontendUpstream,
        StaticAssetsPath: TOPOLOGY.staticAssets.path,
        OidcAuthority: TOPOLOGY.oidc.authority,
        OidcClientId: TOPOLOGY.oidc.clientId,
      },
    },
  };
}

const registryProgram = "import { createReadStream,readFileSync } from 'node:fs';import { createServer } from 'node:http';import { createHash } from 'node:crypto';import { basename } from 'node:path';import { execFileSync } from 'node:child_process';const entries=new Map(process.argv.slice(1).map(a=>{const manifest=JSON.parse(execFileSync('tar',['-xOf',a,'package/package.json'],{encoding:'utf8'}));return [manifest.name,{a,manifest}]}));const server=createServer((q,s)=>{const p=new URL(q.url??'/','http://localhost').pathname;for(const [n,e]of entries){const a='/archive/'+basename(e.a);if(decodeURIComponent(p.slice(1))===n){const tarball='http://127.0.0.1:'+server.address().port+a;const integrity='sha512-'+createHash('sha512').update(readFileSync(e.a)).digest('base64');s.writeHead(200,{'content-type':'application/json'});return s.end(JSON.stringify({name:n,'dist-tags':{latest:e.manifest.version},versions:{[e.manifest.version]:{...e.manifest,dist:{tarball,integrity}}}}))}if(p===a){s.writeHead(200);return createReadStream(e.a).pipe(s)}}s.writeHead(404);s.end()});server.listen(0,'127.0.0.1',()=>process.stdout.write('http://127.0.0.1:'+server.address().port+'\\n'));process.on('SIGTERM',()=>server.close(()=>process.exit(0)));";

async function startRegistry() {
  if (archives.length !== NPM_CANDIDATES.length) throw new Error("RUNIC_CURRENT_HOSTED_DEPLOYMENT_NPM_ARCHIVES must name exact current Runic Svelte and SvelteKit archives.");
  const manifests = [];
  for (const archive of archives) {
    const result = await run("tar", ["-xOf", archive, "package/package.json"], root);
    requireSuccess(`read ${basename(archive)}`, result);
    manifests.push(JSON.parse(result.output));
  }
  if (!same(manifests.map((item) => ({ identity: item.name, version: item.version })).sort((left, right) => left.identity.localeCompare(right.identity)), [...NPM_CANDIDATES].map((item) => ({ identity: item.identity, version: item.version })).sort((left, right) => left.identity.localeCompare(right.identity))))
    throw new Error("npm archives must contain exactly the current Runic Svelte and SvelteKit candidates.");
  const child = spawn("node", ["--input-type=module", "--eval", registryProgram, ...archives], { cwd: root, stdio: ["ignore", "pipe", "pipe"] });
  const url = await new Promise((resolveUrl, reject) => {
    const timer = setTimeout(() => reject(new Error("local npm registry did not bind")), 5000);
    child.stdout.once("data", (value) => { clearTimeout(timer); resolveUrl(value.toString("utf8").trim()); });
    child.once("error", reject);
    child.stderr.once("data", (value) => reject(new Error(value.toString("utf8"))));
  });
  return { child, url, manifests };
}

function frontendPackage(versions) {
  return JSON.stringify({
    private: true,
    type: "module",
    scripts: { check: "svelte-kit sync && svelte-check --tsconfig ./tsconfig.json", build: "vite build" },
    dependencies: {
      "@runic-artifex/svelte": versions["@runic-artifex/svelte"],
      "@runic-artifex/sveltekit": versions["@runic-artifex/sveltekit"],
      "@sveltejs/kit": "2.70.2",
      svelte: "5.56.8",
    },
    devDependencies: { "@sveltejs/adapter-node": "5.5.2", "@sveltejs/vite-plugin-svelte": "7.2.0", "@types/node": "24.10.4", "svelte-check": "4.7.5", typescript: "5.9.3", vite: "8.2.1" },
  }, null, 2) + "\n";
}

async function writeFixture(directory, versions) {
  const source = join(directory, "source");
  const frontend = join(source, "frontend");
  const ejected = join(directory, "ejected");
  await Promise.all([
    mkdir(join(frontend, "src", "routes"), { recursive: true }),
    mkdir(join(ejected, "frontend"), { recursive: true }),
    mkdir(join(ejected, "service"), { recursive: true }),
  ]);
  await Promise.all([
    writeFile(join(source, "NuGet.config"), nugetConfig(join(directory, ".nuget", "packages")), "utf8"),
    writeFile(join(source, "HostedDeployment.csproj"), project, "utf8"),
    writeFile(join(source, "Program.cs"), program, "utf8"),
    writeFile(join(source, "appsettings.json"), JSON.stringify(deploymentSettings(), null, 2) + "\n", "utf8"),
    writeFile(join(frontend, "package.json"), frontendPackage(versions), "utf8"),
    writeFile(join(frontend, ".npmrc"), `@runic-artifex:registry=${versions.registry}\n`, "utf8"),
    writeFile(join(frontend, "svelte.config.js"), svelteConfig, "utf8"),
    writeFile(join(frontend, "vite.config.js"), viteConfig, "utf8"),
    writeFile(join(frontend, "tsconfig.json"), "{\"extends\":\"./.svelte-kit/tsconfig.json\",\"compilerOptions\":{\"allowJs\":true,\"checkJs\":true}}\n", "utf8"),
    writeFile(join(frontend, "src", "app.html"), appHtml, "utf8"),
    writeFile(join(frontend, "src", "routes", "+page.server.ts"), pageServer, "utf8"),
    writeFile(join(frontend, "src", "routes", "+page.svelte"), page, "utf8"),
    writeFile(join(ejected, "deployment.json"), JSON.stringify({ schema: TOPOLOGY.schema, topology: TOPOLOGY, topologySha256: TOPOLOGY_SHA256 }, null, 2) + "\n", "utf8"),
  ]);
  return { source, frontend, ejected };
}

async function nugetMetadata(source, directory) {
  const assets = JSON.parse(await readFile(join(source, "obj", "project.assets.json"), "utf8"));
  return Promise.all(NUGET_CANDIDATES.map(async (candidate) => {
    const key = `${candidate.identity}/${candidate.version}`.toLowerCase();
    const library = Object.entries(assets.libraries ?? {}).find(([name]) => name.toLowerCase() === key)?.[1];
    const metadata = JSON.parse(await readFile(join(directory, ".nuget", "packages", candidate.identity.toLowerCase(), candidate.version.toLowerCase(), ".nupkg.metadata"), "utf8"));
    if (!library || library.type !== "package" || metadata.source !== nugetFeed || metadata.contentHash !== library.sha512) throw new Error(`NuGet provenance failed closed for ${candidate.identity}.`);
    return { ...candidate, source: NUGET_FEED, contentHash: metadata.contentHash };
  }));
}

async function npmMetadata(frontend, registry) {
  const lock = JSON.parse(await readFile(join(frontend, "package-lock.json"), "utf8"));
  return Promise.all(NPM_CANDIDATES.map(async (candidate) => {
    const item = lock.packages?.[`node_modules/${candidate.identity}`];
    if (!item || item.version !== candidate.version || !item.resolved?.startsWith(registry.url) || !item.integrity) throw new Error(`npm provenance failed closed for ${candidate.identity}.`);
    const index = registry.manifests.findIndex((manifest) => manifest.name === candidate.identity);
    return { ...candidate, source: NPM_FEED, archiveSha256: await hash(archives[index]), integrity: item.integrity };
  }));
}

function deploymentEnvironment(directory, extra = {}) {
  return {
    NUGET_PACKAGES: join(directory, ".nuget", "packages"),
    NUGET_HTTP_CACHE_PATH: join(directory, ".nuget", "http-cache"),
    DOTNET_CLI_HOME: join(directory, ".dotnet"),
    npm_config_cache: join(directory, ".npm-cache"),
    ...extra,
  };
}

async function openPort() {
  const server = createServer();
  await new Promise((resolveReady, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", resolveReady); });
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("could not reserve a loopback port");
  const port = address.port;
  await new Promise((resolveClosed) => server.close(resolveClosed));
  return port;
}

async function waitFor(url, expected) {
  const deadline = Date.now() + 15000;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      const body = await response.text();
      if (response.ok && expected(body)) return;
      lastError = new Error(`${response.status}: ${body}`);
    } catch (error) { lastError = error; }
    await new Promise((resolveWait) => setTimeout(resolveWait, 100));
  }
  throw new Error(`Timed out waiting for ${url}: ${lastError?.message ?? "unknown failure"}`);
}

async function stop(child) {
  if (child.exitCode !== null) return;
  child.kill("SIGTERM");
  await new Promise((resolveExit) => child.once("exit", resolveExit));
}

function start(command, args, cwd, env) {
  const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ["ignore", "pipe", "pipe"] });
  child.output = [];
  child.stdout.on("data", (value) => child.output.push(value));
  child.stderr.on("data", (value) => child.output.push(value));
  return child;
}

const phases = [
  ["restore", ["dotnet", "restore", "HostedDeployment.csproj", "--configfile", "NuGet.config", "--no-cache", "--force-evaluate", "--nologo"]],
  ["publish", ["dotnet", "publish", "HostedDeployment.csproj", "--no-restore", "--configuration", "Release", "--output", "<ejected-service>", "--nologo"]],
  ["frontend-install", ["npm", "install", "--ignore-scripts", "--legacy-peer-deps"]],
  ["frontend-check", ["npm", "run", "check"]],
  ["frontend-build", ["npm", "run", "build"]],
  ["ejected-frontend-install", ["npm", "install", "--omit=dev", "--ignore-scripts", "--legacy-peer-deps"]],
  ["missing-secret", ["dotnet", "<ejected-service>"]],
  ["unsafe-public-origin", ["dotnet", "<ejected-service>"]],
  ["service-health-readiness", ["dotnet", "<ejected-service>"]],
  ["frontend-ssr", ["node", "<ejected-frontend-server>"]],
];

export function verifyTopology(value) {
  const errors = [];
  if (value?.schema !== "runic.hosted-deployment/1") errors.push("topology schema mismatch");
  if (value?.publicOrigin !== "https://app.example.test" || !same(value?.trustedProxyAddresses, ["10.0.0.10"])) errors.push("public origin or trusted proxy mismatch");
  if (value?.serviceUpstream !== "http://service.internal.test:8080" || value?.frontendUpstream !== "http://frontend.internal.test:3000") errors.push("private upstream mismatch");
  if (!same(value?.staticAssets, { owner: "sveltekit", path: "frontend/build" })) errors.push("static asset ownership mismatch");
  if (!same(value?.oidc, { authority: "https://idp.example.test", clientId: "runic-hosted" })) errors.push("OIDC configuration mismatch");
  if (value?.secret?.configurationKey !== "Runic__HostedDeployment__OidcClientSecret") errors.push("secret injection mismatch");
  const routes = [
    { path: "/runic/service/*", upstream: "service" }, { path: "/signin-oidc", upstream: "service" },
    { path: "/runic/health", upstream: "service" }, { path: "/runic/ready", upstream: "service" }, { path: "/*", upstream: "frontend" },
  ];
  if (value?.proxy?.tlsTerminator !== "trusted-reverse-proxy" || !same(value?.proxy?.routes, routes) || value?.proxy?.cors !== false || value?.proxy?.publicApplicationBridgeWebSocket !== false)
    errors.push("proxy/TLS or local WebSocket boundary mismatch");
  return { ok: errors.length === 0, errors };
}

export function verifyReceipt(receipt) {
  const errors = [...verifyTopology(receipt?.topology).errors];
  if (receipt?.schema !== RECEIPT_SCHEMA || receipt?.topologySha256 !== TOPOLOGY_SHA256) errors.push("receipt or topology hash mismatch");
  if (!same(receipt?.isolation, { nugetGlobalPackagesFolder: ".nuget/packages", nugetHttpCachePath: ".nuget/http-cache", dotnetCliHome: ".dotnet", npmCache: ".npm-cache" })) errors.push("cache isolation mismatch");
  if (!Array.isArray(receipt?.nugetCandidates) || receipt.nugetCandidates.length !== NUGET_CANDIDATES.length || receipt.nugetCandidates.some((candidate, index) => !same({ identity: candidate?.identity, version: candidate?.version, source: candidate?.source }, { ...NUGET_CANDIDATES[index], source: NUGET_FEED }) || !candidate.contentHash)) errors.push("NuGet provenance mismatch");
  if (!Array.isArray(receipt?.npmCandidates) || receipt.npmCandidates.length !== NPM_CANDIDATES.length || receipt.npmCandidates.some((candidate, index) => !same({ identity: candidate?.identity, version: candidate?.version, source: candidate?.source }, { ...NPM_CANDIDATES[index], source: NPM_FEED }) || !candidate.integrity || !/^[a-f0-9]{64}$/.test(candidate.archiveSha256 ?? ""))) errors.push("npm provenance mismatch");
  if (!same(receipt?.ejection, { service: "service", frontend: "frontend/build", staticAssetsOwner: "sveltekit", topologyBound: true, secretAbsent: true })) errors.push("ejection boundary mismatch");
  if (!Array.isArray(receipt?.phases) || receipt.phases.length !== phases.length) errors.push("phase evidence malformed");
  else receipt.phases.forEach((item, index) => {
    const [name, argv] = phases[index];
    const expectedStatus = name === "missing-secret" || name === "unsafe-public-origin" ? "failed" : "passed";
    if (!item || item.name !== name || !same(item.argv, argv) || item.status !== expectedStatus || (expectedStatus === "passed" ? item.exitCode !== 0 || item.reasonCode !== null : item.exitCode === 0)) errors.push(`${name} evidence malformed`);
  });
  return { ok: errors.length === 0, errors };
}

export function verifyRepeatedReceipt(receipt) {
  const errors = [];
  if (receipt?.schema !== REPEAT_RECEIPT_SCHEMA || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push("repeat receipt malformed");
  else {
    receipt.journeys.forEach((journey) => errors.push(...verifyReceipt(journey).errors));
    if (!same(receipt.journeys[0], receipt.journeys[1])) errors.push("hosted deployment journeys are not deterministic");
  }
  return { ok: errors.length === 0, errors };
}

async function runOne() {
  if (!nugetFeed) throw new Error("RUNIC_CURRENT_HOSTED_DEPLOYMENT_NUGET_FEED must name the exact local candidate feed.");
  const directory = await mkdtemp(join(tmpdir(), "runic-current-hosted-deployment-"));
  let registry;
  try {
    registry = await startRegistry();
    const versions = Object.fromEntries(registry.manifests.map((manifest) => [manifest.name, manifest.version]));
    versions.registry = registry.url;
    const fixture = await writeFixture(directory, versions);
    const environment = deploymentEnvironment(directory);
    const evidence = [];
    const restore = await run("dotnet", phases[0][1].slice(1), fixture.source, environment);
    evidence.push(phase("restore", phases[0][1], restore));
    requireSuccess("restore", restore);
    const publish = await run("dotnet", ["publish", "HostedDeployment.csproj", "--no-restore", "--configuration", "Release", "--output", join(fixture.ejected, "service"), "--nologo"], fixture.source, environment);
    evidence.push(phase("publish", phases[1][1], publish));
    requireSuccess("publish", publish);
    await writeFile(join(fixture.ejected, "service", "appsettings.json"), JSON.stringify(deploymentSettings(), null, 2) + "\n", "utf8");
    const frontendInstall = await run("npm", ["install", "--ignore-scripts", "--legacy-peer-deps"], fixture.frontend, environment);
    evidence.push(phase("frontend-install", phases[2][1], frontendInstall));
    requireSuccess("frontend-install", frontendInstall);
    const frontendCheck = await run("npm", ["run", "check"], fixture.frontend, environment);
    evidence.push(phase("frontend-check", phases[3][1], frontendCheck));
    requireSuccess("frontend-check", frontendCheck);
    const frontendBuild = await run("npm", ["run", "build"], fixture.frontend, environment);
    evidence.push(phase("frontend-build", phases[4][1], frontendBuild));
    requireSuccess("frontend-build", frontendBuild);
    await Promise.all([
      writeFile(join(fixture.ejected, "frontend", "package.json"), await readFile(join(fixture.frontend, "package.json"))),
      writeFile(join(fixture.ejected, "frontend", "package-lock.json"), await readFile(join(fixture.frontend, "package-lock.json"))),
      writeFile(join(fixture.ejected, "frontend", ".npmrc"), await readFile(join(fixture.frontend, ".npmrc"))),
    ]);
    const ejectedFrontend = join(fixture.ejected, "frontend");
    const ejectedInstall = await run("npm", ["install", "--omit=dev", "--ignore-scripts", "--legacy-peer-deps"], ejectedFrontend, environment);
    evidence.push(phase("ejected-frontend-install", phases[5][1], ejectedInstall));
    requireSuccess("ejected-frontend-install", ejectedInstall);

    const serviceDll = join(fixture.ejected, "service", "HostedDeployment.dll");
    const missingSecret = await run("dotnet", [serviceDll], join(fixture.ejected, "service"), deploymentEnvironment(directory, { [TOPOLOGY.secret.configurationKey]: "" }));
    evidence.push(phase("missing-secret", phases[6][1], missingSecret));
    requireFailure("missing-secret", missingSecret, "OidcClientSecret");
    const unsafeOrigin = await run("dotnet", [serviceDll], join(fixture.ejected, "service"), deploymentEnvironment(directory, { [TOPOLOGY.secret.configurationKey]: "fixture-secret", Runic__HostedDeployment__PublicOrigin: "http://app.example.test" }));
    evidence.push(phase("unsafe-public-origin", phases[7][1], unsafeOrigin));
    requireFailure("unsafe-public-origin", unsafeOrigin, "must be an HTTPS origin");

    const servicePort = await openPort();
    const service = start("dotnet", [serviceDll], join(fixture.ejected, "service"), deploymentEnvironment(directory, { [TOPOLOGY.secret.configurationKey]: "fixture-secret", ASPNETCORE_URLS: `http://127.0.0.1:${servicePort}` }));
    try {
      await waitFor(`http://127.0.0.1:${servicePort}/runic/health`, (body) => body.includes("healthy"));
      await waitFor(`http://127.0.0.1:${servicePort}/runic/ready`, (body) => body.includes("ready"));
    } finally { await stop(service); }
    evidence.push({ name: "service-health-readiness", argv: phases[8][1], status: "passed", exitCode: 0, reasonCode: null });

    const frontendPort = await openPort();
    const frontend = start("node", ["build/index.js"], ejectedFrontend, deploymentEnvironment(directory, { HOST: "127.0.0.1", PORT: String(frontendPort) }));
    try { await waitFor(`http://127.0.0.1:${frontendPort}/`, (body) => body.includes("Runic hosted deployment") && body.includes("/runic/service/session")); }
    catch (error) { throw new Error(`${error.message}\n${Buffer.concat(frontend.output).toString("utf8")}`); }
    finally { await stop(frontend); }
    evidence.push({ name: "frontend-ssr", argv: phases[9][1], status: "passed", exitCode: 0, reasonCode: null });

    const [deployment, settings] = await Promise.all([
      readFile(join(fixture.ejected, "deployment.json"), "utf8"),
      readFile(join(fixture.ejected, "service", "appsettings.json"), "utf8"),
    ]);
    if (deployment.includes("fixture-secret") || settings.includes("fixture-secret")) throw new Error("ejected deployment contains the injected OIDC secret.");
    if (!same(JSON.parse(deployment), { schema: TOPOLOGY.schema, topology: TOPOLOGY, topologySha256: TOPOLOGY_SHA256 }) || !same(JSON.parse(settings), deploymentSettings())) throw new Error("ejected configuration is not bound to the committed topology.");
    const receipt = {
      schema: RECEIPT_SCHEMA,
      topology: TOPOLOGY,
      topologySha256: TOPOLOGY_SHA256,
      isolation: { nugetGlobalPackagesFolder: ".nuget/packages", nugetHttpCachePath: ".nuget/http-cache", dotnetCliHome: ".dotnet", npmCache: ".npm-cache" },
      nugetCandidates: await nugetMetadata(fixture.source, directory),
      npmCandidates: await npmMetadata(fixture.frontend, registry),
      ejection: { service: "service", frontend: "frontend/build", staticAssetsOwner: "sveltekit", topologyBound: true, secretAbsent: true },
      phases: evidence,
    };
    const report = verifyReceipt(receipt);
    if (!report.ok) throw new Error(report.errors.join("\n"));
    return receipt;
  } finally {
    if (registry) registry.child.kill("SIGTERM");
    await rm(directory, { recursive: true, force: true });
  }
}

async function main() {
  const [command, path] = process.argv.slice(2);
  if (command === "run-twice" && !path) {
    const receipt = { schema: REPEAT_RECEIPT_SCHEMA, journeys: [await runOne(), await runOne()] };
    const report = verifyRepeatedReceipt(receipt);
    if (!report.ok) throw new Error(report.errors.join("\n"));
    process.stdout.write(JSON.stringify(receipt, null, 2) + "\n");
    return;
  }
  if (command === "verify-twice" && path) {
    const report = verifyRepeatedReceipt(JSON.parse(await readFile(path, "utf8")));
    if (!report.ok) throw new Error(report.errors.join("\n"));
    return;
  }
  throw new Error("Usage: node eng/current-hosted-deployment/verify.mjs <run-twice|verify-twice> [receipt.json]");
}

if (import.meta.main) main().catch((error) => { process.stderr.write(error.message + "\n"); process.exitCode = 1; });
