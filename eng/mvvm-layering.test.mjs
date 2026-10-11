// MVVM layering (D018.9): ReactiveUI is the primary MVVM path and CommunityToolkit
// is supported, but general-purpose projects (Views core, Navigation core, Platform,
// Desktop, Assets, tools) stay free of both. Only the projects below may depend on
// ReactiveUI, System.Reactive or CommunityToolkit.Mvvm, directly or transitively.
import assert from "node:assert/strict";
import { test } from "node:test";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));

export const mvvmLibraries = ["ReactiveUI", "System.Reactive", "CommunityToolkit.Mvvm"];

// Project -> the libraries it may use and why. Keep entries minimal: a new
// general-purpose dependency belongs in an adapter, not in this list.
export const mvvmAllowList = {
  "packages/dotnet/Runic.Application.Views.ReactiveUI/Runic.Application.Views.ReactiveUI.csproj":
    { libraries: ["ReactiveUI"], reason: "ReactiveUI Views adapter" },
  "packages/dotnet/Runic.Application.Views.ReactiveUI.Reactive/Runic.Application.Views.ReactiveUI.Reactive.csproj":
    { libraries: ["ReactiveUI", "System.Reactive"], reason: "ReactiveUI System.Reactive-flavour Views adapter" },
  "packages/dotnet/Runic.Navigation.ReactiveUI/Runic.Navigation.ReactiveUI.csproj":
    { libraries: ["ReactiveUI"], reason: "ReactiveUI Navigation adapter" },
  "packages/dotnet/Runic.Navigation.ReactiveUI.Reactive/Runic.Navigation.ReactiveUI.Reactive.csproj":
    { libraries: ["ReactiveUI", "System.Reactive"], reason: "ReactiveUI System.Reactive-flavour Navigation adapter" },
  "tools/Runic.Application.Views.Codegen.ReactiveUI/Runic.Application.Views.Codegen.ReactiveUI.csproj":
    { libraries: ["ReactiveUI"], reason: "ReactiveUI generator plug-in" },
  "tools/Runic.Application.Views.Codegen.Toolkit/Runic.Application.Views.Codegen.Toolkit.csproj":
    { libraries: ["CommunityToolkit.Mvvm"], reason: "CommunityToolkit generator plug-in" },
  // The generator host composes the plug-ins through its Conventions/ modules. It
  // ships only as the private build tool under tools/ in Runic.Application.Views and is
  // never part of an application's compile or runtime closure.
  "tools/Runic.Application.Views.Codegen/Runic.Application.Views.Codegen.csproj":
    { libraries: ["ReactiveUI", "CommunityToolkit.Mvvm"], reason: "generator host for the plug-ins above" },
};

export function mvvmLibrary(id) {
  return mvvmLibraries.find(library => id.toLowerCase() === library.toLowerCase() || id.toLowerCase().startsWith(`${library.toLowerCase()}.`));
}

const uncommented = text => text.replace(/<!--[\s\S]*?-->/g, "");
const attribute = (tag, name) => tag.match(new RegExp(`\\b${name}\\s*=\\s*"([^"]*)"`))?.[1];
const elements = (text, names) => [...uncommented(text).matchAll(new RegExp(`<(${names})\\b[^>]*>`, "g"))].map(([tag, name]) => ({ name, tag }));

// The MVVM dependencies a project's own files declare: package and assembly
// references in the project and in its packed .props/.targets, and nuspec dependencies.
export function declaredDependencies(project, read, packagingFiles) {
  const found = [];
  for (const file of [project, ...packagingFiles]) {
    for (const { name, tag } of elements(read(file), "PackageReference|GlobalPackageReference|Reference|PackageDependency|dependency")) {
      const id = name === "dependency" ? attribute(tag, "id") : attribute(tag, "Include");
      const library = id && mvvmLibrary(id.split(",")[0].trim());
      if (library) found.push({ library, via: `${file === project ? "" : `${file}: `}${name} ${id}` });
    }
  }
  return found;
}

