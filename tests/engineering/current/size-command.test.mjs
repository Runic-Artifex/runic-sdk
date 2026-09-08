import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const root = resolve(fileURLToPath(new URL("../../..", import.meta.url)));
const cli = resolve(root, `tools/dotnet-runic/bin/${process.env.CONFIGURATION ?? "Debug"}/net10.0/dotnet-runic.dll`);
const rid = `${({ linux: "linux", darwin: "osx", win32: "win" })[process.platform]}-${process.arch}`;

test("size preserves failed verification, publish failures and existing evidence", { timeout: 180000 }, () => {
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
    assert.equal(measured.publishExitCode, 0);
    assert.equal(measured.verification.status, "failed");
    assert.equal(measured.verification.exitCode, 7);
    assert.ok(existsSync(marker));
    assert.ok(existsSync(measured.archive));
    assert.equal(measured.totalBytes, measured.files.reduce((sum, file) => sum + file.bytes, 0));
    assert.ok(measured.compressedBytes > 0);
    assert.ok(measured.buildOperatingSystem);
    assert.equal(invoke(report).status, 2, "Existing reports must be rejected");
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
