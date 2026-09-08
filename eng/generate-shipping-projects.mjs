import assert from "node:assert/strict";
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

const root = fileURLToPath(new URL("..", import.meta.url));
export function shippingProjects(workspace) {
  assert(Array.isArray(workspace.nuget) && workspace.nuget.length > 0, "Missing NuGet inventory");
  const identities = new Set(), paths = new Set();
  const entries = workspace.nuget.map(({ name, project }) => {
    assert(typeof name === "string" && /^[A-Za-z0-9.-]+$/.test(name), "Invalid NuGet identity");
    assert(typeof project === "string" && /^(packages|tools)\/[A-Za-z0-9./-]+\.csproj$/.test(project) && !project.split("/").includes(".."), "Invalid shipping project path");
    assert(!identities.has(name.toLowerCase()) && !paths.has(project.toLowerCase()), "Duplicate shipping project or identity");
    identities.add(name.toLowerCase()); paths.add(project.toLowerCase());
    return `    <RunicShippingProject Include="$(MSBuildThisFileDirectory)../../${project}" PackageId="${name}" />`;
  });
  return `<Project>\n  <!-- Generated from eng/workspace.json by eng/generate-shipping-projects.mjs. -->\n  <ItemGroup>\n${entries.join("\n")}\n  </ItemGroup>\n</Project>\n`;
}
export function checkShippingProjects() {
  const expected = shippingProjects(JSON.parse(readFileSync(resolve(root, "eng/workspace.json"), "utf8")));
  assert.equal(readFileSync(resolve(root, "eng/build/shipping-projects.props"), "utf8"), expected,
    "Shipping project inventory is stale; run bun eng/generate-shipping-projects.mjs");
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.includes("--check")) checkShippingProjects();
  else writeFileSync(resolve(root, "eng/build/shipping-projects.props"), shippingProjects(JSON.parse(readFileSync(resolve(root, "eng/workspace.json"), "utf8"))));
}