// Packages NuGet resolved for the project, including those that arrive through
// project references and third-party packages.
export function lockedDependencies(lockFile) {
  const found = [];
  for (const packages of Object.values(lockFile?.dependencies ?? {}))
    for (const [id, entry] of Object.entries(packages)) {
      const library = entry.type !== "Project" && mvvmLibrary(id);
      if (library) found.push({ library, via: `packages.lock.json ${entry.type} ${id}` });
    }
  return found;
}

export function projectReferences(project, read) {
  return elements(read(project), "ProjectReference")
    .map(({ tag }) => attribute(tag, "Include")?.replaceAll("\\", "/"))
    .filter(Boolean)
    .map(path => relative(root, resolve(root, dirname(project), path)).replaceAll("\\", "/"));
}

// Every violation in `projects`, as readable messages. `inspect(project)` returns
// { declared, locked, references } for one project.
export function mvvmLayeringErrors(projects, inspect, allowList = mvvmAllowList) {
  const errors = [];
  for (const path of Object.keys(allowList))
    if (!projects.includes(path)) errors.push(`${path} is in the MVVM allow-list but is not a shipped project; remove the entry.`);
  const cache = new Map();
  // The MVVM libraries a project depends on, with the first route to each.
  const closure = (project, stack = []) => {
    if (cache.has(project)) return cache.get(project);
    const { declared, locked, references } = inspect(project);
    const routes = new Map();
    for (const { library, via } of [...declared, ...locked]) if (!routes.has(library)) routes.set(library, via);
    for (const reference of references) {
      if (stack.includes(reference)) continue;
      for (const [library, via] of closure(reference, [...stack, project]))
        if (!routes.has(library)) routes.set(library, `ProjectReference ${reference} -> ${via}`);
    }
    cache.set(project, routes);
    return routes;
  };
  for (const project of projects) {
    const allowed = allowList[project]?.libraries ?? [];
    for (const [library, via] of closure(project))
      if (!allowed.includes(library))
        errors.push(`${project} depends on ${library} (${via}). General-purpose projects must stay free of MVVM libraries (D018.9): `
          + `move the library-specific code into an adapter such as Runic.Application.Views.ReactiveUI, or, for a new adapter, add it to mvvmAllowList in eng/mvvm-layering.test.mjs.`);
  }
  return errors;
}

// Shipped .NET projects: every tracked project under packages/ and tools/ except
// template payloads, which are applications.
export function shippedProjects() {
  const listed = spawnSync("git", ["ls-files", "--", "packages/*.csproj", "tools/*.csproj"], { cwd: root, encoding: "utf8" });
  assert.equal(listed.status, 0, listed.stderr);
  return listed.stdout.split("\n").filter(Boolean).filter(path => !/^tools\/[^/]+\/content\//.test(path)).sort();
}

function inspectProject(project) {
  const read = path => readFileSync(resolve(root, path), "utf8");
  const directory = dirname(project);
  const packaging = [];
  const walk = dir => {
    for (const entry of readdirSync(resolve(root, dir), { withFileTypes: true })) {
      const path = join(dir, entry.name).replaceAll("\\", "/");
      if (entry.isDirectory()) { if (!["bin", "obj", "node_modules", "content"].includes(entry.name)) walk(path); }
      else if (/\.(props|targets|nuspec)$/.test(entry.name)) packaging.push(path);
    }
  };
  walk(directory);
  const lock = resolve(root, directory, "packages.lock.json");
  return {
    declared: declaredDependencies(project, read, packaging),
    locked: existsSync(lock) ? lockedDependencies(JSON.parse(read(join(directory, "packages.lock.json")))) : [],
    references: projectReferences(project, read),
  };
}

test("only allow-listed adapters depend on ReactiveUI, System.Reactive or CommunityToolkit", () => {
  const projects = shippedProjects();
  const workspace = JSON.parse(readFileSync(resolve(root, "eng/workspace.json"), "utf8"));
  for (const { project } of workspace.nuget) assert.ok(projects.includes(project), `${project} is missing from the layering check`);
  // Shared build files apply to every project.
  const shared = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
    ...readdirSync(resolve(root, "eng/build")).filter(name => /\.(props|targets)$/.test(name)).map(name => `eng/build/${name}`)];
  const sharedRead = path => readFileSync(resolve(root, path), "utf8");
  for (const file of shared) assert.deepEqual(declaredDependencies(file, sharedRead, []), [], `${file} must not add an MVVM dependency to every project`);
  const errors = mvvmLayeringErrors(projects, inspectProject);
  assert.deepEqual(errors, [], `\n${errors.join("\n")}`);
});

