import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";
import { root } from "./run.mjs";

// Every [Experimental] diagnostic links to its entry in docs/experimental.md at the release
// tag (W250-015). The URL itself is built by eng/build/release-links.targets.
const sources = execFileSync("git", ["ls-files", "packages/dotnet/*.cs"], { cwd: root, encoding: "utf8" })
  .trim().split("\n").map(file => [file, readFileSync(resolve(root, file), "utf8")]);
const docs = readFileSync(resolve(root, "docs/experimental.md"), "utf8");

test("every Experimental attribute sets a documentation UrlFormat", () => {
  const attributes = sources.flatMap(([file, text]) =>
    [...text.matchAll(/\[(?:System\.Diagnostics\.CodeAnalysis\.)?Experimental\(([^\]]*)\)\]/g)].map(([, args]) => ({ file, args })));
  const missing = attributes.filter(({ args }) =>
    !/UrlFormat = RunicReleaseLinks\.ExperimentalUrlFormat$/.test(args));
  assert.deepEqual(missing, [], "Set UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat");
});

test("docs/experimental.md has an entry for every experimental diagnostic ID, or says there is none", () => {
  const ids = new Set(sources.flatMap(([, text]) => [
    ...[...text.matchAll(/const string \w*Id = "(RUNIC[A-Z]+\d{3})"/g)].map(match => match[1]),
    ...[...text.matchAll(/Experimental\("(RUNIC[A-Z]+\d{3})"/g)].map(match => match[1]),
  ]));
  // RUNICASSETS001, the last .NET family, graduated in 0.10.0 (D029.3); a new family adds its ID here.
  assert.deepEqual([...ids].sort(), []);
  if (ids.size === 0) assert.match(docs, /^No \.NET API is experimental\./m);
  for (const id of ids) {
    // The anchor keeps the ID's case, which the compiler substitutes into UrlFormat.
    assert.ok(docs.includes(`<a id="${id}"></a>`), `docs/experimental.md has no anchor for ${id}`);
    assert.match(docs, new RegExp(`^## ${id}$`, "m"), `docs/experimental.md has no section for ${id}`);
  }
});

test("experimental help links name the release tag, not main", () => {
  const targets = readFileSync(resolve(root, "eng/build/release-links.targets"), "utf8");
  assert.match(targets, /<RunicReleaseTag [^>]*>v\$\(PackageVersion\)<\/RunicReleaseTag>/);
  assert.match(targets, /<RunicReleaseSourceUrl [^>]*>\$\(RepositoryUrl\)\/blob\/\$\(RunicReleaseTag\)\/<\/RunicReleaseSourceUrl>/);
  assert.match(targets, /<RunicExperimentalHelpLinkBase [^>]*>\$\(RunicReleaseSourceUrl\)docs\/experimental\.md#<\/RunicExperimentalHelpLinkBase>/);
});

// A graduated family's ID is never reused: it stays listed as retired and no source marks an API with it.
const retired = ["RUNICASSETS001", "RUNICDESK100", "RUNICDESK101", "RUNICDESK102", "RUNICNAV001", "RUNICWPF001"];

test("graduated diagnostic IDs are retired, not reused", () => {
  const section = docs.slice(docs.indexOf('<a id="retired"></a>'));
  assert.ok(section.length < docs.length, "docs/experimental.md has no retired-ID section");
  for (const id of retired) {
    assert.ok(!sources.some(([, text]) => text.includes(`"${id}"`)), `${id} is retired but a source still uses it`);
    assert.match(section, new RegExp(`\\| \`${id}\` \\|`), `docs/experimental.md does not list ${id} as retired`);
    assert.doesNotMatch(docs, new RegExp(`^## ${id}$`, "m"), `docs/experimental.md still has a section for ${id}`);
  }
});

test("the WPF Views package graduated: no experimental attribute and no suppression targets", () => {
  const project = "Runic.Application.Views.Wpf";
  const files = execFileSync("git", ["ls-files", `packages/dotnet/${project}`], { cwd: root, encoding: "utf8" }).trim().split("\n");
  assert.ok(!files.includes(`packages/dotnet/${project}/buildTransitive/${project}.targets`), "the RUNICWPF001 suppression targets came back");
  const csproj = readFileSync(resolve(root, `packages/dotnet/${project}/${project}.csproj`), "utf8");
  assert.doesNotMatch(csproj, /RUNICWPF001|\.targets" Pack/);
  // RunicWebView.CreateWindowHostFactory() graduated with Runic.Desktop's custom window hosts (RUNICDESK100) in 0.10.0.
  const marked = sources.filter(([file]) => file.startsWith(`packages/dotnet/${project}/`))
    .flatMap(([file, text]) => [...text.matchAll(/\[Experimental\("(\w+)"[^\]]*\]\s*(?:\/\/\/.*\s*)*public [^(]*?(\w+)\(/g)]
      .map(([, id, member]) => `${file.split("/").pop()} ${member} ${id}`));
  assert.deepEqual(marked, []);
});

// D029.4: every npm `./experimental` export graduated in 0.10.0. A new npm experiment adds the
// subpath and its docs/experimental.md entry together; until then no package has one.
test("no npm package has an ./experimental entry, and the docs say so", () => {
  const manifests = execFileSync("git", ["ls-files", "packages/web/*/package.json"], { cwd: root, encoding: "utf8" })
    .trim().split("\n");
  assert.ok(manifests.length >= 6);
  for (const file of manifests) {
    const manifest = JSON.parse(readFileSync(resolve(root, file), "utf8"));
    assert.equal("./experimental" in (manifest.exports ?? {}), false, `${file} still exports ./experimental`);
    const source = resolve(root, file.replace(/package\.json$/, "src/experimental.ts"));
    assert.equal(execFileSync("git", ["ls-files", source], { cwd: root, encoding: "utf8" }).trim(), "", `${source} still exists`);
  }
  const section = docs.slice(docs.indexOf('<a id="npm-packages"></a>'));
  assert.match(section, /No npm API is experimental/);
});
