// The SDK adds some packages to a restore implicitly, at the version of the runtime it bundles: the NativeAOT
// compiler and its per-runtime packages, the trimmer's build task, and the runtime, host and ASP.NET Core packs.
// `bun run lock:nuget` rewrites the solution's lock files when the SDK moves, but the independent fixtures' lock
// files (tests/fixtures, templates, examples) are restored separately and can keep an older runtime's packages,
// which their offline builds then download and use. Each such package in a committed lock file must be at the
// SDK's runtime version, which package-downloads.json records (its check compares that with every CI restore),
// and outside every known advisory's affected range.
import { expect, test } from "bun:test";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { listPath } from "../../eng/dependencies/package-downloads.mjs";

const root = resolve(import.meta.dir, "../..");

const runtimeVersioned = /^(Microsoft\.DotNet\.ILCompiler|runtime\.[\w-]+\.Microsoft\.DotNet\.ILCompiler|Microsoft\.NET\.ILLink\.Tasks|Microsoft\.NETCore\.App\.(Runtime|Host|Crossgen2)\.[\w-]+|Microsoft\.AspNetCore\.App\.Runtime\.[\w-]+)$/i;

// Advisories for runtime-versioned packages: the packages they name, every version from `introduced` up to but not
// including `fixed`. Add one when the SDK moves for a .NET security release.
const advisories = [
  { id: "GHSA-p3jp-9v83-76w9", cve: "CVE-2026-111497", introduced: "10.0.0", fixed: "10.0.14",
    packages: /^(Microsoft\.DotNet\.ILCompiler|runtime\.[\w-]+\.Microsoft\.DotNet\.ILCompiler|Microsoft\.NETCore\.App\.Runtime\.[\w-]+)$/i },
];

const parts = version => version.split("-")[0].split(".").map(Number);
const compareVersions = (a, b) => {
  const [x, y] = [parts(a), parts(b)];
  for (let index = 0; index < Math.max(x.length, y.length); index++) {
    const difference = (x[index] ?? 0) - (y[index] ?? 0);
    if (difference !== 0) return difference;
  }
  return 0;
};
const affected = (id, version) => advisories.filter(advisory => advisory.packages.test(id) &&
  compareVersions(version, advisory.introduced) >= 0 && compareVersions(version, advisory.fixed) < 0);

// The SDK's runtime version: the NativeAOT compiler pack the SDK downloads.
function sdkRuntimeVersion() {
  const versions = new Set(JSON.parse(readFileSync(listPath, "utf8"))
    .filter(item => item.id === "Microsoft.DotNet.ILCompiler").map(item => item.version));
  expect([...versions]).toHaveLength(1);
  return [...versions][0];
}

// Every runtime-versioned package of every committed NuGet lock file, with the lock file and target.
function lockedRuntimePackages() {
  const files = execFileSync("git", ["ls-files", "-z", "--", "*packages.lock.json", "*packages.*.lock.json"], { cwd: root, encoding: "utf8" })
    .split("\0").filter(Boolean);
  expect(files).toContain("tests/fixtures/flathub/App/packages.aot.lock.json");
  return files.flatMap(path => Object.entries(JSON.parse(readFileSync(resolve(root, path), "utf8")).dependencies ?? {})
    .flatMap(([target, packages]) => Object.entries(packages)
      .filter(([id]) => runtimeVersioned.test(id))
      .map(([id, entry]) => ({ path, target, id, version: entry.resolved }))));
}

test("an advisory range covers versions from introduced up to but not including fixed", () => {
  expect(affected("runtime.linux-x64.Microsoft.DotNet.ILCompiler", "10.0.12").map(advisory => advisory.id)).toEqual(["GHSA-p3jp-9v83-76w9"]);
  expect(affected("Microsoft.NETCore.App.Runtime.linux-arm64", "10.0.0")).toHaveLength(1);
  expect(affected("Microsoft.DotNet.ILCompiler", "10.0.14")).toEqual([]);
  expect(affected("Microsoft.DotNet.ILCompiler", "9.0.11")).toEqual([]);
  expect(affected("Microsoft.NET.ILLink.Tasks", "10.0.12")).toEqual([]);
  expect(runtimeVersioned.test("runtime.linux-arm64.Microsoft.DotNet.ILCompiler")).toBe(true);
  expect(runtimeVersioned.test("Microsoft.Extensions.DependencyInjection")).toBe(false);
});

test("the SDK's runtime packs are outside every known advisory", () => {
  const version = sdkRuntimeVersion();
  expect(affected("Microsoft.DotNet.ILCompiler", version).map(advisory => `${advisory.id} (${advisory.cve})`)).toEqual([]);
  const vulnerableDownloads = JSON.parse(readFileSync(listPath, "utf8"))
    .filter(item => affected(item.id, item.version).length)
    .map(item => `${item.id} ${item.version}`);
  expect(vulnerableDownloads).toEqual([]);
});

test("every committed lock file pins the SDK's runtime packages at its runtime version, outside known advisories", () => {
  const version = sdkRuntimeVersion();
  const locked = lockedRuntimePackages();
  expect(locked.length).toBeGreaterThan(0);
  const stale = locked.filter(item => item.version !== version)
    .map(item => `${item.path} (${item.target}): ${item.id} ${item.version}, not the SDK's ${version}`
      + affected(item.id, item.version).map(advisory => `; affected by ${advisory.id} (${advisory.cve})`).join(""));
  // Restore a stale fixture's lock file again with the SDK, for example dotnet restore -p:PublishAot=true --force-evaluate.
  expect(stale).toEqual([]);
});
