// Sets RUNIC_TEST_TIMEOUT_SCALE for the rest of a GitHub Actions job (.github/actions/test-timeout-scale): the
// runner's value from testTimeoutScales in eng/ci/platforms.mjs, or the job's own value when it sets one. Runs with
// the runner's Node before any setup, so it imports only plain data. eng/ci/README.md, "Test timeouts".
import { appendFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { testTimeoutScales } from "./platforms.mjs";

/** The scale for a runner, or the configured one; throws on a value that is not a positive number. */
export function testTimeoutScale(os, arch, configured) {
  if (configured !== undefined && configured !== "") {
    const value = Number(configured);
    if (!Number.isFinite(value) || value <= 0) throw new Error(`RUNIC_TEST_TIMEOUT_SCALE must be a positive number, got ${configured}.`);
    return configured;
  }
  const scale = testTimeoutScales[`${os}-${arch}`];
  if (!scale) throw new Error(`testTimeoutScales in eng/ci/platforms.mjs has no value for the ${os} ${arch} runner.`);
  return scale;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { RUNNER_OS: os, RUNNER_ARCH: arch, RUNIC_TEST_TIMEOUT_SCALE: configured, GITHUB_ENV: environment } = process.env;
  const scale = testTimeoutScale(os, arch, configured);
  if (environment) appendFileSync(environment, `RUNIC_TEST_TIMEOUT_SCALE=${scale}\n`);
  console.log(`RUNIC_TEST_TIMEOUT_SCALE=${scale} (${configured ? "set by the job" : `${os} ${arch} runner`})`);
}
