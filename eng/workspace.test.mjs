import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { root, workspace, affectedComponents } from "./run.mjs";

const json = (path) => JSON.parse(readFileSync(resolve(root, path), "utf8"));
test("workspace contains every SDK artifact with unchanged package identities", () => {
  const canonical = json("eng/release/runic.compatibility-set.json").packages;
  const names = [...workspace.npm, ...workspace.nuget].map((p) => p.name);
  assert.equal(new Set(names).size, names.length);
  const authorityNames = canonical.map(
    (p) => p.name ?? p.identity ?? p.packageId,
  );
  for (const name of authorityNames) assert.ok(names.includes(name), `Imported identity removed: ${name}`);
  assert.deepEqual(names.filter(name => !authorityNames.includes(name)), ["Runic.Application.CsWebUi"]);
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
      !path.startsWith("tests/fixtures/legacy-examples/samples/"),
      "historical examples must remain excluded",
    );
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
test("all imported source histories remain ancestors of the monorepo", () => {
  for (const source of json("eng/migration/imports.json").sources) {
    assert.equal(
      spawnSync("git", ["merge-base", "--is-ancestor", source.head, "HEAD"], {
        cwd: root,
      }).status,
      0,
      source.repository,
    );
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

test("relocated artifacts have exactly one component owner", () => {
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
test("affected detection follows relocated code and its dependents", () => {
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

test("development workspaces and CI exclude imported engineering archives", () => {
  for (const path of json("package.json").workspaces) {
    assert.ok(
      /^(packages\/web\/|apps\/|docs$|examples\/(counter|customer-migration)\/)/.test(
        path,
      ),
      `unexpected development workspace: ${path}`,
    );
  }
  const files = spawnSync("git", ["ls-files"], { cwd: root, encoding: "utf8" });
  assert.equal(files.status, 0);
  for (const path of files.stdout.trim().split("\n")) {
    if (path.startsWith("eng/archive/")) continue;
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

test("solution projects do not import archived engineering files", () => {
  const solution = readFileSync(resolve(root, "RunicSdk.slnx"), "utf8");
  for (const [, path] of solution.matchAll(/<Project Path="([^"]+)"/g)) {
    assert.ok(!path.startsWith("eng/archive/"), path);
    const project = readFileSync(resolve(root, path), "utf8");
    assert.doesNotMatch(
      project,
      /(?:eng[\\/]archive|packages[\\/]runic-)/,
      path,
    );
  }
});
