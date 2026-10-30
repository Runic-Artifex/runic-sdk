// Condition-wait timeouts for the Bun and Node tests and smokes, scaled by RUNIC_TEST_TIMEOUT_SCALE (a positive
// number, default 1). The .NET tests use tests/support/TestTimeouts.cs; the rules are in eng/ci/README.md,
// "Test timeouts". Scale only waits for a condition, never product timing or performance budgets. No dependencies.
import { fileURLToPath } from "node:url";

export const variable = "RUNIC_TEST_TIMEOUT_SCALE";

/** Reads a scale; an empty value is 1, anything but a positive number throws. */
export function readScale(value) {
  if (value === undefined || String(value).trim() === "") return 1;
  const scale = Number(value);
  if (!Number.isFinite(scale) || scale <= 0) throw new Error(`${variable} must be a positive number, got ${value}.`);
  return scale;
}

/** The configured factor. */
export const timeoutScale = readScale(globalThis.process?.env?.[variable]);

/** The scaled milliseconds for a condition wait of `milliseconds`. */
export const scaled = milliseconds => milliseconds > 0 && Number.isFinite(milliseconds) ? Math.ceil(milliseconds * timeoutScale) : milliseconds;

const root = fileURLToPath(new URL("../../", import.meta.url)).replace(/\\/g, "/");
const reported = new Set();

// The first stack frame outside the helpers names the waiting test or smoke.
function caller() {
  for (const line of (new Error().stack ?? "").split("\n").slice(1)) {
    const match = /\(?((?:file:\/\/)?[^\s()]+?):(\d+):\d+\)?\s*$/.exec(line.trim());
    if (!match || /\/(tests\/support\/timeouts|examples\/shared\/smoke)\.mjs$/.test(match[1]) || match[1].startsWith("node:")) continue;
    let file = match[1];
    try { if (file.startsWith("file://")) file = fileURLToPath(file); } catch { }
    file = file.replace(/\\/g, "/");
    const workspace = (globalThis.process?.env?.GITHUB_WORKSPACE ?? "").replace(/\\/g, "/").replace(/\/$/, "");
    if (workspace && file.startsWith(`${workspace}/`)) file = file.slice(workspace.length + 1);
    else if (file.startsWith(root)) file = file.slice(root.length);
    return { file, line: Number(match[2]) };
  }
  return undefined;
}

const data = value => value.replace(/%/g, "%25").replace(/\r/g, "%0D").replace(/\n/g, "%0A");
const property = value => data(value).replace(/:/g, "%3A").replace(/,/g, "%2C");

/**
 * Reports a scaled wait for `label` that succeeded after `elapsed` ms, more than its `unscaled` limit: without the
 * scale it would have failed. In GitHub Actions this is a `::warning::` annotation at the waiting line, `where`
 * (`{ file, line }`), by default the caller's; after an `await` the stack may no longer name it, so `startWait`
 * captures it when the wait starts.
 */
export function reportIfSlow(elapsed, unscaled, label = "a condition", where = caller()) {
  if (timeoutScale <= 1 || !(unscaled > 0) || !Number.isFinite(unscaled) || elapsed <= unscaled) return;
  const key = where ? `${where.file}:${where.line}:${label}` : label;
  if (reported.has(key)) return;
  reported.add(key);
  const message = `${label}${where ? ` (${where.file}:${where.line})` : ""} took ${Math.round(elapsed)} ms, past its unscaled ` +
    `${unscaled} ms limit; it passed only because ${variable}=${timeoutScale}.`;
  if (globalThis.process?.env?.GITHUB_ACTIONS === "true")
    console.log(`\n::warning ${where ? `file=${property(where.file)},line=${where.line},` : ""}title=${property("Slow test wait")}::${data(message)}`);
  else console.warn(`warning: ${message}`);
}

/**
 * Times one condition wait: `limit` is the scaled time, `remaining()` what is left, and `succeeded()` reports a
 * success after the unscaled time.
 */
export function startWait(unscaled, label) {
  const started = performance.now(), limit = scaled(unscaled), where = caller();
  return {
    unscaled, limit,
    elapsed: () => performance.now() - started,
    remaining: () => Math.max(0, limit - (performance.now() - started)),
    expired: () => performance.now() - started >= limit,
    succeeded: () => reportIfSlow(performance.now() - started, unscaled, label, where),
  };
}

/** Resolves with `promise`, or rejects when it does not settle within the scaled `unscaled` ms. */
export async function withinScaled(promise, unscaled, label = "operation") {
  const wait = startWait(unscaled, label);
  let timer;
  const expired = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`${label} did not settle within ${wait.limit} ms (${unscaled} ms × ${variable}=${timeoutScale})`)),
      wait.limit);
  });
  try {
    const value = await Promise.race([promise, expired]);
    wait.succeeded();
    return value;
  } finally { clearTimeout(timer); }
}
