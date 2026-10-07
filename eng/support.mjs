// Reads and validates eng/support.json and renders the README support table.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

export const supportStatuses = ["ci-verified", "packaged-unverified", "unsupported"];
const statusLabels = {
  "ci-verified": "CI-verified",
  "packaged-unverified": "Packaged, not CI-verified",
  "unsupported": "Unsupported",
};
const osNames = { win: "Windows", linux: "Linux", osx: "macOS" };
export const readmeMarkers = ["<!-- support-matrix:begin (generated from eng/support.json) -->", "<!-- support-matrix:end -->"];

export function readSupport(root) {
  const support = JSON.parse(readFileSync(resolve(root, "eng/support.json"), "utf8"));
  validateSupport(support);
  return support;
}

export function validateSupport(support) {
  assert.equal(support.schemaVersion, 1, "eng/support.json: unknown schemaVersion");
  assert.deepEqual(Object.keys(support.statuses), supportStatuses, "eng/support.json: statuses must describe every status");
  assert.ok(support.hosts.length > 0, "eng/support.json: no hosts");
  const hostIds = support.hosts.map(host => host.id);
  assert.equal(new Set(hostIds).size, hostIds.length, "eng/support.json: duplicate host");
  for (const host of support.hosts) {
    assert.match(host.id, /^[a-z0-9]+$/, `eng/support.json: host id ${host.id}`);
    assert.ok(host.name && host.package, `eng/support.json: ${host.id} needs a name and package`);
    const rids = host.targets.map(target => target.rid);
    assert.equal(new Set(rids).size, rids.length, `eng/support.json: duplicate RID in ${host.id}`);
    for (const target of host.targets) {
      assert.match(target.rid, /^(?:win|linux|linux-musl|osx)-(?:x64|arm64)$/, `eng/support.json: ${host.id} RID ${target.rid}`);
      assert.ok(supportStatuses.includes(target.status), `eng/support.json: ${host.id}/${target.rid} status ${target.status}`);
      assert.ok(typeof target.reason === "string" && target.reason.length > 0, `eng/support.json: ${host.id}/${target.rid} needs a reason`);
      assert.equal(target.status === "unsupported", typeof target.remediation === "string",
        `eng/support.json: ${host.id}/${target.rid} needs a remediation exactly when unsupported`);
    }
  }
  const requirementIds = support.requirements.map(requirement => requirement.id);
  assert.equal(new Set(requirementIds).size, requirementIds.length, "eng/support.json: duplicate requirement");
  for (const requirement of support.requirements) {
    assert.ok(Object.hasOwn(osNames, requirement.os), `eng/support.json: requirement ${requirement.id} os ${requirement.os}`);
    assert.ok(requirement.hosts.length > 0 && requirement.hosts.every(id => hostIds.includes(id)),
      `eng/support.json: requirement ${requirement.id} names an unknown host`);
    assert.ok(requirement.component && requirement.note, `eng/support.json: requirement ${requirement.id} needs a component and note`);
    assert.ok(requirement.minimum === null || /^\d+(?:\.\d+)*$/.test(requirement.minimum),
      `eng/support.json: requirement ${requirement.id} minimum ${requirement.minimum}`);
  }
}

// The RIDs of all hosts in first-listed order.
export function supportRids(support) {
  return [...new Set(support.hosts.flatMap(host => host.targets.map(target => target.rid)))];
}

const status = (host, rid) => host.targets.find(target => target.rid === rid)?.status ?? "unsupported";

export function renderSupportMarkdown(support) {
  const lines = [
    `| RID | ${support.hosts.map(host => host.name).join(" | ")} |`,
    `| --- | ${support.hosts.map(() => "---").join(" | ")} |`,
    ...supportRids(support).map(rid => `| \`${rid}\` | ${support.hosts.map(host => statusLabels[status(host, rid)]).join(" | ")} |`),
    "",
    ...supportStatuses.map(name => `- ${statusLabels[name]}: ${support.statuses[name]}`),
    "",
    "| OS | Host | Requirement | Notes |",
    "| --- | --- | --- | --- |",
    ...support.requirements.map(requirement => {
      const hosts = requirement.hosts.map(id => support.hosts.find(host => host.id === id).name).join(", ");
      const component = requirement.minimum ? `${requirement.component} ${requirement.minimum} or newer` : requirement.component;
      return `| ${osNames[requirement.os]} | ${hosts} | ${component} | ${requirement.note} |`;
    }),
    "",
    "Notes on targets that CI does not verify:",
    "",
    ...support.hosts.flatMap(host => host.targets.filter(target => target.status !== "ci-verified")
      .map(target => `- ${host.name}, \`${target.rid}\`: ${target.reason}`)),
  ];
  return lines.join("\n");
}

// Replaces the text between the README markers; throws when they are missing.
export function renderReadme(readme, support) {
  const [begin, end] = readmeMarkers;
  const start = readme.indexOf(begin);
  const finish = readme.indexOf(end);
  assert.ok(start >= 0 && finish > start, `README.md must contain ${begin} and ${end}`);
  return `${readme.slice(0, start + begin.length)}\n${renderSupportMarkdown(support)}\n${readme.slice(finish)}`;
}
