import assert from "node:assert/strict";
import { readFileSync, statSync } from "node:fs";
import { resolve } from "node:path";
import { test } from "node:test";
import { root } from "../../eng/run.mjs";

// The Nix workflow is path-filtered. Its flake checks build the SDK examples from the sources that
// nix/examples.nix lists, so a change to any of them must run it.
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

const sources = [...new Set([...readFileSync(resolve(root, "nix/examples.nix"), "utf8")
  .matchAll(/\.\.\/([A-Za-z0-9._-][A-Za-z0-9._/-]*)/g)].map(([, path]) => path.replace(/\/$/, "")))].filter(path => path !== ".");

test("the Nix workflow runs for every source of the packaged examples", () => {
  assert.ok(sources.length > 10, `nix/examples.nix lists ${sources.length} sources`);
  for (const event of ["push", "pull_request"]) {
    const patterns = filters(event);
    for (const source of sources) {
      // A directory is covered when a file in it is.
      const file = statSync(resolve(root, source)).isDirectory() ? `${source}/Project.csproj` : source;
      assert.ok(matches(patterns, file), `${event} paths of nix.yml miss ${source}, which nix/examples.nix builds from`);
    }
    for (const file of ["nix/lib/build-runic-application.nix", "flake.lock", "tools/Runic.Application.Templates/content/runic-app/flake.nix"])
      assert.ok(matches(patterns, file), `${event} paths of nix.yml miss ${file}`);
    assert.ok(!matches(patterns, "packages/dotnet/Runic.Desktop/README.md"), "documentation does not run the Nix workflow");
  }
});
