#!/usr/bin/env node
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { lstat, mkdtemp, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, dirname, join, resolve } from "node:path";

const toolStaging = process.env.RUNIC_W60_TOOL_STAGING && resolve(process.env.RUNIC_W60_TOOL_STAGING);
const prerequisiteFeed = process.env.RUNIC_W60_TOOL_PREREQUISITE_FEED && resolve(process.env.RUNIC_W60_TOOL_PREREQUISITE_FEED);
const toolkitRoot = process.env.RUNIC_W60_TOOLKIT_ROOT && resolve(process.env.RUNIC_W60_TOOLKIT_ROOT);
const authority = process.env.RUNIC_W60_AUTHORITY_MANIFEST && resolve(process.env.RUNIC_W60_AUTHORITY_MANIFEST);
const candidateSet = process.env.RUNIC_W60_EDITOR_CANDIDATE_SET && resolve(process.env.RUNIC_W60_EDITOR_CANDIDATE_SET);
const schema = "runic.unsigned-tool-staging-consumer/1";
const repeatSchema = "runic.unsigned-tool-staging-consumer-repeat/1";
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const hash = async (path) => createHash("sha256").update(await readFile(path)).digest("hex");

const run = (command, args, cwd, env = {}) => new Promise((done) => {
  const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, stdio: ["ignore", "pipe", "pipe"] });
  const output = [];
  child.stdout.on("data", (value) => output.push(value)); child.stderr.on("data", (value) => output.push(value));
  child.on("error", (error) => done({ exitCode: null, output: String(error) }));
  child.on("close", (exitCode) => done({ exitCode, output: Buffer.concat(output).toString("utf8") }));
});
const requireSuccess = (name, result) => { if (result.exitCode !== 0) throw new Error(`${name} failed:\n${result.output.slice(-4096)}`); };
const regular = async (path, description) => { const entry = await lstat(path); if (!entry.isFile() || entry.isSymbolicLink()) throw new Error(`${description} must be a regular file`); };

async function linkedCandidate(cwd) {
  if (!authority || !candidateSet) throw new Error("RUNIC_W60_AUTHORITY_MANIFEST and RUNIC_W60_EDITOR_CANDIDATE_SET are required.");
  const result = await run("node", [join(dirname(authority), "eng", "link-unsigned-candidate-set.mjs"), "run-twice", authority, candidateSet], cwd);
  requireSuccess("candidate-set linker", result);
  const repeated = JSON.parse(result.output);
  if (repeated?.schema !== "runic.unsigned-candidate-set-repeat/1" || !same(repeated?.journeys?.[0], repeated?.journeys?.[1])) throw new Error("candidate set did not deterministically link current local inputs");
  return repeated.journeys[0];
}

async function stagedTool() {
  if (!toolStaging || !prerequisiteFeed || !toolkitRoot) throw new Error("RUNIC_W60_TOOL_STAGING, RUNIC_W60_TOOL_PREREQUISITE_FEED, and RUNIC_W60_TOOLKIT_ROOT are required.");
  const recordPath = join(toolStaging, "dotnet-runic-unsigned-staging.json");
  await regular(recordPath, "tool staging record");
  const record = JSON.parse(await readFile(recordPath, "utf8"));
  const archive = join(toolStaging, record?.package?.archive ?? "");
  await regular(archive, "staged dotnet-runic package");
  const stageEntries = (await readdir(toolStaging)).sort();
  const sourceProject = join(toolkitRoot, "tools/dotnet-runic-toolkit/RunicToolkit.DotNet.RunicToolkit.csproj");
  const script = join(toolkitRoot, "eng/stage-dotnet-runic-unsigned.ps1");
  const revision = (await run("git", ["rev-parse", "HEAD"], toolkitRoot)).output.trim();
  const tree = (await run("git", ["rev-parse", "HEAD^{tree}"], toolkitRoot)).output.trim();
  const sourceStatus = await run("git", ["status", "--porcelain"], toolkitRoot);
  requireSuccess("toolkit source status", sourceStatus);
  if (sourceStatus.output !== "") throw new Error("toolkit source worktree must be clean");
  const feed = await Promise.all((await readdir(prerequisiteFeed)).sort((left, right) => left.toLowerCase().localeCompare(right.toLowerCase())).map(async (name) => { const path = join(prerequisiteFeed, name); await regular(path, "local prerequisite package"); return { archive: name, sha256: await hash(path) }; }));
  const expectedTopLevel = ["canonicalReleaseApproval", "package", "prerequisiteFeed", "producer", "publication", "schema", "source", "supportEnvelopeContent"];
  const expectedStageEntries = ["dotnet-runic-unsigned-staging.json", record?.package?.archive].sort();
  if (!same(Object.keys(record).sort(), expectedTopLevel) || !same(stageEntries, expectedStageEntries) || record.schema !== "runic.dotnet-runic-unsigned-staging/1" || record.publication !== "forbidden" || record.canonicalReleaseApproval !== "seven-package-release-gate-required" || record.supportEnvelopeContent !== "forbidden") throw new Error("unsigned tool staging contract is not closed");
  if (!same(record.producer, { operation: "direct-dotnet-pack", script: "eng/stage-dotnet-runic-unsigned.ps1", scriptSha256: await hash(script), project: "tools/dotnet-runic-toolkit/RunicToolkit.DotNet.RunicToolkit.csproj", fullPackInvoked: false, sourceProjectReferences: [] }) || (await readFile(sourceProject, "utf8")).includes("ProjectReference")) throw new Error("tool staging does not prove the direct source-project boundary");
  if (!same(record.source, { repository: "https://github.com/Runic-Artifex/runic-toolkit", revision, tree }) || !same(record.prerequisiteFeed, { packages: feed, remoteSources: [] }) || record.package?.archive !== `dotnet-runic.${record.package?.metadata?.version}.nupkg` || record.package?.sha256 !== await hash(archive) || record.package?.metadata?.id !== "dotnet-runic" || record.package?.metadata?.toolCommandName !== "dotnet-runic" || record.package?.metadata?.repository?.type !== "git" || record.package?.metadata?.repository?.url !== record.source.repository || record.package?.metadata?.repository?.commit !== revision || !/^[a-f0-9]{64}$/.test(record.package?.metadata?.nuspecSha256 ?? "") || !same(record.package?.metadata?.dependencies, [])) throw new Error("tool package identity, metadata, or provenance drifted");
  return record;
}

