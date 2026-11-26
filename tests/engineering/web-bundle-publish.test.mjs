import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, relative } from "node:path";
import { after, before, test } from "node:test";
import { validate } from "../../eng/json-schema.mjs";
import { configuration, root } from "../../eng/run.mjs";

// D082, split CI, end to end on a real application: a copy of examples/first-window-desktop builds on Linux and writes
// a web bundle. The copy lives below artifacts/, so the repository's build files, packages and root node_modules apply
// as to the example.
const skip = process.platform === "win32" ? "runs on Linux and macOS, as CI's engineering job" : false;
const work = join(root, "artifacts", `web-bundle-test-${process.pid}`);
const app = join(work, "first-window-desktop");
const project = join(app, "FirstWindowDesktop.csproj");
const bundle = join(work, "bundle");
const schema = JSON.parse(readFileSync(join(root, "specs/application/web-bundle/web-bundle.schema.json"), "utf8"));

before(() => {
  if (skip) return;
  const source = join(root, "examples/first-window-desktop");
  cpSync(source, app, {
    recursive: true,
    filter: path => !["bin", "obj", "node_modules", "dist", "packages.lock.json"].includes(relative(source, path).split(/[\\/]/).at(-1)),
  });
  // The example references the SDK's projects and targets relative to examples/.
  writeFileSync(project, readFileSync(project, "utf8").replaceAll("../../packages/", `${join(root, "packages")}/`));
});

after(() => {
  rmSync(work, { recursive: true, force: true });
});

function dotnet(args, { env = {}, cwd = work } = {}) {
  // bun test sets NODE_ENV=test, which the frontend build would receive.
  const result = spawnSync("dotnet", [...args, "-nologo", "-nodeReuse:false", "-v:m", "-clp:NoSummary"],
    { cwd, encoding: "utf8", env: { ...process.env, NODE_ENV: "", ...env } });
  return { status: result.status, output: `${result.stdout}${result.stderr}` };
}

const files = folder => existsSync(folder) ? readdirSync(folder, { recursive: true, withFileTypes: true }).filter(item => item.isFile())
  .map(item => join(item.parentPath, item.name).slice(folder.length + 1).replaceAll("\\", "/")).sort() : [];

test("a build writes the application's web bundle, which the schema accepts", { skip }, () => {
  mkdirSync(work, { recursive: true });
  const result = dotnet(["build", project, "-c", configuration, `-p:RunicApplicationFrontendBundleDirectory=${bundle}`]);
  assert.equal(result.status, 0, result.output);
  const manifest = JSON.parse(readFileSync(join(bundle, "web-bundle.json"), "utf8"));
  assert.deepEqual(validate(schema, manifest), []);
  const [entry] = manifest.projects;
  assert.equal(manifest.projects.length, 1);
  assert.equal(entry.assembly, "FirstWindowDesktop");
  assert.equal(entry.host, "desktop");
  assert.equal(entry.project, relative(root, project).replaceAll("\\", "/"));
  // The fingerprints are the contract IR's that this build wrote.
  const ir = JSON.parse(readFileSync(join(app, "obj", configuration, "net10.0", "runic-bridge", "contract.ir.json"), "utf8"));
  assert.deepEqual(entry.contracts.views, ir.views.map(view => ({ id: view.id, fingerprint: view.fingerprint })));
  assert.deepEqual(entry.contracts.views.map(view => view.id), ["FirstWindowDesktop.CounterViewModel"]);
  assert.deepEqual(files(join(bundle, entry.www)), files(join(app, "Frontend/dist")));
  assert.ok(files(join(bundle, entry.clients)).includes("counter.ts"));
});
