import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readdirSync, writeFileSync, readFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const root = resolve(fileURLToPath(new URL("../..", import.meta.url)));
const cli = resolve(root, `tools/dotnet-runic/bin/${process.env.CONFIGURATION ?? "Debug"}/net10.0/dotnet-runic.dll`);
const rid = `${({ linux: "linux", darwin: "osx", win32: "win" })[process.platform]}-${process.arch}`;

// Invokes the built CLI (bun run build) and publishes a temporary probe project.
test("size preserves failed verification, publish failures and existing evidence", { timeout: 180000 }, () => {
  assert.ok(existsSync(cli), `Build tools/dotnet-runic first: ${cli} is missing.`);
  const directory = mkdtempSync(join(tmpdir(), "runic-size-acceptance-"));
  try {
    const project = join(directory, "Probe.csproj");
    const code = join(directory, "Program.cs");
    const checker = join(directory, "check.mjs");
    const marker = join(directory, "checked");
    writeFileSync(project, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>');
    writeFileSync(code, 'System.Console.WriteLine("size probe");');
    writeFileSync(checker, `import {writeFileSync} from "node:fs"; writeFileSync(${JSON.stringify(marker)}, "ran"); process.exitCode = 7;`);
    const report = join(directory, "failed-check.json");
    const invoke = (path, extra = []) => {
      const result = spawnSync(process.env.DOTNET_HOST_PATH ?? "dotnet", [cli, "size", "--project", project,
        "--runtime", rid, "--no-aot", "--report", path, "--verify", process.execPath, "--verify-argument", checker, ...extra],
        { cwd: directory, encoding: "utf8", timeout: 120000, maxBuffer: 8 * 1024 * 1024 });
      if (result.error) throw result.error;
      assert.equal(result.signal, null, result.stderr);
      return result;
    };
    const failed = invoke(report);
    assert.equal(failed.status, 1, failed.stdout + failed.stderr);
    const original = readFileSync(report, "utf8");
    const measured = JSON.parse(original);
    assert.equal(measured.schema, "runic.size/2");
    assert.match(measured.createdAt, /Z$/);
    assert.ok(Array.isArray(measured.installedRuntimes) && measured.installedRuntimes.every(runtime => runtime.name && "version" in runtime));
    assert.ok(existsSync(measured.publishLogPath));
    assert.equal(measured.publishExitCode, 0);
    assert.equal(measured.verification.status, "failed");
    assert.equal(measured.verification.exitCode, 7);
    assert.ok(existsSync(marker));
    assert.ok(existsSync(measured.archivePath));
    assert.equal(measured.totalBytes, measured.files.reduce((sum, file) => sum + file.bytes, 0));
    assert.ok(measured.compressedBytes > 0);
    assert.ok(measured.buildOperatingSystem);
    assert.equal(invoke(report).status, 2, "Existing reports must be rejected");
    // A failed verification still reports its runic.application.tool.size/1 payload, in fault.data.
    const jsonReport = join(directory, "json-report.json");
    const json = invoke(jsonReport, ["--format", "json"]);
    assert.equal(json.status, 1, json.stderr);
    const envelope = JSON.parse(json.stdout);
    assert.equal(envelope.success, false);
    assert.equal(envelope.fault.code, "RAPPCLI1000");
    assert.equal(envelope.fault.data.type, "runic.application.tool.size/1");
    assert.equal(envelope.fault.data.payload.report, jsonReport);
    assert.equal(envelope.fault.data.payload.verification.status, "failed");
    assert.equal(envelope.fault.data.payload.totalBytes, JSON.parse(readFileSync(jsonReport, "utf8")).totalBytes);
    assert.equal(readFileSync(report, "utf8"), original, "Prior evidence was changed");
    rmSync(marker);
    writeFileSync(code, '#error Deliberate publish failure');
    const broken = join(directory, "failed-publish.json");
    assert.equal(invoke(broken).status, 1);
    const failure = JSON.parse(readFileSync(broken, "utf8"));
    assert.notEqual(failure.publishExitCode, 0);
    assert.equal(failure.verification.status, "not-run");
    assert.ok(!existsSync(marker), "A failed publish must not run the checker");
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

const size = (directory, args) => {
  const result = spawnSync(process.env.DOTNET_HOST_PATH ?? "dotnet", [cli, "size", ...args],
    { cwd: directory, encoding: "utf8", timeout: 120000, maxBuffer: 8 * 1024 * 1024 });
  if (result.error) throw result.error;
  return result;
};

// W319-003 cli-10: the default report was always runic-size.json, so a second run without --report failed.
test("size without --report writes a new report each run", { timeout: 180000 }, () => {
  assert.ok(existsSync(cli), `Build tools/dotnet-runic first: ${cli} is missing.`);
  const directory = mkdtempSync(join(tmpdir(), "runic-size-default-"));
  try {
    writeFileSync(join(directory, "Probe.csproj"), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>');
    writeFileSync(join(directory, "Program.cs"), 'System.Console.WriteLine("size probe");');
    const reports = () => readdirSync(directory).filter(name => /^runic-size-.+\.json$/.test(name)).sort();
    for (const run of [1, 2]) {
      const result = size(directory, ["--runtime", rid, "--no-aot"]);
      assert.equal(result.status, 0, result.stdout + result.stderr);
      assert.equal(reports().length, run, reports().join(", "));
    }
    for (const name of reports()) {
      assert.match(name, new RegExp(`^runic-size-${rid}-\\d{8}T\\d{6}Z(-\\d+)?\\.json$`));
      const report = JSON.parse(readFileSync(join(directory, name), "utf8"));
      assert.ok(existsSync(report.archivePath) && report.archivePath.startsWith(join(directory, name.slice(0, -".json".length))), report.archivePath);
    }
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

// W319-003 cli-07: an MSBuild evaluation failure is a failed command (exit code 1), not a usage error (2).
test("size reports a project MSBuild cannot evaluate as a command failure", { timeout: 120000 }, () => {
  assert.ok(existsSync(cli), `Build tools/dotnet-runic first: ${cli} is missing.`);
  const directory = mkdtempSync(join(tmpdir(), "runic-size-evaluation-"));
  try {
    writeFileSync(join(directory, "Broken.csproj"), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>');
    const result = size(directory, ["--project", join(directory, "Broken.csproj"), "--runtime", rid, "--format", "json"]);
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stdout, /"code": ?"RAPPSIZE1004"/);
    assert.deepEqual(readdirSync(directory), ["Broken.csproj"], "Nothing is written before the settings are known.");
  } finally { rmSync(directory, { recursive: true, force: true }); }
});
