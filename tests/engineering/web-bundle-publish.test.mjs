import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { join, relative } from "node:path";
import { after, before, test } from "node:test";
import { validate } from "../../eng/json-schema.mjs";
import { configuration, root } from "../../eng/run.mjs";

// D082, split CI, end to end on a real application: a copy of examples/first-window-desktop builds on Linux and writes
// a web bundle; then a framework-dependent win-x64 publish, with neither Node.js nor Bun on PATH, takes its page from
// the bundle, and a publish after a ViewModel change stops at the contract check. Embedded delivery, which these
// source-tree projects do not pack, is in web-bundle-targets.test.mjs. The copy lives below artifacts/, so
// the repository's build files, packages and root node_modules apply as to the example.
const skip = process.platform === "win32" ? "the no-Node PATH is built for Linux and macOS" : false;
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

// A PATH with dotnet, sh and git but neither node nor bun, as a Windows runner without Node.js has.
function withoutNode() {
  const bin = join(work, "no-node-bin");
  if (!existsSync(bin)) {
    mkdirSync(bin, { recursive: true });
    for (const tool of ["dotnet", "sh", "git"]) {
      const path = spawnSync("sh", ["-c", `command -v ${tool}`], { encoding: "utf8" }).stdout.trim();
      if (path) symlinkSync(path, join(bin, tool));
    }
  }
  for (const tool of ["node", "bun"])
    assert.notEqual(spawnSync("sh", ["-c", `command -v ${tool}`], { env: { PATH: bin }, encoding: "utf8" }).status, 0, `${tool} is on the test PATH`);
  return { PATH: bin };
}

const publish = (output, { properties = [], env = {} } = {}) =>
  dotnet(["publish", project, "-c", configuration, "-r", "win-x64", "--self-contained", "false", "-o", join(work, output), ...properties.map(property => `-p:${property}`)],
    { env: { ...withoutNode(), ...env } });

test("a win-x64 publish takes the page from the bundle with neither Node.js nor Bun on PATH", { skip }, () => {
  // The prebuilt directory as an environment variable, relative to where the publish starts, as dotnet runic package
  // can pass it before it has an option of its own.
  const result = publish("out-win", { env: { RunicApplicationFrontendPrebuiltDirectory: "bundle" } });
  assert.equal(result.status, 0, result.output);
  assert.match(result.output, /Web bundle: FirstWindowDesktop takes its frontend from .*bundle\/ \(1 View matches\)/);
  assert.doesNotMatch(result.output, /RUNICBRIDGE084|warning RUNICBRIDGE/);
  const www = join(work, "out-win/www");
  const page = files(join(bundle, "projects/FirstWindowDesktop/www"));
  assert.deepEqual(files(www), [...page, "runic-views.js"].sort());
  for (const path of page) assert.equal(readFileSync(join(www, path), "utf8"), readFileSync(join(bundle, "projects/FirstWindowDesktop/www", path), "utf8"));
  assert.ok(existsSync(join(work, "out-win/FirstWindowDesktop.exe")));
});

test("RUNICBRIDGE094: a publish after a ViewModel change stops at the check, naming the View, unless it is a warning", { skip }, () => {
  const model = join(app, "CounterViewModel.cs");
  const source = readFileSync(model, "utf8");
  const bundled = JSON.parse(readFileSync(join(bundle, "web-bundle.json"), "utf8")).projects[0].contracts.views[0].fingerprint;
  try {
    writeFileSync(model, source.replace("    public int Count\n", "    public string Note { get; set; } = \"\";\n\n    public int Count\n"));
    let result = publish("out-changed", { properties: [`RunicApplicationFrontendPrebuiltDirectory=${bundle}`] });
    assert.notEqual(result.status, 0);
    const error = result.output.split("\n").find(line => line.includes("error RUNICBRIDGE094:"));
    assert.match(error, new RegExp(`FirstWindowDesktop\\.CounterViewModel is [0-9A-F]{64} in this build and ${bundled} in the bundle\\.`));
    assert.doesNotMatch(error, new RegExp(`is ${bundled} in this build`));
    assert.equal(existsSync(join(work, "out-changed/www")), false);

    result = publish("out-changed", { properties: [`RunicApplicationFrontendPrebuiltDirectory=${bundle}`, "RunicApplicationFrontendPrebuiltCheck=warning"] });
    assert.equal(result.status, 0, result.output);
    assert.match(result.output, /warning RUNICBRIDGE094: .*FirstWindowDesktop\.CounterViewModel is /);
    assert.ok(existsSync(join(work, "out-changed/www/index.html")));
  } finally {
    writeFileSync(model, source);
  }
});