export function verifyReceipt(receipt, expected) {
  const errors = [];
  if (receipt?.schema !== repeatSchema || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push("two unsigned-tool journeys required");
  for (const journey of receipt?.journeys ?? []) {
    if (!same(Object.keys(journey ?? {}).sort(), ["candidateSet", "canonicalReleaseApproval", "command", "isolation", "projectReferences", "remoteSources", "schema", "supportEnvelopeContent", "toolStaging"].sort()) || journey?.schema !== schema || !same(journey?.isolation, { dotnetCliHome: ".dotnet", nugetPackages: ".nuget/packages", nugetHttpCache: ".nuget/http" }) || !same(journey?.projectReferences, []) || !same(journey?.remoteSources, []) || journey?.supportEnvelopeContent !== "forbidden" || journey?.canonicalReleaseApproval !== "seven-package-release-gate-required" || journey?.command?.output !== "dotnet-runic") errors.push("unsigned-tool consumer boundary mismatch");
    if (expected && (!same(journey?.toolStaging, expected.toolStaging) || !same(journey?.candidateSet, expected.candidateSet))) errors.push("supplied local provenance differs from receipt");
  }
  if (receipt?.journeys?.length === 2 && !same(receipt.journeys[0], receipt.journeys[1])) errors.push("unsigned-tool journeys are not deterministic");
  return { ok: errors.length === 0, errors };
}

async function one(inputs) {
  const directory = await mkdtemp(join(tmpdir(), "runic-w60-unsigned-tool-"));
  try {
    const feed = join(directory, "feed"), tool = join(directory, "tool"), config = join(directory, "NuGet.config");
    await writeFile(config, `<configuration><packageSources><clear/><add key="candidate" value="${feed}"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="dotnet-runic"/></packageSource></packageSourceMapping></configuration>`);
    await (await import("node:fs/promises")).cp(toolStaging, feed, { recursive: true, filter: (source) => basename(source) !== "dotnet-runic-unsigned-staging.json" });
    const env = { DOTNET_CLI_HOME: join(directory, ".dotnet"), NUGET_PACKAGES: join(directory, ".nuget/packages"), NUGET_HTTP_CACHE_PATH: join(directory, ".nuget/http") };
    requireSuccess("local tool install", await run("dotnet", ["tool", "install", "dotnet-runic", "--tool-path", tool, "--version", inputs.tool.package.metadata.version, "--configfile", config, "--ignore-failed-sources"], directory, env));
    const command = await run(join(tool, "dotnet-runic"), ["--version"], directory, env); requireSuccess("staged dotnet-runic", command);
    if (command.output.trim() !== "dotnet-runic") throw new Error("staged tool identity drifted");
    return { schema, isolation: { dotnetCliHome: ".dotnet", nugetPackages: ".nuget/packages", nugetHttpCache: ".nuget/http" }, projectReferences: [], remoteSources: [], supportEnvelopeContent: "forbidden", canonicalReleaseApproval: "seven-package-release-gate-required", toolStaging: inputs.tool, candidateSet: inputs.candidate, command: { output: command.output.trim() } };
  } finally { await rm(directory, { recursive: true, force: true }); }
}

export async function runTwice() { const tool = await stagedTool(), candidate = await linkedCandidate(process.cwd()), first = await one({ tool, candidate }), second = await one({ tool, candidate }); const receipt = { schema: repeatSchema, journeys: [first, second] }; const report = verifyReceipt(receipt, { toolStaging: tool, candidateSet: candidate }); if (!report.ok) throw new Error(report.errors.join("\n")); return receipt; }
if (import.meta.main) { const [command, path] = process.argv.slice(2); if (command === "run-twice") process.stdout.write(`${JSON.stringify(await runTwice(), null, 2)}\n`); else if (command === "verify-twice" && path) { const expected = { toolStaging: await stagedTool(), candidateSet: await linkedCandidate(process.cwd()) }, report = verifyReceipt(JSON.parse(await readFile(path, "utf8")), expected); if (!report.ok) throw new Error(report.errors.join("\n")); } else throw new Error("Usage: verify.mjs run-twice | verify-twice <receipt>"); }
