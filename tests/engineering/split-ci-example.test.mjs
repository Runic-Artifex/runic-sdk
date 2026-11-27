import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { root } from "../../eng/run.mjs";
import { compareWorkflows, forgejoPath, githubPath } from "../../examples/split-ci/ci/check-workflows.mjs";

// D082.5: examples/split-ci keeps a GitHub/Gitea workflow and a Forgejo one, which ci/check-workflows.mjs keeps in step.
const example = join(root, "examples/split-ci");
const read = path => readFileSync(join(example, path), "utf8");
const github = read(githubPath), forgejo = read(forgejoPath);

test("the example's GitHub/Gitea and Forgejo workflows are in step", () => {
  const result = compareWorkflows(github, forgejo);
  assert.deepEqual(result.problems, []);
  assert.ok(result.artifacts >= 3, "the bundle's upload and the packaging jobs' downloads");
  assert.ok(result.labels >= 1, "Forgejo has its own default runner labels");
});

test("check-workflows.mjs exits 0 on the example", () => {
  const result = spawnSync(process.execPath, [join(example, "ci/check-workflows.mjs"), example], { encoding: "utf8", env: { ...process.env, PATH: "" } });
  assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
  assert.match(result.stdout, /are in step/);
});

const once = (text, from, to) => {
  assert.ok(text.includes(from), `the workflow has ${from}`);
  return text.replace(from, to);
};

test("a step that only one file changes is reported with both lines", () => {
  const { problems } = compareWorkflows(github, once(forgejo, "dotnet runic bundle --project src/Acme.App", "dotnet runic bundle --project src/Other"));
  assert.equal(problems.length, 1);
  assert.match(problems[0], new RegExp(`^${githubPath.replaceAll(".", "\\.")}:\\d+ and ${forgejoPath.replaceAll(".", "\\.")}:\\d+ differ:`));
  assert.match(problems[0], /src\/Other/);
});

test("a line that only one file has is reported", () => {
  const { problems } = compareWorkflows(`${github}      - run: echo extra\n`, forgejo);
  assert.match(problems.join("\n"), /only this file has this line: - run: echo extra/);
});

test("the comment at the top and the default runner labels may differ, the runner variable may not", () => {
  assert.deepEqual(compareWorkflows(`# Another comment\n${github}`, forgejo).problems, []);
  assert.deepEqual(compareWorkflows(github, forgejo.replaceAll("|| 'docker'", "|| 'linux-amd64'")).problems, []);
  const { problems } = compareWorkflows(github, once(forgejo, "vars.WINDOWS_RUNNER", "vars.WIN_RUNNER"));
  assert.match(problems.join("\n"), /differ:/);
});

test("each file must use its own CI system's artifact actions", () => {
  const stock = forgejo.replace(/forgejo\/upload-artifact@[0-9a-f]{40} # v4/, "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1");
  assert.match(compareWorkflows(github, stock).problems.join("\n"), /Forgejo needs forgejo\/upload-artifact/);
  const fork = github.replace(/actions\/download-artifact@[0-9a-f]{40} # v8\.0\.1/, "forgejo/download-artifact@d8d0a99033603453ad2255e58720b460a0555e1e # v4");
  assert.match(compareWorkflows(fork, forgejo).problems.join("\n"), /GitHub and Gitea use the stock actions\/download-artifact/);
  // An upload where the other file downloads is not the same step.
  const swapped = forgejo.replace(/forgejo\/upload-artifact@/, "forgejo/download-artifact@");
  assert.match(compareWorkflows(github, swapped).problems.join("\n"), /differ:/);
});

test("every action is pinned to a commit SHA with its version", () => {
  const tag = (text, from) => once(text, from, "actions/checkout@v7");
  const checkout = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1";
  const { problems } = compareWorkflows(tag(github, checkout), tag(forgejo, checkout));
  assert.equal(problems.filter(problem => /pin actions\/checkout to a full commit SHA/.test(problem)).length, 2);
});

test("the example names one Runic release everywhere, and its lock serves a candidate or that release", () => {
  const version = /<RunicVersion>([^<]+)<\/RunicVersion>/.exec(read("Directory.Packages.props"))?.[1];
  assert.match(version, /^\d+\.\d+\.\d+/);
  assert.equal(JSON.parse(read(".config/dotnet-tools.json")).tools["dotnet-runic"].version, version);
  const manifest = JSON.parse(read("src/Acme.App/Frontend/package.json"));
  assert.equal(manifest.dependencies["@runic-artifex/views"], version);
  const lock = JSON.parse(read("src/Acme.App/Frontend/package-lock.json"));
  assert.deepEqual(lock.packages[""].dependencies, manifest.dependencies);
  assert.deepEqual(lock.packages[""].devDependencies, manifest.devDependencies);
  for (const [path, entry] of Object.entries(lock.packages).filter(([path]) => path !== "")) {
    if (path.startsWith("node_modules/@runic-artifex/")) {
      // Without the candidate's resolved URL and hash, npm ci installs the version from the configured registry: the
      // package-smoke's candidate registry here, npm for a copy of the example.
      assert.equal(entry.version, version, path);
      assert.equal(entry.resolved, undefined, path);
      assert.equal(entry.integrity, undefined, path);
    } else {
      assert.match(entry.resolved, /^https:\/\/registry\.npmjs\.org\//, path);
      assert.match(entry.integrity, /^sha512-/, path);
    }
  }
});
