import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { test } from "node:test";
import { root } from "../../eng/run.mjs";

// The Nix workflow is path-filtered. Its flake checks build the SDK examples from the sources that
// nix/examples.nix lists, and the builder's fixtures that nix/tests names, so a change to any of them must run it.
const workflow = readFileSync(resolve(root, ".github/workflows/nix.yml"), "utf8");

function filters(event) {
  const match = workflow.match(new RegExp(`\\n  ${event}:\\n(?:    branches: .*\\n)?    paths: \\[([^\\]]*)\\]`));
  assert.ok(match, `nix.yml has no ${event} paths`);
  return [...match[1].matchAll(/'([^']+)'/g)].map(([, pattern]) => pattern);
}

// GitHub's path filters: * matches within a directory, ** across them, and a later ! pattern excludes.
function matches(patterns, file) {
  let included = false;
  for (const pattern of patterns) {
    const negated = pattern.startsWith("!");
    const glob = negated ? pattern.slice(1) : pattern;
    const expression = new RegExp(`^${glob.replace(/[.+^${}()|[\]\\]/g, "\\$&")
      .replace(/\*\*\/?/g, "\u0000").replace(/\*/g, "[^/]*").replace(/\u0000/g, ".*")}$`);
    if (expression.test(file)) included = !negated;
  }
  return included;
}

// The relative paths in the Nix files the flake checks build, as paths from the repository root.
const nixFiles = ["nix/examples.nix", ...readdirSync(resolve(root, "nix/tests")).filter(name => name.endsWith(".nix")).map(name => `nix/tests/${name}`)];
const sources = [...new Set(nixFiles.flatMap(file => [...readFileSync(resolve(root, file), "utf8")
  .matchAll(/(?:\.\.\/)+[A-Za-z0-9_-][A-Za-z0-9._/-]*/g)]
  .map(([path]) => relative(root, resolve(root, dirname(file), path.replace(/\/$/, ""))))))].filter(path => path !== "");

test("the Nix workflow runs on main for every source of the packaged examples", () => {
  assert.ok(sources.length > 10, `nix/examples.nix lists ${sources.length} sources`);
  const patterns = filters("push");
  for (const source of sources) {
    // A directory is covered when a file in it is.
    const file = statSync(resolve(root, source)).isDirectory() ? `${source}/Project.csproj` : source;
    assert.ok(matches(patterns, file), `push paths of nix.yml miss ${source}, which the flake checks build from`);
  }
  // The template job builds the default (React) frontend against the packed candidates, which the loopback
  // registry and the lock binding serve to the sandbox.
  for (const file of ["nix/lib/build-runic-application.nix", "flake.lock", "tools/Runic.Application.Templates/content/runic-app/flake.nix",
    "packages/web/react/src/index.ts", "packages/web/vite-plugin-runic/src/index.ts", "tests/templates/template-npm-registry.mjs",
    "tests/templates/bind-template-candidate-integrities.mjs"])
    assert.ok(matches(patterns, file), `push paths of nix.yml miss ${file}`);
  assert.ok(!matches(patterns, "packages/dotnet/Runic.Desktop/README.md"), "documentation does not run the Nix workflow");
});

// A3 (D077.25): a pull request runs it only when a Nix input or packaging path changes; the push to main checks the
// sources. Its paths are a subset of main's, so whatever a pull request runs, main runs too.
test("a pull request runs the Nix workflow for Nix inputs and packaging paths only", () => {
  const pull = filters("pull_request"), push = filters("push");
  const inputs = ["flake.nix", "flake.lock", "nix/lib/build-runic-application.nix", "nix/tests/locks.nix", "nixos/packaging/default.nix",
    "tests/fixtures/nix/lock-files/packages.lock.json", ".github/workflows/nix.yml", "global.json", "NuGet.config", "Directory.Packages.props",
    "Directory.Build.props", "Directory.Build.targets", "eng/build/desktop.props", "examples/Directory.Packages.props", "examples/NuGet.config",
    "packages/dotnet/Runic.Desktop/packages.lock.json", "packages/dotnet/Runic.Desktop/buildTransitive/Runic.Desktop.targets",
    "tools/Runic.Application.Views.Codegen/packages.lock.json", "examples/status-icon/packages.lock.json",
    "tests/native/Runic.Privileged.Smoke/packages.lock.json", "packages/web/views/package.json",
    "tools/Runic.Application.Templates/content/runic-app/flake.nix", "tools/Runic.Application.Templates/content/runic-app/.template.config/template.json",
    "tools/Runic.Application.Templates/content/runic-app/frontends/react/package-lock.json", "tests/templates/Test-NixTemplate.sh",
    "tests/templates/template-npm-registry.mjs", "tests/templates/bind-template-candidate-integrities.mjs", "eng/release/template-locks.mjs"];
  for (const file of inputs) {
    assert.ok(matches(pull, file), `pull_request paths of nix.yml miss ${file}`);
    assert.ok(matches(push, file), `push paths of nix.yml miss ${file}, which pull requests run it for`);
  }
  // Every lock file the flake checks restore from (nix/examples.nix) is an input.
  for (const source of sources.filter(path => path.endsWith("packages.lock.json")))
    assert.ok(matches(pull, source), `pull_request paths of nix.yml miss ${source}`);
  // The views source path is joined so that packages/web/views/test/entries.test.ts, which refuses scripts that bundle
  // that package's source, does not read it as one.
  for (const file of ["packages/dotnet/Runic.Desktop/Window.cs", ["packages/web/views/src", "index.ts"].join("/"), "packages/web/react/src/index.ts",
    "examples/status-icon/Program.cs", "tools/Runic.Application.Views.Codegen/Generator.cs", "eng/Versions.props", "eng/workspace.json",
    "nix/README.md", "tools/Runic.Application.Templates/content/runic-app/Program.cs"])
    assert.ok(!matches(pull, file), `a pull request that changes ${file} alone does not run the Nix workflow`);
  assert.equal(pull.at(-1), "!**/*.md");
});
