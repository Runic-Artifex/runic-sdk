#!/usr/bin/env node
import { createHash } from "node:crypto";
import { access, cp, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import { tmpdir } from "node:os";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const toolPackage = process.env.RUNIC_W50_TOOL_PACKAGE && resolve(process.env.RUNIC_W50_TOOL_PACKAGE);
const editorDirectory = process.env.RUNIC_W50_EDITOR_DIRECTORY && resolve(process.env.RUNIC_W50_EDITOR_DIRECTORY);
const schema = "runic.support-envelope-consumer/1", repeatSchema = "runic.support-envelope-consumer-repeat/1";
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const hash = async path => createHash("sha256").update(await readFile(path)).digest("hex");
const exists = path => access(path).then(() => true, () => false);
const run = (command, args, cwd, env) => new Promise(resolveResult => { const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ["ignore", "pipe", "pipe"] }); let output = ""; child.stdout.on("data", value => output += value); child.stderr.on("data", value => output += value); child.on("close", exitCode => resolveResult({ exitCode, output })); child.on("error", error => resolveResult({ exitCode: 1, output: String(error) })); });
const requireSuccess = (name, result) => { if (result.exitCode !== 0) throw new Error(`${name} failed: ${result.output}`); };
const hostileCases = ["workspace-root", "relative-path", "token", "source-text", "translation-text", "review-text"];

function crc32(bytes) {
  let value = 0xffffffff;
  for (const byte of bytes) { value ^= byte; for (let bit = 0; bit < 8; bit += 1) value = (value >>> 1) ^ (value & 1 ? 0xedb88320 : 0); }
  return (value ^ 0xffffffff) >>> 0;
}

