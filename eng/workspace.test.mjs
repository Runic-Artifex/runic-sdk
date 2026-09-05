import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { root, workspace } from "./run.mjs";

const json = (path) => JSON.parse(readFileSync(resolve(root, path), "utf8"));
test("workspace contains every SDK artifact with unchanged package identities", () => {
  const canonical = json("eng/release/runic.compatibility-set.json").packages;
  const names = [...workspace.npm, ...workspace.nuget].map((p) => p.name);
  assert.equal(new Set(names).size, 27);
  const authorityNames = canonical.map(
    (p) => p.name ?? p.identity ?? p.packageId,
  );
  assert.deepEqual([...names].sort(), authorityNames.sort());
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
      !path.startsWith("examples/samples/"),
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
