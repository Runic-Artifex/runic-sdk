#!/usr/bin/env node
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";

const feed = process.env.RUNIC_W50_NATIVE_SHELL_CSWEBUI_FEED && resolve(process.env.RUNIC_W50_NATIVE_SHELL_CSWEBUI_FEED);
const version = process.env.RUNIC_W50_NATIVE_SHELL_CSWEBUI_VERSION;
const editorDirectory = process.env.RUNIC_W50_NATIVE_SHELL_EDITOR_DIRECTORY && resolve(process.env.RUNIC_W50_NATIVE_SHELL_EDITOR_DIRECTORY);
const editorArchive = process.env.RUNIC_W50_NATIVE_SHELL_EDITOR_ARCHIVE && resolve(process.env.RUNIC_W50_NATIVE_SHELL_EDITOR_ARCHIVE);
const schema = "runic.native-shell-consumer/1", repeatSchema = "runic.native-shell-consumer-repeat/1";
const packages = ["CsWebUi", "CsWebUi.Native"];
const fingerprint = "d7919f3d2ba1ec4af48bac5892dd25667f323d6341de212ae69c83b086224faf";
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const hash = async path => createHash("sha256").update(await readFile(path)).digest("hex");
const run = (command, args, cwd, env = {}) => new Promise(done => {
  const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ["ignore", "pipe", "pipe"] });
  const output = [];
  child.stdout.on("data", value => output.push(value)); child.stderr.on("data", value => output.push(value));
  child.on("error", error => done({ ok: false, exitCode: null, output: String(error) }));
  child.on("close", exitCode => done({ ok: exitCode === 0, exitCode, output: Buffer.concat(output).toString("utf8") }));
});
const requireSuccess = (name, result) => { if (!result.ok) throw new Error(`${name} failed:\n${result.output.slice(-4096)}`); };
const phase = (name, result, status = result.ok ? "passed" : "failed") => ({ name, status, exitCode: result.exitCode });

const project = version => `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="CsWebUi" Version="${version}" /></ItemGroup>
</Project>
`;
const program = `using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using CsWebUi;
using CsWebUi.Native;

var evidence = new {
    schema = "runic.cswebui-managed-capability/1",
    runtime = new { framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString() },
    capabilities = new { freePort = WebUiApplication.GetFreePort() != 0, privateFileHandlerStreaming = WebUiNativeLibrary.SupportsPrivateFileHandlerStreaming, webViewAvailable = WebUiApplication.BrowserExists(CsWebUi.WebUiBrowser.WebView) }
};
if (!evidence.capabilities.freePort) return 10;
Console.WriteLine(JsonSerializer.Serialize(evidence));
return 0;
`;
function nugetConfig(packagesDirectory) { return `<configuration><packageSources><clear/><add key="candidate" value="${feed}"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="CsWebUi"/><package pattern="CsWebUi.Native"/></packageSource></packageSourceMapping><config><add key="globalPackagesFolder" value="${packagesDirectory}"/></config></configuration>`; }

async function provenance() {
  if (!feed || !version || !editorDirectory || !editorArchive) throw new Error("RUNIC_W50_NATIVE_SHELL_CSWEBUI_FEED, RUNIC_W50_NATIVE_SHELL_CSWEBUI_VERSION, RUNIC_W50_NATIVE_SHELL_EDITOR_DIRECTORY, and RUNIC_W50_NATIVE_SHELL_EDITOR_ARCHIVE are required.");
  const packed = await Promise.all(packages.map(async identity => {
    const archive = join(feed, `${identity}.${version}.nupkg`);
    return { identity, version, archive: basename(archive), sha256: await hash(archive) };
  }));
  return { packages: packed, editor: { archive: basename(editorArchive), archiveSha256: await hash(editorArchive), binary: "RunicTranslations.Editor.dll", binarySha256: await hash(join(editorDirectory, "RunicTranslations.Editor.dll")), nativeLibrary: "libwebui-2.so", nativeLibrarySha256: await hash(join(editorDirectory, "libwebui-2.so")) } };
}

