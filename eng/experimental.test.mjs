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
  assert.ok(attributes.length > 0);
  const missing = attributes.filter(({ args }) =>
    !/UrlFormat = (RunicReleaseLinks\.ExperimentalUrlFormat|WpfDiagnostics\.UrlFormat)$/.test(args));
  assert.deepEqual(missing, [], "Set UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat");
});

test("docs/experimental.md has an entry for every experimental diagnostic ID", () => {
  const ids = new Set(sources.flatMap(([, text]) => [
    ...[...text.matchAll(/const string \w*Id = "(RUNIC[A-Z]+\d{3})"/g)].map(match => match[1]),
    ...[...text.matchAll(/Experimental\("(RUNIC[A-Z]+\d{3})"/g)].map(match => match[1]),
  ]));
  assert.deepEqual([...ids].sort(), ["RUNICWPF001"]);
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

test("the WPF Views package suppresses its experimental ID only in intermediate .g.cs files", () => {
  for (const [project, id] of [["Runic.Application.Views.Wpf", "RUNICWPF001"]]) {
    const targets = readFileSync(resolve(root, `packages/dotnet/${project}/buildTransitive/${project}.targets`), "utf8");
    assert.match(targets, new RegExp(`dotnet_diagnostic\\.${id}\\.severity = none`));
    assert.equal([...targets.matchAll(/dotnet_diagnostic\./g)].length, 1, `${project} suppresses more than ${id}`);
    assert.match(targets, /StartsWith\('\$\(_\w+Intermediate\)'\)/, `${project} must limit the suppression to the intermediate directory`);
    const csproj = readFileSync(resolve(root, `packages/dotnet/${project}/${project}.csproj`), "utf8");
    assert.ok(csproj.includes(`<None Include="buildTransitive/${project}.targets" Pack="true" PackagePath="buildTransitive/" />`),
      `${project} does not pack its buildTransitive targets`);
  }
});
