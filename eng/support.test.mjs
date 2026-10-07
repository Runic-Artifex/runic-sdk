import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { root, workspace } from "./run.mjs";
import { readSupport, renderReadme, renderSupportMarkdown, supportRids, validateSupport } from "./support.mjs";

const support = readSupport(root);
const workflow = Bun.YAML.parse(readFileSync(resolve(root, ".github/workflows/ci.yml"), "utf8"));

test("ci-verified support entries match the Native CI matrix", () => {
  // Both Window hosts run in every Native job (Desktop smoke and CS-WebUI probes).
  const nativeRids = workflow.jobs.native.strategy.matrix.include.map(entry => entry.rid).sort();
  for (const host of support.hosts) {
    const verified = host.targets.filter(target => target.status === "ci-verified").map(target => target.rid).sort();
    assert.deepEqual(verified, nativeRids, `${host.id}: ci-verified RIDs must equal the Native job matrix`);
  }
  const steps = JSON.stringify(workflow.jobs.native.steps);
  assert.match(steps, /CsWebUiWindowProbes/, "the Native job must exercise the CS-WebUI host");
  assert.match(steps, /Runic\.Desktop\.WebViewSmoke/, "the Native job must exercise the Runic Desktop host");
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
  assert.equal(set.schemaVersion, 3);
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