function expectedDetails(webViewAvailable, highContrast) { return {
  allowedOrigin: "exact-loopback-origin", bridge: "generated-bridge-attached", cleanup: "closed-disposed-cleaned", contractFingerprint: fingerprint,
  highContrast, highContrastPropagated: "true", listener: "private-loopback", loopbackAssetRequests: "0", outboundTransportAttempts: "0", privateFileHandlerStreaming: "false",
  protocolIdentity: "runic.translations.editor", protocolVersion: "1", schema: "runic.translations.editor-native-shell/1",
  webViewCapability: webViewAvailable ? "available" : "webview-prerequisite-missing"
}; }

export function verifyReceipt(receipt, supplied) {
  const errors = [];
  if (receipt?.schema !== repeatSchema || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push("two native-shell journeys required");
  for (const journey of receipt?.journeys ?? []) {
    if (journey?.schema !== schema || !same(journey?.isolation, { dotnetCliHome: ".dotnet", nugetPackages: ".nuget/packages", nugetHttpCache: ".nuget/http" }) || !same(journey?.projectReferences, [])) errors.push("consumer isolation or source-reference evidence mismatch");
    if (!Array.isArray(journey?.phases) || !same(journey.phases.map(item => item.name), ["restore", "build", "managed-capability", "editor-native-shell"]) || journey.phases.slice(0, 3).some(item => item.status !== "passed" || item.exitCode !== 0) || journey.phases[3]?.status !== "expected-unavailable" || journey.phases[3]?.exitCode !== 2) errors.push("phase evidence mismatch");
    const managed = journey?.managed;
    if (managed?.schema !== "runic.cswebui-managed-capability/1" || managed?.capabilities?.freePort !== true || managed?.capabilities?.privateFileHandlerStreaming !== false || typeof managed?.capabilities?.webViewAvailable !== "boolean" || typeof managed?.runtime?.framework !== "string" || typeof managed?.runtime?.os !== "string" || typeof managed?.runtime?.architecture !== "string") errors.push("managed capability facts mismatch");
    const details = journey?.nativeShell?.details;
    if (journey?.nativeShell?.faultCode !== "REDIT0008" || journey?.nativeShell?.capability !== "private-file-handler-streaming-unavailable" || journey?.nativeShell?.retryable !== false || !same(details, expectedDetails(managed?.capabilities?.webViewAvailable, details?.highContrast))) errors.push("native shell failed-closed evidence mismatch");
    if (details?.highContrast !== "true" && details?.highContrast !== "false") errors.push("high-contrast fact malformed");
    if (supplied && (!same(journey?.packages, supplied.packages) || !same(journey?.editor, supplied.editor))) errors.push("artifact provenance mismatch");
    else if (!Array.isArray(journey?.packages) || journey.packages.length !== 2 || journey.packages.some(item => !/^[a-f0-9]{64}$/.test(item?.sha256 ?? "")) || !/^[a-f0-9]{64}$/.test(journey?.editor?.archiveSha256 ?? "") || !/^[a-f0-9]{64}$/.test(journey?.editor?.binarySha256 ?? "") || !/^[a-f0-9]{64}$/.test(journey?.editor?.nativeLibrarySha256 ?? "")) errors.push("artifact provenance malformed");
  }
  if (receipt?.journeys?.length === 2 && !same(receipt.journeys[0], receipt.journeys[1])) errors.push("native-shell journeys are not deterministic");
  return { ok: errors.length === 0, errors };
}

async function one(supplied) {
  const directory = await mkdtemp(join(tmpdir(), "runic-w50-native-shell-"));
  try {
    const env = { DOTNET_CLI_HOME: join(directory, ".dotnet"), NUGET_PACKAGES: join(directory, ".nuget", "packages"), NUGET_HTTP_CACHE_PATH: join(directory, ".nuget", "http") };
    await Promise.all([writeFile(join(directory, "NuGet.config"), nugetConfig(env.NUGET_PACKAGES)), writeFile(join(directory, "NativeShell.csproj"), project(version)), writeFile(join(directory, "Program.cs"), program)]);
    if ((await readFile(join(directory, "NativeShell.csproj"), "utf8")).includes("ProjectReference")) throw new Error("consumer fixture must not reference product source");
    const phases = [];
    for (const [name, args] of [["restore", ["restore", "NativeShell.csproj", "--configfile", "NuGet.config", "--no-cache", "--force-evaluate", "--nologo"]], ["build", ["build", "NativeShell.csproj", "--no-restore", "--configuration", "Release", "--nologo"]]]) {
      const result = await run("dotnet", args, directory, env); phases.push(phase(name, result)); requireSuccess(name, result);
    }
    const extracted = await run("tar", ["-xzf", editorArchive, "-C", directory], directory, env); requireSuccess("Editor archive extraction", extracted);
    const editor = join(directory, "RunicTranslations.Editor");
    if (await hash(join(editor, "RunicTranslations.Editor.dll")) !== supplied.editor.binarySha256 || await hash(join(editor, "libwebui-2.so")) !== supplied.editor.nativeLibrarySha256) throw new Error("Editor archive content does not match supplied binary provenance");
    const sourceFiles = await run("find", [editor, "-type", "f", "(", "-name", "*.csproj", "-o", "-name", "*.sln", ")"], directory, env); requireSuccess("published Editor inspection", sourceFiles); if (sourceFiles.output.trim()) throw new Error("published Editor artifact included product project references");
    const managedResult = await run("dotnet", ["run", "--project", "NativeShell.csproj", "--no-build", "--configuration", "Release"], directory, { ...env, CSWEBUI_NATIVE_LIBRARY: join(editor, "libwebui-2.so") }); phases.push(phase("managed-capability", managedResult)); requireSuccess("managed capability smoke", managedResult);
    const managed = JSON.parse(managedResult.output.trim());
    const nativeResult = await run("dotnet", [join(editor, "RunicTranslations.Editor.dll"), "edit", "--native-shell-canary", "--workspace", join(editor, "ExampleWorkspace"), "--runic-output", "json"], directory, { ...env, CSWEBUI_NATIVE_LIBRARY: join(editor, "libwebui-2.so") });
    phases.push(phase("editor-native-shell", nativeResult, nativeResult.exitCode === 2 ? "expected-unavailable" : "failed"));
    if (nativeResult.exitCode !== 2) throw new Error(`native shell did not fail closed:\n${nativeResult.output.slice(-4096)}`);
    const response = JSON.parse(nativeResult.output.trim());
    if (response?.fault?.code !== "REDIT0008" || response?.fault?.message !== "Native shell capability unavailable: private-file-handler-streaming-unavailable.") throw new Error("native shell did not report the bounded streaming diagnostic");
    return { schema, isolation: { dotnetCliHome: ".dotnet", nugetPackages: ".nuget/packages", nugetHttpCache: ".nuget/http" }, projectReferences: [], packages: supplied.packages, editor: supplied.editor, managed, nativeShell: { faultCode: response.fault.code, capability: "private-file-handler-streaming-unavailable", retryable: response.fault.retryable, details: response.fault.details }, phases };
  } finally { await rm(directory, { recursive: true, force: true }); }
}

export async function runTwice() { const supplied = await provenance(), receipt = { schema: repeatSchema, journeys: [await one(supplied), await one(supplied)] }; const report = verifyReceipt(receipt, supplied); if (!report.ok) throw new Error(report.errors.join("\n")); return receipt; }
if (import.meta.main) { const [command, path] = process.argv.slice(2); if (command === "run-twice") process.stdout.write(JSON.stringify(await runTwice(), null, 2) + "\n"); else if (command === "verify-twice" && path) { const report = verifyReceipt(JSON.parse(await readFile(path, "utf8")), await provenance()); if (!report.ok) throw new Error(report.errors.join("\n")); } else throw new Error("Usage: verify.mjs run-twice | verify-twice <receipt>"); }
