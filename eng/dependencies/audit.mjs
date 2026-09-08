#!/usr/bin/env bun
// Read-only registry audit. Major/prerelease upgrades remain explicit decisions.
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "../..");
const files = execFileSync("git", ["ls-files", "-z"], { cwd: root, encoding: "utf8" }).split("\0")
  .filter(path => path && !path.startsWith("eng/archive/") && !path.startsWith("tests/fixtures/legacy-examples/"));
const packages = new Map();
function declare(ecosystem, name, version, path, kind) {
  if (name.startsWith("@runic-artifex/") || name.startsWith("Runic.") || version.includes("$(") || version.startsWith("__")) return;
  const key = `${ecosystem}:${name}`;
  if (!packages.has(key)) packages.set(key, { ecosystem, name, declarations: [], resolved: [] });
  packages.get(key).declarations.push({ path, kind, version });
}
for (const path of files) {
  if (path.endsWith("package.json")) {
    const manifest = JSON.parse(readFileSync(resolve(root, path), "utf8"));
    for (const kind of ["dependencies", "devDependencies", "peerDependencies", "optionalDependencies"])
      for (const [name, version] of Object.entries(manifest[kind] ?? {})) declare("npm", name, version, path, kind);
  } else if (/\.(props|csproj)$/.test(path)) {
    const source = readFileSync(resolve(root, path), "utf8");
    for (const match of source.matchAll(/<(PackageVersion|PackageReference)\b[^>]*\bInclude="([^"]+)"[^>]*\bVersion(?:Override)?="([^"]+)"/g))
      declare("nuget", match[2], match[3], path, match[1]);
  } else if ((path.startsWith(".github/") || path.startsWith("eng/ci/fixtures/")) && /\.ya?ml$/.test(path)) {
    for (const match of readFileSync(resolve(root, path), "utf8").matchAll(/uses:\s*([\w.-]+\/[\w.-]+)(?:\/[\w./-]+)?@([^\s#]+)/g))
      declare("github-action", match[1], match[2], path, "uses");
  }
}
// The Angular packaging canary creates its npm manifest at runtime.
const angularCanary = "tests/web/angular-package-consumer/test-package-consumer.mjs";
const ngPackagr = readFileSync(resolve(root, angularCanary), "utf8").match(/"ng-packagr": "([^"]+)"/);
if (ngPackagr) declare("npm", "ng-packagr", ngPackagr[1], angularCanary, "devDependencies");
for (const name of ["bun", "npm", "pnpm", "devframe", "crossws"])
  if (!packages.has(`npm:${name}`)) packages.set(`npm:${name}`, { ecosystem: "npm", name, declarations: [], resolved: [] });
for (const path of files.filter(path => path.endsWith("bun.lock"))) {
  const lock = Bun.JSONC.parse(readFileSync(resolve(root, path), "utf8"));
  for (const entry of Object.values(lock.packages ?? {})) {
    const identity = entry[0];
    const split = identity.lastIndexOf("@");
    const item = packages.get(`npm:${identity.slice(0, split)}`);
    if (item && !identity.includes("workspace:")) item.resolved.push({ path, version: identity.slice(split + 1) });
  }
}

async function json(url) {
  const response = await fetch(url, { signal: AbortSignal.timeout(30000) });
  if (!response.ok) throw new Error(`${response.status}: ${url}`);
  return response.json();
}
async function inspect(item) {
  try {
    if (item.ecosystem === "npm") {
      const data = await json(`https://registry.npmjs.org/${encodeURIComponent(item.name)}`);
      item.candidates = Object.fromEntries(Object.entries(data["dist-tags"]).filter(([tag]) => ["latest", "rc", "beta", "next"].includes(tag)));
      item.source = `https://www.npmjs.com/package/${item.name}`;
      item.requirements = Object.fromEntries(Object.entries(item.candidates).map(([tag, version]) => [tag, {
        engines: data.versions[version]?.engines, peers: data.versions[version]?.peerDependencies,
      }]));
    } else if (item.ecosystem === "nuget") {
      const data = await json(`https://api.nuget.org/v3-flatcontainer/${item.name.toLowerCase()}/index.json`);
      item.candidates = { latest: data.versions.findLast(version => !version.includes("-")) ?? null,
        prerelease: data.versions.findLast(version => version.includes("-")) ?? null };
      item.source = `https://www.nuget.org/packages/${item.name}`;
    } else {
      const data = JSON.parse(execFileSync("gh", ["api", `repos/${item.name}/releases/latest`], { encoding: "utf8", timeout: 30000 }));
      item.candidates = { latest: data.tag_name };
      item.source = data.html_url;
    }
  } catch (error) { item.error = String(error); }
  return item;
}
const queue = [...packages.values()].sort((a, b) => `${a.ecosystem}:${a.name}`.localeCompare(`${b.ecosystem}:${b.name}`));
const results = [];
// Bound concurrent registry requests; do not mutate manifests or lockfiles.
for (let index = 0; index < queue.length; index += 6)
  results.push(...await Promise.all(queue.slice(index, index + 6).map(inspect)));
console.log(JSON.stringify({ checkedAt: new Date().toISOString(),
  toolchain: JSON.parse(readFileSync(resolve(root, "eng/release/runic.compatibility-set.json"), "utf8")).toolchain,
  nixpkgs: JSON.parse(readFileSync(resolve(root, "flake.lock"), "utf8")).nodes.nixpkgs.locked,
  packages: results }, null, 2));
if (results.some(item => item.error)) process.exitCode = 1;
