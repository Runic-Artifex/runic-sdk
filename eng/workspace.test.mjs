import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { root, workspace, affectedComponents } from "./run.mjs";

const json = (path) => JSON.parse(readFileSync(resolve(root, path), "utf8"));
test("workspace defines the complete public SDK package inventory", () => {
  const names = [...workspace.npm, ...workspace.nuget].map(p => p.name);
  assert.equal(workspace.nuget.length, 32);
  assert.equal(workspace.npm.length, 8);
  assert.equal(new Set(names).size, names.length);
  for (const p of workspace.npm) assert.ok(p.name.startsWith("@runic-artifex/"), p.name);
  for (const p of workspace.nuget) {
    assert.match(p.name, /^(?:Runic\.|dotnet-runic(?:$|-))/);
    assert.doesNotMatch(p.name, /(?:Generators?|Inspector|Packer|Compiler)$/,
      "Embedded implementation tools must not become public packages");
  }
  for (const p of workspace.npm) {
    const manifest = json(`${p.path}/package.json`);
    assert.equal(manifest.name, p.name);
    assert.equal(manifest.version, workspace.version);
  }
  for (const p of workspace.nuget)
    assert.ok(existsSync(resolve(root, p.project)));
});
test("active npm consumers resolve internal dependencies from the workspace", () => {
  const paths = json("package.json").workspaces;
  const names = new Set(workspace.npm.map((p) => p.name));
  for (const path of paths) {
    assert.ok(
      !existsSync(resolve(root, path, "bun.lock")),
      `${path} owns a competing lockfile`,
    );
    const manifest = json(`${path}/package.json`);
    for (const [name, version] of Object.entries({
      ...manifest.dependencies,
      ...manifest.devDependencies,
    })) {
      if (names.has(name))
        assert.equal(version, "workspace:*", `${path}: ${name}`);
    }
  }
});
test("component dependency graph is closed and acyclic", () => {
  const visiting = new Set();
  const complete = new Set();
  function visit(name) {
    assert.ok(workspace.components[name], `unknown component ${name}`);
    assert.ok(!visiting.has(name), `dependency cycle at ${name}`);
    if (complete.has(name)) return;
    visiting.add(name);
    for (const dependency of workspace.components[name].dependsOn)
      visit(dependency);
    visiting.delete(name);
    complete.add(name);
  }
  for (const name of Object.keys(workspace.components)) visit(name);
});

test("SDK artifacts have exactly one component owner", () => {
  for (const artifact of [...workspace.npm, ...workspace.nuget]) {
    const path = artifact.path ?? artifact.project;
    const owners = Object.entries(workspace.components).filter(
      ([, component]) =>
        component.paths.some(
          (prefix) => path === prefix || path.startsWith(`${prefix}/`),
        ),
    );
    assert.equal(owners.length, 1, `${path}: ${owners.map(([name]) => name)}`);
  }
});
test("affected detection follows component code and its dependents", () => {
  assert.deepEqual(
    affectedComponents([
      "packages/dotnet/Runic.Desktop/DesktopSurface.cs",
    ]).sort(),
    [
      "desktop",
      "assets",
      "application",
      "vite",
      "svelte",
      "editor",
      "examples",
      "platform",
    ].sort(),
  );
  assert.deepEqual(
    affectedComponents(["eng/build/desktop.props"]).sort(),
    affectedComponents([
      "packages/dotnet/Runic.Desktop/DesktopSurface.cs",
    ]).sort(),
  );
  assert.deepEqual(
    affectedComponents(["Directory.Build.props"]).sort(),
    Object.keys(workspace.components).sort(),
  );
});

test("development workspaces and workflows use the SDK layout", () => {
  for (const path of json("package.json").workspaces) {
    assert.ok(
      /^(packages\/web\/|apps\/|docs$|examples\/(counter|customer-migration|document-migration)\/)/.test(
        path,
      ),
      `unexpected development workspace: ${path}`,
    );
  }
  const files = spawnSync("git", ["ls-files"], { cwd: root, encoding: "utf8" });
  assert.equal(files.status, 0);
  for (const path of files.stdout.trim().split("\n")) {
    if (!existsSync(resolve(root, path))) continue;
    assert.ok(
      !path.startsWith("packages/runic-"),
      `retired package root: ${path}`,
    );
    assert.ok(
      !path.includes("/.github/workflows/"),
      `nested active CI: ${path}`,
    );
  }
});

test("solution projects use the maintained SDK layout", () => {
  const solution = readFileSync(resolve(root, "RunicSdk.slnx"), "utf8");
  for (const [, path] of solution.matchAll(/<Project Path="([^"]+)"/g)) {
    assert.match(path, /^(?:packages\/dotnet|tools|tests|examples|apps)\//);
    const project = readFileSync(resolve(root, path), "utf8");
    assert.doesNotMatch(
      project,
      /packages[\\/]runic-/,
      path,
    );
  }
});
