// The quarantine (eng/ci/README.md, "Quarantine"; B2 of D077.25). eng/ci/flakes.json lists TUnit tests with an open
// flake issue. In the jobs that start .github/actions/flaky-tests, the required test steps skip them
// (tests/support/Quarantine.cs reads RUNIC_QUARANTINE); the job's quarantine step then runs each one on the same leg,
// without failing the job, and records the outcome for eng/ci/flakes.mjs. A release is blocked while the list names a
// test (`gate`).
//
//   node eng/ci/quarantine.mjs check [--online]     validate the list (--online: every issue is still open)
//   node eng/ci/quarantine.mjs run --job <id> --leg <leg>   run this job and leg's quarantined tests
//   node eng/ci/quarantine.mjs gate [--online]      fail while any test is quarantined (the release preflight)
import { execFileSync, spawnSync } from "node:child_process";
import { appendFileSync, existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { failingTests } from "./flakes.mjs";

export const root = resolve(fileURLToPath(new URL("../..", import.meta.url)));
export const listPath = resolve(root, "eng/ci/flakes.json");
// The jobs that start .github/actions/flaky-tests, so their test steps skip quarantined tests and run them afterwards.
export const quarantineJobs = ["managed", "native", "screen-readers", "wpf"];
const repository = "Runic-Artifex/runic-sdk";

export const load = (path = listPath) => JSON.parse(readFileSync(path, "utf8")).quarantine ?? [];

const csFiles = directory => readdirSync(directory).flatMap(name => {
  const path = join(directory, name);
  if (["bin", "obj"].includes(name)) return [];
  return statSync(path).isDirectory() ? csFiles(path) : name.endsWith(".cs") ? [path] : [];
});

// The list's problems, as messages; empty when it is valid. `states` maps issue numbers to "open" or "closed" when
// known (check --online, gate --online).
export function validate(entries, { base = root, states = {} } = {}) {
  const problems = [];
  const seen = new Set();
  entries.forEach((entry, index) => {
    const at = `flakes.json quarantine[${index}]${entry?.method ? ` (${entry.class}.${entry.method})` : ""}`;
    if (!Number.isInteger(entry?.issue) || entry.issue <= 0) { problems.push(`${at}: names no issue; quarantine only a test with an open flake issue`); return; }
    if (states[entry.issue] === "closed") problems.push(`${at}: #${entry.issue} is closed; remove the entry (the test runs in the required steps again)`);
    if (!quarantineJobs.includes(entry.job)) problems.push(`${at}: job must be one of ${quarantineJobs.join(", ")}`);
    for (const key of ["project", "class", "method"])
      if (typeof entry[key] !== "string" || !/^[\w./-]+$/.test(entry[key])) problems.push(`${at}: ${key} is missing`);
    if (entry.legs !== undefined && !(Array.isArray(entry.legs) && entry.legs.length && entry.legs.every(leg => typeof leg === "string")))
      problems.push(`${at}: legs is a list of leg names (a runtime identifier, a screen reader), or left out for every leg`);
    for (const key of ["wrapper", "arguments"])
      if (entry[key] !== undefined && !(Array.isArray(entry[key]) && entry[key].every(item => typeof item === "string")))
        problems.push(`${at}: ${key} is a list of strings`);
    const key = `${entry.class}.${entry.method}`;
    if (seen.has(key)) problems.push(`${at}: listed twice`);
    seen.add(key);
    const project = typeof entry.project === "string" ? resolve(base, entry.project) : "";
    const name = project.split(/[\\/]/).at(-1);
    if (!project || !existsSync(join(project, `${name}.csproj`))) { problems.push(`${at}: ${entry.project} is not a test project`); return; }
    if (!readFileSync(join(project, `${name}.csproj`), "utf8").includes("<RunicFlakyTests>true</RunicFlakyTests>"))
      problems.push(`${at}: ${entry.project} does not set RunicFlakyTests, so its tests cannot skip a quarantined test`);
    const sources = csFiles(project).map(path => readFileSync(path, "utf8"));
    if (!sources.some(text => new RegExp(`\\bclass\\s+${entry.class}\\b`).test(text) && new RegExp(`\\b${entry.method}\\s*\\(`).test(text)))
      problems.push(`${at}: ${entry.project} has no ${entry.class}.${entry.method}`);
  });
  return problems;
}

// The release gate (B2): a release waits while the list names a test, open issue or not (a closed one is a stale
// entry). Returns the reasons, empty when the release may go on.
export function gate(entries, { base = root, states = {} } = {}) {
  return [...validate(entries, { base, states }), ...entries.map(entry => `${entry.class}.${entry.method} is quarantined under #${entry.issue}`
    + `${states[entry.issue] ? ` (${states[entry.issue]})` : ""}: fix it and remove the entry (eng/ci/flakes.json) before the release.`)];
}

// The issues' states, through gh.
export function issueStates(entries) {
  return Object.fromEntries([...new Set(entries.map(entry => entry.issue).filter(Number.isInteger))].map(number => [number,
    execFileSync("gh", ["api", `repos/${repository}/issues/${number}`, "--jq", ".state"], { encoding: "utf8" }).trim()]));
}

// The entries a job and leg run.
export const selected = (entries, job, leg) => entries.filter(entry => entry.job === job && (!entry.legs || entry.legs.includes(leg)));

// The command that runs one quarantined test, with the required steps' skip turned off.
export function command(entry) {
  const run = ["dotnet", "run", "--project", entry.project, "-c", "Release", "--",
    "--treenode-filter", `/*/*/${entry.class}/${entry.method}`, "--output", "detailed", ...entry.arguments ?? []];
  return [...entry.wrapper ?? [], ...run];
}

// One run's record line (the same file as the recorded retries, eng/ci/retried-tests.mjs).
export function outcome(entry, { status, output }, leg) {
  const failure = failingTests(output)[0];
  const result = status === 0 ? "passed" : status === 8 ? "not-run" : "failed";
  return { quarantined: true, test: failure?.name ?? entry.method, method: entry.method, class: entry.class, issue: entry.issue,
    leg, outcome: result, exception: result === "failed" ? failure?.raw ?? `exit code ${status}` : "", at: new Date().toISOString() };
}

function runSelected(job, leg, { env = process.env, log = console.log } = {}) {
  const records = [];
  for (const entry of selected(load(), job, leg)) {
    const [executable, ...args] = command(entry);
    log(`::group::Quarantined ${entry.class}.${entry.method} (#${entry.issue})`);
    const child = spawnSync(executable, args, { cwd: root, encoding: "utf8", maxBuffer: 256 * 1024 * 1024,
      env: { ...env, RUNIC_QUARANTINE: "" } });
    const output = `${child.stdout ?? ""}${child.stderr ?? ""}`;
    log(output);
    log("::endgroup::");
    const record = outcome(entry, { status: child.status ?? 1, output }, leg);
    records.push(record);
    if (env.RUNIC_RETRIED_TESTS) appendFileSync(env.RUNIC_RETRIED_TESTS, `${JSON.stringify(record)}\n`);
  }
  if (!records.length) log(`No test is quarantined for ${job} / ${leg}.`);
  return records;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [verb, ...rest] = process.argv.slice(2);
  const option = name => rest.includes(name) ? rest[rest.indexOf(name) + 1] : undefined;
  const entries = load();
  if (verb === "check" || verb === "gate") {
    const states = rest.includes("--online") ? issueStates(entries) : {};
    const errors = verb === "gate" ? gate(entries, { states }) : validate(entries, { states });
    for (const error of errors) console.log(`::error title=${verb === "gate" ? "Release gate: quarantine" : "Quarantine list"}::${error}`);
    if (!errors.length) console.log(verb === "gate" ? "No test is quarantined." : `The quarantine list is valid (${entries.length} entries).`);
    process.exitCode = errors.length ? 1 : 0;
  } else if (verb === "run") {
    runSelected(option("--job"), option("--leg"));
  } else {
    throw new Error("usage: node eng/ci/quarantine.mjs check|gate [--online] | run --job <id> --leg <leg>");
  }
}
