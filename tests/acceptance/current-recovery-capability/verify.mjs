#!/usr/bin/env node
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { cp, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";

const feed = process.env.RUNIC_W50_RECOVERY_NUGET_FEED && resolve(process.env.RUNIC_W50_RECOVERY_NUGET_FEED);
const editorDirectory = process.env.RUNIC_W50_RECOVERY_EDITOR_DIRECTORY && resolve(process.env.RUNIC_W50_RECOVERY_EDITOR_DIRECTORY);
const applicationVersion = process.env.RUNIC_W50_RECOVERY_APPLICATION_VERSION;
const schema = "runic.recovery-capability-consumer/1", repeatSchema = "runic.recovery-capability-consumer-repeat/1";
const candidates = ["Runic.Application", "Runic.Application.Testing", "Runic.Assets"];
const recovery = { modes: ["complete", "rollback"], blockedMutations: 2, staleSessionReplays: 2, diagnostics: "sanitized-counts" };
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const hash = async path => createHash("sha256").update(await readFile(path)).digest("hex");
const run = (command, args, cwd, env = {}) => new Promise(done => { const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ["ignore", "pipe", "pipe"] }); const output = []; child.stdout.on("data", value => output.push(value)); child.stderr.on("data", value => output.push(value)); child.on("error", error => done({ ok: false, exitCode: null, output: String(error) })); child.on("close", exitCode => done({ ok: exitCode === 0, exitCode, output: Buffer.concat(output).toString("utf8") })); });
const requireSuccess = (name, result) => { if (!result.ok) throw new Error(`${name} failed:\n${result.output.slice(-4096)}`); };

const project = version => `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="Runic.Application" Version="${version}" /><PackageReference Include="Runic.Application.Testing" Version="${version}" /></ItemGroup>
</Project>
`;

const program = `using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application;
using Runic.Application.Testing;

[assembly: RunicApplicationManifest("runic.recovery-capability-consumer", Version = "1.0.0", Provenance = "exact-local")]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationCapability("headless")]
[assembly: RunicApplicationCapability("unconfigured")]

var normalHost = new DeterministicApplicationTestHost(capabilities: [
    ApplicationCapabilityStatus.Available("headless"),
    ApplicationCapabilityStatus.Unavailable("desktop", "headless-consumer"),
]);
ApplicationCompositionManifest manifest;
await using (var normal = RunicApplication.CreateBuilder([]).UseHost(normalHost).Build())
{
    await normal.RunAsync();
    if (!normalHost.Lifecycle.SequenceEqual(["start", "wait", "stop"]) ||
        normal.Capabilities.GetRequired("headless").Availability != ApplicationCapabilityAvailability.Available ||
        normal.Capabilities.GetRequired("desktop").UnavailableReason != "headless-consumer" ||
        normal.Capabilities.GetRequired("unconfigured").UnavailableReason != "not-configured-by-headless-host") return 10;
    try { _ = normal.Capabilities.GetRequired("undeclared"); return 11; } catch (ArgumentException) { }
    if (!normal.Manifest.Capabilities.SequenceEqual(["desktop", "headless", "unconfigured"])) return 12;
    manifest = normal.Manifest;
}

var waitFaultHost = new DeterministicApplicationTestHost { WaitFailure = new InvalidOperationException("primary-wait"), StopFailure = new InvalidOperationException("cleanup-stop") };
await using (var waitFault = new ApplicationHost(manifest, [], waitFaultHost))
{
    try { await waitFault.RunAsync(); return 20; }
    catch (InvalidOperationException exception) when (exception.Message == "primary-wait")
    { if (!waitFaultHost.Lifecycle.SequenceEqual(["start", "wait", "stop"])) return 21; }
}

var startFaultHost = new DeterministicApplicationTestHost { StartFailure = new InvalidOperationException("primary-start"), StopFailure = new InvalidOperationException("cleanup-stop") };
await using (var startFault = new ApplicationHost(manifest, [], startFaultHost))
{
    try { await startFault.RunAsync(); return 30; }
    catch (InvalidOperationException exception) when (exception.Message == "primary-start")
    { if (!startFaultHost.Lifecycle.SequenceEqual(["start", "stop"])) return 31; }
}

var cancelledHost = new DeterministicApplicationTestHost(completeShutdownOnWait: false);
await using (var cancelled = new ApplicationHost(manifest, [], cancelledHost))
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { await cancelled.RunAsync(cancellation.Token); return 40; }
    catch (OperationCanceledException) { if (!cancelledHost.Lifecycle.SequenceEqual(["start", "wait", "stop"])) return 41; }
}

await using var noProvider = new ApplicationHost(manifest, [], new NonProviderHost());
if (noProvider.Capabilities.GetRequired("headless").UnavailableReason != "host-does-not-report-capabilities") return 50;
try { _ = new ApplicationHost(manifest, [], new MismatchedCapabilityHost()); return 51; }
catch (InvalidOperationException) { }

Console.WriteLine("{\\\"schema\\\":\\\"runic.recovery-capability-test-host/1\\\",\\\"capabilities\\\":[\\\"undeclared\\\",\\\"unconfigured\\\",\\\"mismatched\\\"],\\\"lifecycle\\\":[\\\"normal\\\",\\\"wait-primary\\\",\\\"start-primary\\\",\\\"cancellation\\\"]}");
return 0;

sealed class NonProviderHost : IApplicationHost
{
    private readonly DeterministicApplicationTestHost _inner = new();
    public ValueTask StartAsync(ApplicationCompositionManifest manifest, ReadOnlyMemory<string> arguments, CancellationToken cancellationToken) => _inner.StartAsync(manifest, arguments, cancellationToken);
    public ValueTask WaitForShutdownAsync(CancellationToken cancellationToken) => _inner.WaitForShutdownAsync(cancellationToken);
    public ValueTask StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

sealed class MismatchedCapabilityHost : IApplicationHost, IApplicationCapabilityProvider
{
    private readonly DeterministicApplicationTestHost _inner = new();
    public ApplicationCapabilityStatus GetCapabilityStatus(string capability) => ApplicationCapabilityStatus.Available("other-capability");
    public ValueTask StartAsync(ApplicationCompositionManifest manifest, ReadOnlyMemory<string> arguments, CancellationToken cancellationToken) => _inner.StartAsync(manifest, arguments, cancellationToken);
    public ValueTask WaitForShutdownAsync(CancellationToken cancellationToken) => _inner.WaitForShutdownAsync(cancellationToken);
    public ValueTask StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
`;