function diagnosticZip(summary) {
  const name = Buffer.from("diagnostics.json"), data = Buffer.from(JSON.stringify(summary)), crc = crc32(data), local = Buffer.alloc(30), central = Buffer.alloc(46), end = Buffer.alloc(22);
  local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt32LE(crc, 14); local.writeUInt32LE(data.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(name.length, 26);
  central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt32LE(crc, 16); central.writeUInt32LE(data.length, 20); central.writeUInt32LE(data.length, 24); central.writeUInt16LE(name.length, 28); central.writeUInt32LE(0, 42);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(1, 8); end.writeUInt16LE(1, 10); end.writeUInt32LE(central.length + name.length, 12); end.writeUInt32LE(local.length + name.length + data.length, 16);
  return Buffer.concat([local, name, data, central, name, end]);
}

const hostileSummary = (kind) => {
  const result = { schema: "runic.translations.editor-diagnostics/1", generatedAt: "2026-08-27T00:00:00+00:00", application: { product: "Runic Editor", version: "1", updateChannel: "preview", commit: null, runtime: "net", runtimeIdentifier: "linux-x64", operatingSystem: "Linux", architecture: "X64" }, workspace: { catalogId: null, schemaVersion: null, localeCount: 0, documentCount: 0, messageCount: 0, compilerSuccess: true, reviewStateAvailable: true, pendingTransaction: false, pendingTransactionPathCount: 0, diagnostics: [] } };
  if (kind === "workspace-root") result.application.product = "/workspace/root";
  else if (kind === "relative-path") result.application.product = "../workspace";
  else if (kind === "token") result.application.product = "access-token";
  else result.workspace[kind.replace("-text", "")] = "private content";
  return result;
};

export function verifyReceipt(receipt, provenance) {
  const errors = [];
  if (receipt?.schema !== repeatSchema || !Array.isArray(receipt.journeys) || receipt.journeys.length !== 2) errors.push("two journeys required");
  for (const journey of receipt?.journeys ?? []) {
    if (journey?.schema !== schema || journey?.tool?.archive !== "dotnet-runic.0.2.0-preview.w50001.nupkg" || !/^[a-f0-9]{64}$/.test(journey?.tool?.sha256 ?? "") || journey?.editor?.binary !== "RunicTranslations.Editor.dll" || !/^[a-f0-9]{64}$/.test(journey?.editor?.sha256 ?? "")) errors.push("artifact provenance mismatch");
    if (provenance && (!same(journey?.tool, provenance.tool) || !same(journey?.editor, provenance.editor))) errors.push("supplied artifact hash mismatch");
    if (!journey?.isolatedCaches || !journey?.noProductProjectReference || !journey?.previewListsSelectionAndOmissions || !journey?.collectByteIdentical || !journey?.removed || !journey?.hostileRejected || !same(journey?.hostileRejections, hostileCases) || journey?.outboundTransportAttempts !== 0) errors.push("support evidence mismatch");
  }
  if (receipt?.journeys?.length === 2 && !same(receipt.journeys[0], receipt.journeys[1])) errors.push("journeys are not deterministic");
  return { ok: errors.length === 0, errors };
}

async function currentProvenance() {
  if (!toolPackage || !editorDirectory) throw new Error("RUNIC_W50_TOOL_PACKAGE and RUNIC_W50_EDITOR_DIRECTORY are required.");
  return { tool: { archive: basename(toolPackage), sha256: await hash(toolPackage) }, editor: { binary: "RunicTranslations.Editor.dll", sha256: await hash(join(editorDirectory, "RunicTranslations.Editor.dll")) } };
}

async function one(provenance) {
  if (!toolPackage || !editorDirectory) throw new Error("RUNIC_W50_TOOL_PACKAGE and RUNIC_W50_EDITOR_DIRECTORY are required.");
  const directory = await mkdtemp(join(tmpdir(), "runic-w50-support-consumer-"));
  try {
    const feed = join(directory, "feed"), editor = join(directory, "editor"), tool = join(directory, "tool"), envelopeA = join(directory, "a.json"), envelopeB = join(directory, "b.json");
    await cp(editorDirectory, editor, { recursive: true });
    await cp(dirname(toolPackage), feed, { recursive: true });
    await writeFile(join(directory, "NuGet.config"), `<configuration><packageSources><clear/><add key="candidate" value="${feed}"/></packageSources></configuration>`);
    const env = { DOTNET_CLI_HOME: join(directory, ".dotnet"), NUGET_PACKAGES: join(directory, ".nuget/packages"), NUGET_HTTP_CACHE_PATH: join(directory, ".nuget/http") };
    const projects = await run("find", [editor, "-type", "f", "(", "-name", "*.csproj", "-o", "-name", "*.sln", ")"], directory, env); requireSuccess("published Editor inspection", projects);
    if (projects.output.trim()) throw new Error("published Editor artifact included a product project reference");
    requireSuccess("tool install", await run("dotnet", ["tool", "install", "dotnet-runic", "--tool-path", tool, "--version", "0.2.0-preview.w50001", "--configfile", join(directory, "NuGet.config"), "--ignore-failed-sources"], directory, env));
    const produced = await run(join(editor, "RunicTranslations.Editor"), ["diagnostics", join(editor, "ExampleWorkspace"), "--runic-output", "json"], directory, env); requireSuccess("editor diagnostics", produced);
    const zip = JSON.parse(produced.output).payload.Diagnostics.Path;
    const preview = await run(join(tool, "dotnet-runic"), ["support", "--mode", "preview", "--editor-diagnostics", zip], directory, env); requireSuccess("preview", preview);
    if (!preview.output.includes("runic.translations.editor-diagnostics") || !preview.output.includes("sessions-cookies-tokens") || !preview.output.includes("outbound transport attempts: 0")) throw new Error("preview omitted required local evidence");
    requireSuccess("collect a", await run(join(tool, "dotnet-runic"), ["support", "--mode", "collect", "--editor-diagnostics", zip, "--destination", envelopeA], directory, env));
    requireSuccess("collect b", await run(join(tool, "dotnet-runic"), ["support", "--mode", "collect", "--editor-diagnostics", zip, "--destination", envelopeB], directory, env));
    if (!same(await readFile(envelopeA, "utf8"), await readFile(envelopeB, "utf8"))) throw new Error("collect was not byte-identical");
    requireSuccess("remove a", await run(join(tool, "dotnet-runic"), ["support", "--mode", "remove", "--destination", envelopeA], directory, env));
    requireSuccess("remove b", await run(join(tool, "dotnet-runic"), ["support", "--mode", "remove", "--destination", envelopeB], directory, env));
    if (await exists(envelopeA) || await exists(envelopeB)) throw new Error("remove did not delete the verified envelope");
    for (const kind of hostileCases) {
      const hostile = join(directory, `${kind}.zip`); await writeFile(hostile, diagnosticZip(hostileSummary(kind)));
      if ((await run(join(tool, "dotnet-runic"), ["support", "--mode", "preview", "--editor-diagnostics", hostile], directory, env)).exitCode === 0) throw new Error(`hostile ${kind} source was accepted`);
    }
    return { schema, ...provenance, isolatedCaches: true, noProductProjectReference: true, previewListsSelectionAndOmissions: true, collectByteIdentical: true, removed: true, hostileRejected: true, hostileRejections: hostileCases, outboundTransportAttempts: 0 };
  } finally { await rm(directory, { recursive: true, force: true }); }
}

export async function runTwice() { const provenance = await currentProvenance(), receipt = { schema: repeatSchema, journeys: [await one(provenance), await one(provenance)] }; const report = verifyReceipt(receipt, provenance); if (!report.ok) throw new Error(report.errors.join("\n")); return receipt; }
if (import.meta.main) { const [command, path] = process.argv.slice(2); if (command === "run-twice") process.stdout.write(JSON.stringify(await runTwice(), null, 2) + "\n"); else if (command === "verify-twice" && path) { const report = verifyReceipt(JSON.parse(await readFile(path)), await currentProvenance()); if (!report.ok) throw new Error(report.errors.join("\n")); } else throw new Error("Usage: verify.mjs run-twice | verify-twice <receipt>"); }
