import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import test from "node:test";
const cli = fileURLToPath(new URL("../dist/esm/cli.js", import.meta.url));
const run = (...args) => spawnSync(process.execPath, [cli, ...args], { encoding: "utf8", env: { ...process.env, NO_COLOR: "1" } });

test("root and command help are successful, descriptive and plain when redirected", () => {
  for (const args of [[], ["--help"], ["help", "generate"], ["diff", "--help"]]) {
    const result = run(...args);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /USAGE/);
    assert.doesNotMatch(result.stdout, /\u001b\[/);
  }
  assert.match(run("generate", "--help").stdout, /--project/);
  assert.match(run("generate", "--help").stdout, /EXAMPLES/);
});
test("version and generated completions are available", () => {
  assert.equal(run("--version").status, 0);
  const completions = run("--completions", "bash");
  assert.equal(completions.status, 0, completions.stderr);
  assert.match(completions.stdout, /runic-bridge/);
});
test("invalid flags, commands and authority combinations produce actionable failures", () => {
  for (const args of [["bogus"], ["generate", "--bogus"], ["generate", "--authority", "csharp"], ["diff", "one.json"]]) {
    const result = run(...args);
    assert.equal(result.status, 1);
    assert.ok(result.stderr.length > 0, JSON.stringify(result));
  }
  const missing = run("generate", "--authority", "csharp");
  assert.match(missing.stderr, /requires --project/);
  assert.doesNotMatch(missing.stderr, /at .*cli.js/);
});