function nugetConfig(packages) { return `<configuration><packageSources><clear/><add key="candidate" value="${feed}"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="*"/></packageSource></packageSourceMapping><config><add key="globalPackagesFolder" value="${packages}"/></config></configuration>`; }
function phase(name, command, args, result) { return { name, argv: [command, ...args], status: result.ok ? "passed" : "failed", exitCode: result.exitCode }; }

async function currentProvenance() {
  if (!feed || !editorDirectory || !applicationVersion) throw new Error("RUNIC_W50_RECOVERY_NUGET_FEED, RUNIC_W50_RECOVERY_APPLICATION_VERSION, and RUNIC_W50_RECOVERY_EDITOR_DIRECTORY are required.");
  const packageFacts = await Promise.all(candidates.map(async identity => {
    const version = identity === "Runic.Assets" ? "0.1.0-preview.8d22423" : applicationVersion;
    const archive = join(feed, `${identity}.${version}.nupkg`);
    return { identity, version, archive: basename(archive), sha256: await hash(archive) };
  }));
  return { packages: packageFacts, editor: { binary: "RunicTranslations.Editor.dll", sha256: await hash(join(editorDirectory, "RunicTranslations.Editor.dll")) } };
}

export function verifyReceipt(receipt, provenance) {
  const errors = [];
  if (receipt?.schema !== repeatSchema || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push("two journeys required");
  for (const journey of receipt?.journeys ?? []) {
    if (journey?.schema !== schema || !same(journey?.isolation, { dotnetCliHome: ".dotnet", nugetPackages: ".nuget/packages", nugetHttpCache: ".nuget/http" }) || !same(journey?.projectReferences, [])) errors.push("consumer isolation mismatch");
    if (!same(journey?.capabilityGates, ["undeclared", "unconfigured", "mismatched"]) || !same(journey?.lifecycleOutcomes, ["normal", "wait-primary", "start-primary", "cancellation"])) errors.push("capability or lifecycle evidence mismatch");
    if (!same(journey?.recovery, recovery) || journey?.diagnostics?.schema !== "runic.translations.editor-diagnostics/1" || journey.diagnostics?.outboundTransportAttempts !== 0) errors.push("recovery diagnostics evidence mismatch");
    if (!Array.isArray(journey?.phases) || !same(journey.phases.map(item => item.name), ["restore", "build", "test-host", "editor-smoke", "editor-diagnostics"]) || journey.phases.some(item => item.status !== "passed" || item.exitCode !== 0)) errors.push("phase evidence mismatch");
    if (provenance && (!same(journey?.packages, provenance.packages) || !same(journey?.editor, provenance.editor))) errors.push("supplied artifact provenance mismatch");
    else if (!Array.isArray(journey?.packages) || journey.packages.length !== 3 || journey.packages.some(item => !/^[a-f0-9]{64}$/.test(item?.sha256 ?? "")) || !/^[a-f0-9]{64}$/.test(journey?.editor?.sha256 ?? "")) errors.push("artifact provenance malformed");
  }
  if (receipt?.journeys?.length === 2 && !same(receipt.journeys[0], receipt.journeys[1])) errors.push("journeys are not deterministic");
  return { ok: errors.length === 0, errors };
}

async function one(provenance) {
  const directory = await mkdtemp(join(tmpdir(), "runic-w50-recovery-capability-"));
  try {
    const env = { DOTNET_CLI_HOME: join(directory, ".dotnet"), NUGET_PACKAGES: join(directory, ".nuget", "packages"), NUGET_HTTP_CACHE_PATH: join(directory, ".nuget", "http") };
    await Promise.all([writeFile(join(directory, "NuGet.config"), nugetConfig(env.NUGET_PACKAGES)), writeFile(join(directory, "RecoveryCapability.csproj"), project(applicationVersion)), writeFile(join(directory, "Program.cs"), program)]);
    if ((await readFile(join(directory, "RecoveryCapability.csproj"), "utf8")).includes("ProjectReference")) throw new Error("consumer fixture must not reference product source");
    const phases = [];
    for (const [name, args] of [["restore", ["restore", "RecoveryCapability.csproj", "--configfile", "NuGet.config", "--no-cache", "--force-evaluate", "--nologo"]], ["build", ["build", "RecoveryCapability.csproj", "--no-restore", "--configuration", "Release", "--nologo"]], ["test-host", ["run", "--project", "RecoveryCapability.csproj", "--no-build", "--configuration", "Release"]]]) {
      const result = await run("dotnet", args, directory, env); phases.push(phase(name, "dotnet", args, result)); requireSuccess(name, result);
      if (name === "test-host" && !same(JSON.parse(result.output.trim()), { schema: "runic.recovery-capability-test-host/1", capabilities: ["undeclared", "unconfigured", "mismatched"], lifecycle: ["normal", "wait-primary", "start-primary", "cancellation"] })) throw new Error("test-host emitted unsafe or malformed evidence");
    }
    const editor = join(directory, "editor"); await cp(editorDirectory, editor, { recursive: true });
    const projects = await run("find", [editor, "-type", "f", "(", "-name", "*.csproj", "-o", "-name", "*.sln", ")"], directory, env); requireSuccess("published editor inspection", projects); if (projects.output.trim()) throw new Error("published Editor artifact included product project references");
    const smoke = await run("dotnet", [join(editor, "RunicTranslations.Editor.dll"), "--smoke-test", "--workspace", join(editor, "ExampleWorkspace")], directory, env); phases.push(phase("editor-smoke", "dotnet", ["RunicTranslations.Editor.dll", "--smoke-test"], smoke)); requireSuccess("packaged Editor recovery smoke", smoke);
    if (!smoke.output.includes("RECOVERY-EVIDENCE: complete=1 rollback=1 blocked=2 stale-session=2 diagnostics=sanitized-counts")) throw new Error("packaged Editor did not emit sanitized recovery evidence");
    const diagnostic = await run("dotnet", [join(editor, "RunicTranslations.Editor.dll"), "diagnostics", join(editor, "ExampleWorkspace"), "--runic-output", "json"], directory, env); phases.push(phase("editor-diagnostics", "dotnet", ["RunicTranslations.Editor.dll", "diagnostics"], diagnostic)); requireSuccess("packaged Editor diagnostics", diagnostic);
    const zip = JSON.parse(diagnostic.output).payload.Diagnostics.Path;
    const summary = await run("7z", ["x", "-so", zip, "diagnostics.json"], directory, env); requireSuccess("sanitized diagnostics summary", summary); await rm(zip, { force: true });
    if (summary.output.includes(editor) || summary.output.includes("product.de.json") || summary.output.includes("Speichern")) throw new Error("Editor diagnostics leaked workspace or translation text");
    const parsed = JSON.parse(summary.output); if (parsed.schema !== "runic.translations.editor-diagnostics/1" || typeof parsed.workspace?.documentCount !== "number") throw new Error("Editor diagnostics schema was not bounded");
    return { schema, isolation: { dotnetCliHome: ".dotnet", nugetPackages: ".nuget/packages", nugetHttpCache: ".nuget/http" }, projectReferences: [], packages: provenance.packages, editor: provenance.editor, capabilityGates: ["undeclared", "unconfigured", "mismatched"], lifecycleOutcomes: ["normal", "wait-primary", "start-primary", "cancellation"], recovery, diagnostics: { schema: parsed.schema, outboundTransportAttempts: 0 }, phases };
  } finally { await rm(directory, { recursive: true, force: true }); }
}

export async function runTwice() { const provenance = await currentProvenance(), receipt = { schema: repeatSchema, journeys: [await one(provenance), await one(provenance)] }; const report = verifyReceipt(receipt, provenance); if (!report.ok) throw new Error(report.errors.join("\n")); return receipt; }
if (import.meta.main) { const [command, path] = process.argv.slice(2); if (command === "run-twice") process.stdout.write(JSON.stringify(await runTwice(), null, 2) + "\n"); else if (command === "verify-twice" && path) { const report = verifyReceipt(JSON.parse(await readFile(path, "utf8")), await currentProvenance()); if (!report.ok) throw new Error(report.errors.join("\n")); } else throw new Error("Usage: verify.mjs run-twice | verify-twice <receipt>"); }
