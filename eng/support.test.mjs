import assert from "node:assert/strict";
import { test } from "node:test";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { resolve } from "node:path";
import { root, workspace } from "./run.mjs";
import { readSupport, renderReadme, renderSupportMarkdown, supportRids, validateSupport } from "./support.mjs";
import { nativeLegs } from "./ci/platforms.mjs";

const support = readSupport(root);
const workflow = Bun.YAML.parse(readFileSync(resolve(root, ".github/workflows/ci.yml"), "utf8"));

test("ci-verified support entries match the Native CI jobs", () => {
  // Every Native entry builds and runs Runic Desktop's native window layer (the WebView
  // smoke); entries with `cswebui: true` also run the CS-WebUI window probes. The musl
  // entries run Runic Desktop's checks in Alpine through eng/ci/native-musl.sh.
  // The plan selects the Native legs from eng/ci/platforms.mjs; nightly and manual runs run them all.
  assert.equal(workflow.jobs.native.strategy.matrix, "${{ fromJSON(needs.plan.outputs.native) }}");
  const matrix = nativeLegs;
  const musl = workflow.jobs["native-musl"].strategy.matrix.include;
  const expected = {
    desktop: [...matrix, ...musl].map(entry => entry.rid).sort(),
    cswebui: matrix.filter(entry => entry.cswebui === true).map(entry => entry.rid).sort(),
  };
  assert.deepEqual(support.hosts.map(host => host.id).sort(), Object.keys(expected).sort());
  for (const host of support.hosts) {
    const verified = host.targets.filter(target => target.status === "ci-verified").map(target => target.rid).sort();
    assert.deepEqual(verified, expected[host.id], `${host.id}: ci-verified RIDs must equal the Native jobs that run its window layer`);
  }
  for (const entry of matrix) assert.equal(typeof entry.cswebui, "boolean", `${entry.rid}: cswebui must be true or false`);
  const runner = os => {
    const name = /^ubuntu-/.test(os) ? "Linux" : /^windows-/.test(os) ? "Windows" : /^macos-/.test(os) ? "macOS" : assert.fail(`unknown runner ${os}`);
    // Hosted macOS 14 and later and the -arm labels are Arm runners.
    return { os: name, arch: /-arm$/.test(os) || name === "macOS" ? "ARM64" : "X64" };
  };
  // Only the step conditions the Native job uses are understood; anything else fails the test.
  // It cannot see `$RUNNER_OS` branches inside a step's shell script, such as the probe step's
  // Linux/else branches; the patterns below match commands that both branches run.
  const holds = (condition, entry, { os, arch }) => {
    if (condition === "matrix.cswebui") return entry.cswebui;
    const match = /^runner\.(os|arch) (==|!=) '(\w+)'$/.exec(condition);
    assert.ok(match, `unexpected Native step condition: ${condition}`);
    return ((match[1] === "os" ? os : arch) === match[3]) === (match[2] === "==");
  };
  const runsOn = (step, entry) => step.if === undefined
    || step.if.split(" && ").every(condition => holds(condition, entry, runner(entry.os)));
  for (const entry of matrix) {
    const probes = [["Runic Desktop WebView smoke", /dotnet\s+\S*Runic\.Desktop\.WebViewSmoke\.dll|Runic\.Desktop\.WebViewSmoke\.exe/]];
    if (entry.cswebui) probes.push(["CS-WebUI window probes", /CsWebUiWindowProbes\$\{\{ matrix\.extension \}\} --probe-window-close/]);
    for (const [probe, pattern] of probes)
      assert.ok(workflow.jobs.native.steps.some(step => typeof step.run === "string" && pattern.test(step.run) && runsOn(step, entry)),
        `${entry.rid}: the Native job must run the ${probe}`);
    // CS-WebUI ships no native library for the RID, so no step may publish for it.
    if (!entry.cswebui)
      assert.ok(!workflow.jobs.native.steps.some(step => typeof step.run === "string" && /CsWebUiWindowProbes/.test(step.run) && runsOn(step, entry)),
        `${entry.rid}: the Native job must not run the CS-WebUI window probes`);
  }
  const script = readFileSync(resolve(root, "eng/ci/native-musl.sh"), "utf8");
  for (const entry of musl) {
    assert.match(entry.rid, /^linux-musl-/);
    assert.match(script, new RegExp(`^\\s+(?:[\\w-]+ \\| )*${entry.rid}(?: \\| [\\w-]+)*\\) ;;$`, "m"), `eng/ci/native-musl.sh must accept ${entry.rid}`);
  }
  assert.ok(workflow.jobs["native-musl"].steps.some(step => step.run?.includes('sh eng/ci/native-musl.sh "$RID"')
    && step.env?.RID === "${{ matrix.rid }}"));
  assert.match(script, /xvfb-run -a dotnet "\$smoke"\n/);
  assert.match(script, /xvfb-run -a artifacts\/webview-aot\/Runic\.Desktop\.WebViewSmoke\n/);
});

// Read from the restored NuGet cache when present; the CI engineering job may not restore it.
const nativeVersion = /<PackageVersion Include="CsWebUi\.Native" Version="([^"]+)"/.exec(readFileSync(resolve(root, "Directory.Packages.props"), "utf8"))?.[1];
const nativePackage = [process.env.NUGET_PACKAGES, resolve(root, ".cache/nuget"), resolve(homedir(), ".nuget/packages")]
  .filter(Boolean).map(cache => resolve(cache, "cswebui.native", nativeVersion ?? "missing")).find(path => existsSync(path));
test("CS-WebUI support follows the CsWebUi.Native runtimes", { skip: !nativePackage }, () => {
  // Every shipped runtime is listed; one Runic does not target (osx-x64, Intel Macs) is listed as unsupported.
  const targets = support.hosts.find(host => host.id === "cswebui").targets;
  const notTargeted = targets.filter(target => target.status === "unsupported").map(target => target.rid);
  const shipped = readdirSync(resolve(nativePackage, "runtimes")).filter(rid => !notTargeted.includes(rid)).sort();
  const listed = targets.filter(target => target.status !== "unsupported").map(target => target.rid).sort();
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
