import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { after, before, test } from "node:test";
import { validate } from "../../eng/json-schema.mjs";
import { configuration, root } from "../../eng/run.mjs";

// D082, split CI: the Views targets write a web bundle (rax.web-bundle/1, specs/application/web-bundle.md) with
// RunicApplicationFrontendBundleDirectory. These tests run the targets on small projects that import them, with a
// contract IR, generated clients and a built page written as the generator and the frontend build would, and run the
// generator tool, which writes the bundle on .NET alone.
const targets = resolve(root, "packages/dotnet/Runic.Application.Views/build/Runic.Application.Views.targets");
const schema = JSON.parse(readFileSync(resolve(root, "specs/application/web-bundle/web-bundle.schema.json"), "utf8"));
let directory;

before(() => {
  directory = mkdtempSync(join(tmpdir(), "runic-web-bundle-"));
  // The targets run the generator tool from its build output, as a source-tree build does.
  const build = spawnSync("dotnet", ["build", resolve(root, "tools/Runic.Application.Views.Codegen"), "-c", configuration,
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:q", "-clp:NoSummary"], { cwd: root, encoding: "utf8" });
  assert.equal(build.status, 0, build.stdout + build.stderr);
});

after(() => {
  rmSync(directory, { recursive: true, force: true });
});

const fingerprint = text => createHash("sha256").update(text).digest("hex").toUpperCase();

// The Microsoft.NET.Sdk targets that RunicViewsCollectReferencedFrontends depends on.
const sdkStubs = `  <Target Name="AssignProjectConfiguration" />
  <Target Name="_GetProjectReferenceTargetFrameworkProperties" />
  <Target Name="_SplitProjectReferencesByFileExistence">
    <ItemGroup><_MSBuildProjectReferenceExistent Include="@(ProjectReference)" /></ItemGroup>
  </Target>`;

// A project with a frontend whose build has run: dist/ (unless page is null), the generated clients and their record,
// and the contract IR of its Views.
function project(solution, name, { kind = "application", host = "", views = [`${name}.CounterViewModel`], page = { "index.html": `<p>${name}</p>` },
  references = [], properties = "" } = {}) {
  const folder = join(solution, name);
  const frontend = join(folder, "frontend");
  const generated = join(frontend, "src/generated");
  const bridge = join(folder, "obj", configuration, "net10.0", "runic-bridge");
  mkdirSync(generated, { recursive: true });
  mkdirSync(bridge, { recursive: true });
  writeFileSync(join(frontend, "package.json"), JSON.stringify({ name: name.toLowerCase(), private: true }));
  writePage(folder, page);
  writeIr(folder, name, views, kind === "clientLibrary");
  const client = join(generated, "counter.ts");
  writeFileSync(client, `export const contract = "${name}";\n`);
  writeFileSync(join(bridge, "typescript.outputs"), `${createHash("sha256").update(readFileSync(client)).digest("hex")} ${client}\n`);
  const outputType = kind === "application" ? "Exe" : "Library";
  writeFileSync(join(folder, `${name}.proj`), `<Project>
  <PropertyGroup>
    <RunicApplicationFrontendGenerationEnabled>true</RunicApplicationFrontendGenerationEnabled>
    <RunicApplicationFrontendDirectory>$(MSBuildProjectDirectory)/frontend</RunicApplicationFrontendDirectory>
    <RunicApplicationFrontendBuildCommand>node build.mjs</RunicApplicationFrontendBuildCommand>
    <Configuration>${configuration}</Configuration>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>${outputType}</OutputType>
    <TargetDir>$(MSBuildProjectDirectory)/bin/</TargetDir>
${kind === "clientLibrary" ? "    <RunicApplicationFrontendClientLibrary>true</RunicApplicationFrontendClientLibrary>\n" : ""}${host ? `    <RunicApplicationHost>${host}</RunicApplicationHost>\n` : ""}${properties}
  </PropertyGroup>
  <ItemGroup>
${references.map(reference => `    <ProjectReference Include="../${reference}/${reference}.proj" />`).join("\n")}
  </ItemGroup>
${sdkStubs}
  <Import Project="${targets}" />
</Project>
`);
  return { folder, name, file: join(folder, `${name}.proj`) };
}

function writePage(folder, page) {
  const dist = join(folder, "frontend/dist");
  rmSync(dist, { recursive: true, force: true });
  if (!page) return;
  for (const [path, text] of Object.entries(page)) {
    mkdirSync(join(dist, path, ".."), { recursive: true });
    writeFileSync(join(dist, path), text);
  }
}

function writeIr(folder, unit, views, clientLibrary = false) {
  const ir = {
    format: "runic.views.contract-ir/1",
    producer: { language: "csharp", tool: "test", unit, ...(clientLibrary ? { clientLibrary: true } : {}) },
    views: views.map(view => typeof view === "string" ? { id: view, fingerprint: fingerprint(view) } : view)
      .map(({ id, fingerprint }) => ({ id, name: id.split(".").at(-1).replace(/ViewModel$/, ""), fingerprint, kinds: ["default"], members: [], commands: [] })),
    types: {}, sources: {},
  };
  writeFileSync(join(folder, "obj", configuration, "net10.0", "runic-bridge", "contract.ir.json"), JSON.stringify(ir));
}

function msbuild(project, { targets: run, properties = [], env = {}, cwd } = {}) {
  const result = spawnSync("dotnet", ["msbuild", project.file, `-t:${run}`, ...properties.map(property => `-p:${property}`),
    "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary"],
    // bun test sets NODE_ENV=test, which the bundle would record.
    { cwd: cwd ?? project.folder, encoding: "utf8", env: { ...process.env, NODE_ENV: "", ...env } });
  return { status: result.status, output: `${result.stdout}${result.stderr}` };
}

const write = (project, bundle, options = {}) =>
  msbuild(project, { targets: "RunicViewsWriteWebBundle", ...options, properties: [`RunicApplicationFrontendBundleDirectory=${bundle}`, ...(options.properties ?? [])] });

// Errors and warnings as MSBuild logs them, once each, without the project suffix.
const reported = (output, kind) => [...new Set(output.split("\n").filter(line => new RegExp(`^\\S.* ${kind} [A-Z]+\\d+:`).test(line))
  .map(line => line.replace(/\s*\[[^\]]*\]\s*$/, "").trim()))];

const manifest = bundle => JSON.parse(readFileSync(join(bundle, "web-bundle.json"), "utf8"));
const files = folder => existsSync(folder) ? readdirSync(folder, { recursive: true, withFileTypes: true }).filter(item => item.isFile())
  .map(item => join(item.parentPath, item.name).slice(folder.length + 1).replaceAll("\\", "/")).sort() : [];

test("a solution's projects write their entries side by side, and web-bundle.json lists every one", () => {
  const solution = join(directory, "solution");
  // A WPF application's model project (a library with a page), a client library (a package, no page) and the application.
  const model = project(solution, "Acme.Model", { kind: "library", page: { "model.html": "<p>model</p>" } });
  const shared = project(solution, "Acme.Shared", { kind: "clientLibrary", page: { "index.js": "export {};" },
    properties: "    <RunicApplicationFrontendBuildEnabled>false</RunicApplicationFrontendBuildEnabled>" });
  const app = project(solution, "Acme.App", { host: "desktop", views: ["Acme.App.CounterViewModel", "Acme.Shared.AboutViewModel"],
    page: { "index.html": "<p>app</p>", "assets/index-1.js": "export {};" }, references: ["Acme.Model", "Acme.Shared"],
    properties: "    <Optimize>true</Optimize>" });
  // A relative directory is relative to where the build starts, whichever project writes.
  for (const item of [model, shared, app]) {
    const result = write(item, "bundle", { cwd: solution });
    assert.equal(result.status, 0, result.output);
    assert.match(result.output, new RegExp(`Web bundle: ${item.name.replaceAll(".", "\\.")} -> `));
  }
  const bundle = join(solution, "bundle");
  const written = manifest(bundle);
  assert.deepEqual(validate(schema, written), []);
  assert.equal(written.format, "rax.web-bundle/1");
  assert.equal(written.producer.name, "Runic.Application.Views");
  assert.deepEqual(written.projects.map(entry => [entry.assembly, entry.kind]),
    [["Acme.App", "application"], ["Acme.Model", "library"], ["Acme.Shared", "clientLibrary"]]);

  const [appEntry, modelEntry, sharedEntry] = written.projects;
  assert.equal(appEntry.host, "desktop");
  assert.equal(appEntry.nodeEnv, "production", "an optimized build's page is a production build");
  assert.equal(modelEntry.nodeEnv, undefined);
  assert.deepEqual(appEntry.contracts.views, [
    { id: "Acme.App.CounterViewModel", fingerprint: fingerprint("Acme.App.CounterViewModel") },
    { id: "Acme.Shared.AboutViewModel", fingerprint: fingerprint("Acme.Shared.AboutViewModel") },
  ]);
  assert.equal(appEntry.contracts.irSha256,
    createHash("sha256").update(readFileSync(join(app.folder, "obj", configuration, "net10.0", "runic-bridge", "contract.ir.json"))).digest("hex"));
  assert.equal(appEntry.frontend.buildCommand, "node build.mjs");
  assert.equal(sharedEntry.frontend.buildCommand, undefined, "the client library's package build is off");

  // The page as dist/ has it, hashed as sha256sum lists it; a client library has none.
  assert.deepEqual(files(join(bundle, "projects/Acme.App/www")), ["assets/index-1.js", "index.html"]);
  assert.deepEqual(files(join(bundle, "projects/Acme.Model/www")), ["model.html"]);
  assert.equal(existsSync(join(bundle, "projects/Acme.Shared/www")), false);
  assert.equal(sharedEntry.www, undefined);
  const listing = ["assets/index-1.js", "index.html"]
    .map(path => `${createHash("sha256").update(readFileSync(join(bundle, "projects/Acme.App/www", path))).digest("hex")}  ${path}\n`).join("");
  assert.deepEqual(appEntry.files, { count: 2, sha256: createHash("sha256").update(listing).digest("hex") });
  // The generated clients, for a job that tests the frontend alone.
  assert.equal(readFileSync(join(bundle, "projects/Acme.Shared/clients/counter.ts"), "utf8"), 'export const contract = "Acme.Shared";\n');
  assert.equal(sharedEntry.clients, "projects/Acme.Shared/clients");
  // The fragments are the entries; nothing else is left in the bundle.
  assert.deepEqual(readdirSync(bundle).sort(), ["projects", "web-bundle.json"]);
  assert.deepEqual(JSON.parse(readFileSync(join(bundle, "projects/Acme.App.json"), "utf8")), appEntry);

  // A second write replaces the project's own entry: a file the page no longer has is gone.
  writePage(app.folder, { "index.html": "<p>app 2</p>" });
  assert.equal(write(app, bundle).status, 0);
  assert.deepEqual(files(join(bundle, "projects/Acme.App/www")), ["index.html"]);
  assert.equal(manifest(bundle).projects.length, 3);
});

test("projects that write at the same time all end up in web-bundle.json", async () => {
  const solution = join(directory, "parallel");
  const projects = Array.from({ length: 4 }, (_, index) => project(solution, `Parallel${index}`));
  const bundle = join(solution, "bundle");
  const results = await Promise.all(projects.map(item => new Promise(done => {
    const child = spawn("dotnet", ["msbuild", item.file, "-t:RunicViewsWriteWebBundle", `-p:RunicApplicationFrontendBundleDirectory=${bundle}`,
      "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m"], { cwd: item.folder, env: { ...process.env, NODE_ENV: "" } });
    let output = "";
    child.stdout.on("data", data => output += data);
    child.stderr.on("data", data => output += data);
    child.on("close", status => done({ status, output }));
  })));
  for (const result of results) assert.equal(result.status, 0, result.output);
  assert.deepEqual(manifest(bundle).projects.map(entry => entry.assembly), projects.map(item => item.name));
});

test("RUNICBRIDGE092: a project whose frontend build is off, or that wrote no page, writes no bundle", () => {
  const solution = join(directory, "build-off");
  const app = project(solution, "Off.App", { properties: "    <RunicApplicationFrontendBuildEnabled>false</RunicApplicationFrontendBuildEnabled>" });
  let result = write(app, join(solution, "bundle"));
  assert.notEqual(result.status, 0);
  assert.deepEqual(reported(result.output, "error").map(line => line.replace(/^.*?: error/, "error")), [
    "error RUNICBRIDGE092: RunicApplicationFrontendBundleDirectory asks for a web bundle, but Off.App does not build its frontend in this build (RunicApplicationFrontendBuildEnabled is false), so the bundle would carry an old page or none. Write the bundle with a build that builds the frontend (dotnet build -c Release -p:RunicApplicationFrontendBundleDirectory=...), without dotnet runic dev running.",
  ]);
  assert.equal(existsSync(join(solution, "bundle/web-bundle.json")), false);

  // dotnet runic dev serves the page.
  const served = project(solution, "Served.App");
  result = write(served, join(solution, "bundle"), { properties: [`RunicApplicationFrontendDevSessionProject=${served.file}`] });
  assert.match(result.output, /error RUNICBRIDGE092: .*\(dotnet runic dev serves its frontend\)/);

  // A frontend build that wrote nothing.
  const empty = project(solution, "Empty.App", { page: null });
  result = write(empty, join(solution, "bundle"));
  assert.notEqual(result.status, 0);
  assert.match(result.output, /error RUNICBRIDGE092: Empty\.App's frontend build wrote no files to .*dist/);
});
