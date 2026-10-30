// tests/support/timeouts.mjs: RUNIC_TEST_TIMEOUT_SCALE for the Bun and Node tests and smokes (eng/ci/README.md,
// "Test timeouts").
import { expect, test } from "bun:test";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { readScale } from "../support/timeouts.mjs";

const helper = new URL("../support/timeouts.mjs", import.meta.url).href;

// Runs `source` as a module with the given environment and returns its output.
function run(source, env) {
  const directory = mkdtempSync(join(tmpdir(), "runic-test-timeouts-"));
  try {
    const script = join(directory, "wait.mjs");
    writeFileSync(script, source);
    const { RUNIC_TEST_TIMEOUT_SCALE: _, GITHUB_ACTIONS: __, GITHUB_WORKSPACE: ___, ...inherited } = process.env;
    const result = spawnSync(process.execPath, [script], { env: { ...inherited, ...env }, encoding: "utf8", timeout: 30_000 });
    return { ...result, script };
  } finally { rmSync(directory, { recursive: true, force: true }); }
}

test("the scale is a positive number that defaults to 1", () => {
  expect(readScale(undefined)).toBe(1);
  expect(readScale(" ")).toBe(1);
  expect(readScale("2.5")).toBe(2.5);
  for (const invalid of ["0", "-1", "fast", "Infinity"])
    expect(() => readScale(invalid)).toThrow(`RUNIC_TEST_TIMEOUT_SCALE must be a positive number, got ${invalid}.`);
  const failed = run(`import "${helper}";`, { RUNIC_TEST_TIMEOUT_SCALE: "0" });
  expect(failed.status).not.toBe(0);
  expect(failed.stderr).toContain("RUNIC_TEST_TIMEOUT_SCALE must be a positive number, got 0.");
});

test("only waits that pass after their unscaled limit are annotated, once, at the waiting line", () => {
  const source = `import { scaled, startWait, withinScaled } from "${helper}";
console.log("limit " + scaled(100) + " " + scaled(0));
const quick = startWait(5_000, "a quick wait");
quick.succeeded();
for (let attempt = 0; attempt < 2; attempt++) await withinScaled(new Promise(resolve => setTimeout(resolve, 80)), 20, "the slow frame");
await withinScaled(new Promise(() => {}), 10, "a hung wait").catch(error => console.log(error.message));
`;
  const ci = run(source, { RUNIC_TEST_TIMEOUT_SCALE: "4", GITHUB_ACTIONS: "true" });
  expect(ci.status).toBe(0);
  expect(ci.stdout).toContain("limit 400 0");
  const annotations = ci.stdout.split("\n").filter(line => line.startsWith("::warning "));
  expect(annotations).toHaveLength(1);
  expect(annotations[0]).toMatch(/^::warning file=[^,]*wait\.mjs,line=5,title=Slow test wait::the slow frame \(.*wait\.mjs:5\) took \d+ ms, past its unscaled 20 ms limit; it passed only because RUNIC_TEST_TIMEOUT_SCALE=4\.$/);
  expect(ci.stdout).toContain("a hung wait did not settle within 40 ms (10 ms × RUNIC_TEST_TIMEOUT_SCALE=4)");
  expect(ci.stdout).not.toContain("a quick wait");

  const local = run(source, { RUNIC_TEST_TIMEOUT_SCALE: "4" });
  expect(local.stdout).not.toContain("::warning");
  expect(local.stderr).toMatch(/^warning: the slow frame .* past its unscaled 20 ms limit/m);

  // Unscaled, the slow wait would have failed instead, so there is nothing to report.
  const unscaled = run(source.replace(/^for .*\n/m, ""), { GITHUB_ACTIONS: "true" });
  expect(unscaled.stdout).toContain("limit 100 0");
  expect(unscaled.stdout).not.toContain("::warning");
});