test("the MVVM layering check reports direct, packaged, locked and transitive dependencies", () => {
  const files = {
    "packages/core/Core.csproj": '<Project><ItemGroup><PackageReference Include="Microsoft.Extensions.Logging.Abstractions" /></ItemGroup></Project>',
    "packages/direct/Direct.csproj": '<Project><ItemGroup><PackageReference Include="CommunityToolkit.Mvvm" /></ItemGroup></Project>',
    "packages/commented/Commented.csproj": '<Project><!-- <PackageReference Include="ReactiveUI" /> --></Project>',
    "packages/packed/Packed.csproj": "<Project />",
    "packages/packed/buildTransitive/Packed.targets": '<Project><ItemGroup><PackageReference Include="System.Reactive.Linq" /></ItemGroup></Project>',
    "packages/packed/Packed.nuspec": '<package><metadata><dependencies><dependency id="ReactiveUI.Primitives" version="9.0.0" /></dependencies></metadata></package>',
    "packages/adapter/Adapter.csproj": '<Project><ItemGroup><PackageReference Include="ReactiveUI" /></ItemGroup></Project>',
    "packages/consumer/Consumer.csproj": '<Project><ItemGroup><ProjectReference Include="..\\adapter\\Adapter.csproj" /></ItemGroup></Project>',
    "packages/locked/Locked.csproj": '<Project><ItemGroup><PackageReference Include="Some.Wrapper" /></ItemGroup></Project>',
  };
  const locks = {
    "packages/locked/Locked.csproj": { version: 2, dependencies: { "net10.0": {
      "Some.Wrapper": { type: "Direct" }, "ReactiveUI.Core": { type: "Transitive" }, "reactiveui.adapter": { type: "Project" } } } },
  };
  const read = path => files[path];
  const inspect = project => ({
    declared: declaredDependencies(project, read, Object.keys(files).filter(path => path !== project && !path.endsWith(".csproj") && path.startsWith(dirname(project) + "/"))),
    locked: lockedDependencies(locks[project]),
    references: projectReferences(project, read),
  });
  const projects = Object.keys(files).filter(path => path.endsWith(".csproj"));
  const allowList = { "packages/adapter/Adapter.csproj": { libraries: ["ReactiveUI"] }, "packages/gone/Gone.csproj": { libraries: ["ReactiveUI"] } };
  const errors = mvvmLayeringErrors(projects, inspect, allowList);
  const summary = errors.map(error => error.split(". General-purpose")[0]);
  assert.deepEqual(summary, [
    "packages/gone/Gone.csproj is in the MVVM allow-list but is not a shipped project; remove the entry.",
    "packages/direct/Direct.csproj depends on CommunityToolkit.Mvvm (PackageReference CommunityToolkit.Mvvm)",
    "packages/packed/Packed.csproj depends on System.Reactive (packages/packed/buildTransitive/Packed.targets: PackageReference System.Reactive.Linq)",
    "packages/packed/Packed.csproj depends on ReactiveUI (packages/packed/Packed.nuspec: dependency ReactiveUI.Primitives)",
    "packages/consumer/Consumer.csproj depends on ReactiveUI (ProjectReference packages/adapter/Adapter.csproj -> PackageReference ReactiveUI)",
    "packages/locked/Locked.csproj depends on ReactiveUI (packages.lock.json Transitive ReactiveUI.Core)",
  ]);
  assert.match(errors[1], /D018\.9.*mvvmAllowList in eng\/mvvm-layering\.test\.mjs/);
  assert.equal(mvvmLibrary("ReactiveUIExtras"), undefined, "a prefix without a dot is a different package");
  assert.equal(mvvmLibrary("communitytoolkit.mvvm"), "CommunityToolkit.Mvvm");
  assert.equal(mvvmLibrary("CommunityToolkit.Diagnostics"), undefined, "only the MVVM part of CommunityToolkit is an MVVM library");
});
