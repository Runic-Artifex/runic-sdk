import assert from "node:assert/strict";
import { test } from "node:test";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { resolve } from "node:path";
import { root, workspace } from "./run.mjs";
import { readSupport, renderReadme, renderSupportMarkdown, supportRids, validateSupport } from "./support.mjs";

const support = readSupport(root);
const workflow = Bun.YAML.parse(readFileSync(resolve(root, ".github/workflows/ci.yml"), "utf8"));

test("ci-verified support entries match the Native CI matrix", () => {
  // Every Native job builds and runs both hosts' native window layers: the
  // Runic Desktop WebView smoke and the CS-WebUI window probes.
  const matrix = workflow.jobs.native.strategy.matrix.include;
  const nativeRids = matrix.map(entry => entry.rid).sort();
  for (const host of support.hosts) {
    const verified = host.targets.filter(target => target.status === "ci-verified").map(target => target.rid).sort();
    assert.deepEqual(verified, nativeRids, `${host.id}: ci-verified RIDs must equal the Native job matrix`);
  }
  const runnerOs = os => /^ubuntu-/.test(os) ? "Linux" : /^windows-/.test(os) ? "Windows" : /^macos-/.test(os) ? "macOS" : assert.fail(`unknown runner ${os}`);
  // Only the step conditions the Native job uses are understood; anything else fails the test.
  // It cannot see `$RUNNER_OS` branches inside a step's shell script, such as the probe step's
  // Linux/else branches; the patterns below match commands that both branches run.
  const runsOn = (step, os) => {
    if (step.if === undefined) return true;
    const match = /^runner\.os (==|!=) '(\w+)'$/.exec(step.if);
    assert.ok(match, `unexpected Native step condition: ${step.if}`);
    return (match[2] === os) === (match[1] === "==");
  };
  for (const entry of matrix) {
    const os = runnerOs(entry.os);
    for (const [probe, pattern] of [["Runic Desktop WebView smoke", /dotnet\s+\S*Runic\.Desktop\.WebViewSmoke\.dll|Runic\.Desktop\.WebViewSmoke\.exe/],
      ["CS-WebUI window probes", /CsWebUiWindowProbes\$\{\{ matrix\.extension \}\} --probe-window-close/]]) {
      assert.ok(workflow.jobs.native.steps.some(step => typeof step.run === "string" && pattern.test(step.run) && runsOn(step, os)),
        `${entry.rid}: the Native job must run the ${probe}`);
    }
  }
});

// Read from the restored NuGet cache when present; the CI engineering job may not restore it.
const nativeVersion = /<PackageVersion Include="CsWebUi\.Native" Version="([^"]+)"/.exec(readFileSync(resolve(root, "Directory.Packages.props"), "utf8"))?.[1];
const nativePackage = [process.env.NUGET_PACKAGES, resolve(root, ".cache/nuget"), resolve(homedir(), ".nuget/packages")]
  .filter(Boolean).map(cache => resolve(cache, "cswebui.native", nativeVersion ?? "missing")).find(path => existsSync(path));
test("CS-WebUI support follows the CsWebUi.Native runtimes", { skip: !nativePackage }, () => {
  const shipped = readdirSync(resolve(nativePackage, "runtimes")).sort();
  const listed = support.hosts.find(host => host.id === "cswebui").targets
    .filter(target => target.status !== "unsupported").map(target => target.rid).sort();
  assert.deepEqual(listed, shipped, `CS-WebUI entries must match CsWebUi.Native ${nativeVersion} runtimes/`);
});

test("support hosts name shipped packages and the README table is current", () => {
  for (const host of support.hosts)
    assert.ok(workspace.nuget.some(entry => entry.name === host.package), `${host.id}: unknown package ${host.package}`);
  assert.ok(supportRids(support).length >= 3);
  const readme = readFileSync(resolve(root, "README.md"), "utf8");
  assert.equal(renderReadme(readme, support), readme, "run bun tools/dotnet-runic/metadata/generate.mjs --write");
});

test("the compatibility set embeds the support matrix", () => {
  const set = JSON.parse(readFileSync(resolve(root, "tools/dotnet-runic/metadata/runic.compatibility-set.json"), "utf8"));
  assert.equal(set.schemaVersion, 2);
  const { $comment, schemaVersion, ...embedded } = support;
  assert.deepEqual(set.support, embedded);
});

test("support validation rejects incomplete entries", () => {
  const clone = () => structuredClone(support);
  const broken = clone();
  broken.hosts[0].targets[0].status = "verified";
  assert.throws(() => validateSupport(broken), /status/);
  const missingReason = clone();
  missingReason.hosts[0].targets[0].reason = "";
  assert.throws(() => validateSupport(missingReason), /reason/);
  const duplicate = clone();
  duplicate.hosts[0].targets.push(duplicate.hosts[0].targets[0]);
  assert.throws(() => validateSupport(duplicate), /duplicate RID/);
  const unsupported = clone();
  delete unsupported.hosts.find(host => host.id === "cswebui").targets.find(target => target.status === "unsupported").remediation;
  assert.throws(() => validateSupport(unsupported), /remediation/);
  assert.match(renderSupportMarkdown(support), /\| `linux-x64` \| CI-verified \| CI-verified \|/);
  assert.throws(() => renderReadme("no markers", support), /must contain/);
});
