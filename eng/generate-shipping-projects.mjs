import assert from "node:assert/strict";
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

const root = fileURLToPath(new URL("..", import.meta.url));
export function shippingProjects(workspace) {
  assert(Array.isArray(workspace.nuget) && workspace.nuget.length > 0, "Missing NuGet inventory");
  const identities = new Set(), paths = new Set();
  const projects = workspace.nuget.map(({ name, project }) => {
    assert(typeof name === "string" && /^[A-Za-z0-9.-]+$/.test(name), "Invalid NuGet identity");
    assert(typeof project === "string" && /^(packages|tools)\/[A-Za-z0-9./-]+\.csproj$/.test(project) && !project.split("/").includes(".."), "Invalid shipping project path");
    assert(!identities.has(name.toLowerCase()) && !paths.has(project.toLowerCase()), "Duplicate shipping project or identity");
    identities.add(name.toLowerCase()); paths.add(project.toLowerCase());
    return { name, project };
  });
  const items = projects.map(({ name, project }) => `    <RunicShippingProject Include="$(MSBuildThisFileDirectory)../../${project}" PackageId="${name}" />`);
  const flags = projects.map(({ project }) => `    <RunicIsShippingProject Condition="'$(_RunicProjectPath)' == '${project}'">true</RunicIsShippingProject>`);
  return `<Project>\n  <!-- Generated from eng/workspace.json by eng/generate-shipping-projects.mjs. -->\n  <ItemGroup>\n${items.join("\n")}\n  </ItemGroup>\n  <PropertyGroup>\n    <_RunicProjectPath>$([MSBuild]::MakeRelative($([MSBuild]::NormalizeDirectory($(MSBuildThisFileDirectory)../..)), $(MSBuildProjectFullPath)).Replace('\\', '/'))</_RunicProjectPath>\n${flags.join("\n")}\n  </PropertyGroup>\n</Project>\n`;
}

// Each component policy owns the flag that marks a project as trimmable, AOT-compatible
// and analyzed as shipping code. Desktop keeps its separate policy and sets both explicitly.
const policies = [
  { props: "application.props", required: ["RunicToolkitShippingProject"] },
  { props: "assets.props", required: ["RunicAssetsShippingProject"] },
  { props: "desktop.props", required: ["IsAotCompatible", "IsTrimmable"] },
];
export function shippingPolicyErrors(workspace, read = project => readFileSync(resolve(root, project), "utf8")) {
  return workspace.nuget.flatMap(({ project }) => {
    const source = read(project).replace(/<!--[\s\S]*?-->/g, "");
    // Template packages contain no assemblies, so trim and AOT policy does not apply.
    if (/<PackageType>Template<\/PackageType>/.test(source) && /<IncludeBuildOutput>false<\/IncludeBuildOutput>/.test(source)) return [];
    const policy = policies.find(({ props }) => new RegExp(`<Import Project="[^"]*eng/build/${props.replace(".", "\\.")}"`).test(source));
    if (!policy) return [`${project} imports no shipping build policy from eng/build`];
    return policy.required.filter(flag => !new RegExp(`<${flag}>true</${flag}>`).test(source))
      .map(flag => `${project} must set <${flag}>true</${flag}> for ${policy.props}`);
  });
}
export function checkShippingProjects() {
  const workspace = JSON.parse(readFileSync(resolve(root, "eng/workspace.json"), "utf8"));
  assert.deepEqual(shippingPolicyErrors(workspace), [], "Shipping projects must opt into their component shipping policy");
  assert.equal(readFileSync(resolve(root, "eng/build/shipping-projects.props"), "utf8"), shippingProjects(workspace),
    "Shipping project inventory is stale; run bun eng/generate-shipping-projects.mjs");
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.includes("--check")) checkShippingProjects();
  else {
    const workspace = JSON.parse(readFileSync(resolve(root, "eng/workspace.json"), "utf8"));
    writeFileSync(resolve(root, "eng/build/shipping-projects.props"), shippingProjects(workspace));
    const errors = shippingPolicyErrors(workspace);
    if (errors.length) { console.error(errors.join("\n")); process.exitCode = 1; }
  }
}
