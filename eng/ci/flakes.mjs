// The flake policy's report (eng/ci/README.md, "Flakes"; D069, C10). It reads the failed jobs of recent ci.yml runs
// (pull requests, merge queue entries, main, schedules) through `gh api` and gives each failure a signature: its failing
// test and first error line or, when the log names no test, its failed step and first `##[error]` line. The same
// signature in several runs, jobs or legs is one entry. An entry seen in two or more runs without an issue that
// mentions its test or error needs an issue the same day; one with an open issue is a known flake, so a queue entry that
// failed only in known flakes is re-queued. Read-only: it never reruns, comments or files anything.
//
//   bun eng/ci/flakes.mjs [--since 2d|36h|<ISO date>] [--repo owner/name] [--min 2] [--json]
import { execFile } from "node:child_process";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

export const defaultRepository = "Runic-Artifex/runic-sdk";
// Aggregate jobs that fail because another one did; they name no failure of their own.
const aggregateJobs = new Set(["verify"]);

const strip = line => line.replace(/\x1b\[[0-9;]*m/g, "").replace(/^\d{4}-\d\d-\d\dT[\d:.]+Z ?/, "").replace(/\r$/, "");
// Parameterized cases group under their test: DirectoryReplacedWithoutAReadIsWatchedAgain(False) is the same test as (True).
const baseName = name => name.replace(/\s*\((?:[^()]|\([^()]*\))*\)$/, "").trim();
const testFailurePrefix = /^\s*TUnit\.Engine\.Exceptions\.TestFailedException:\s*(?:\[Test Failure\]\s*)?/;
const detail = line => /^\s*(at\s|\d+ \||\^|---|##\[)/.test(line) || line.trim() === "";
const errorLine = /^\s*(?:error|[\w.]*(?:Error|Exception))\b[^:]*:/;

// An error line as a signature: its first clause (a probe's "VoiceOver did not say 'Finish'; it said: …" varies after
// it), without paths, numbers or JSON, so the same failure on another runner or run with another temporary directory,
// port or count reads the same. An exit code stays (127 or 134, a crash, is another failure than 1), and so does a
// number inside a word (CS0436, IL2037, Win32Exception).
export function normalizeError(text) {
  return text.replace(testFailurePrefix, "").split("; ")[0]
    .replace(/(?:[A-Za-z]:)?(?:[\\/][^\s'"`),;]+)+/g, "<path>")
    .replace(/\{.*\}/g, "{…}")
    .replace(/(?<![\w])(?<!exit code |exited with )(?:0x[\da-f]+|\d+(?:[.:]\d+)*)/gi, "N")
    .replace(/\s+/g, " ").trim().slice(0, 140);
}

// The words of an error that an issue would quote: the message after its exception type, up to its first path,
// number, value or quote; after a tool's one-word prefix ("hdiutil: attach failed - …") when that alone is too short.
// Too short or too generic to tell failures apart (an exit code), it is not used.
export function messageHead(text) {
  const clauses = text.replace(testFailurePrefix, "").replace(/^(?:\s*(?:error|[\w.]*(?:Error|Exception))\s*:\s*)+/i, "").split(": ");
  const head = clauses.slice(0, /^[\w.-]+$/.test(clauses[0].trim()) ? 2 : 1)
    .map(clause => clause.split(/[\\/{"'`=\d]/)[0].replace(/[\s.,;:]+$/, "").trim()).find(clause => clause.length >= 20);
  return head && !/exited with|exit code/i.test(head) ? head : null;
}

// The failures a job log names, each with its first error line. Each pattern is one runner's failure line:
// TUnit (`failed Name (1s 2ms)`, its exception below it), bun test (`(fail) name [12.3ms]`, its error above it since the
// previous test's result), node:test's spec reporter (`✖ name (12ms)`, its error below), and the native probes and
// template checks (`FAIL name: reason`, optionally after `[12.3s]`). A TUnit session stopped by its --timeout or cancelled
// names no failed test; the test then active is the one that hung.
export function failingTests(log) {
  const lines = log.split("\n").map(strip);
  const found = new Map();
  const add = (name, error) => { if (!found.has(name)) found.set(name, { name, error: error ? normalizeError(error) : "", raw: error ?? "" }); };
  const below = index => lines.slice(index + 1, index + 6).find(line => !detail(line))?.trim();
  const above = index => {
    let start = index;
    while (start > 0 && index - start < 80 && !/^\((pass|fail|skip|todo)\)|^##\[group\]/.test(lines[start - 1])) start--;
    return lines.slice(start, index).find(line => errorLine.test(line))?.trim();
  };
  let active;
  let aborted = false;
  lines.forEach((line, index) => {
    let match;
    if ((match = /^\s*failed (\S.*?) \(\d[^)]*\)\s*$/.exec(line))) add(baseName(match[1]), below(index));
    else if ((match = /^\(fail\) (.+?) \[[\d.]+ ?m?s\]\s*$/.exec(line))) add(match[1].trim(), above(index));
    else if ((match = /^\s*✖ (.+?) \([\d.]+m?s\)\s*$/.exec(line))) add(match[1].trim(), below(index));
    else if ((match = /^(?:\[[\d.]+s\] )?FAIL\s+([\w.:/-]+):\s*(.*)$/.exec(line))) add(match[1], match[2]);
    if ((match = /\| active: (.+?)\s*$/.exec(line))) active = baseName(match[1]);
    if (/Test session was aborted|The test run was cancelled|Test session timed out/.test(line)) aborted = true;
  });
  if (found.size === 0 && aborted && active) add(active, "the test session was aborted (its --timeout or a cancel) while this test ran");
  return [...found.values()];
}

// The part of a job log that its failed step wrote: the lines stamped from the step's start to its end (GitHub gives
// step times in whole seconds). Tests that failed in an earlier step with continue-on-error (a quarantined test, a
// retried download) and the job's post steps are not the failure. A log without timestamps, or a step without times,
// is used whole.
export function stepLog(log, step) {
  const start = Date.parse(step?.started_at ?? ""), end = Date.parse(step?.completed_at ?? "") + 1000;
  if (Number.isNaN(start) || Number.isNaN(end)) return log;
  const lines = log.split("\n");
  let current = NaN;
  const kept = lines.filter(line => {
    const stamp = /^(\d{4}-\d\d-\d\dT[\d:.]+Z)/.exec(line);
    if (stamp) current = Date.parse(stamp[1]);
    return current >= start && current < end;
  });
  return kept.length ? kept.join("\n") : log;
}

// Lines that wrap another failure: a runner script's "bash exited with 1", bun's "script exited", a test harness's
// "… failed in <dir> with exit code 1". They are the signature only when nothing more specific was printed.
const wrapper = /\bexited with (?:code )?-?\d+\.?\s*$|\bfailed in \S+ with exit code -?\d+|^error: script "[^"]+" exited|^Process completed with exit code/;
// A test host's echo of a passing case's captured output ("StandardError: child-error"), not an error.
const echo = /^\s*Standard(?:Error|Output)\s*:/;
// Lines that name the failure of a step whose log names no test, most specific first:
//   a compiler, MSBuild, ILC or NuGet error (`… : error CS0436: …`, `ILC : error IL2037: …`, `… : error : …`): the first
//   one is the cause, later ones (MSB3077 after IL2037) follow from it;
//   a probe's or smoke's own failure line (a deadline, `HOSTED_WINDOWS_FAILED:`, `hdiutil: attach failed`, a download
//   that ran out of attempts, an unhandled exception, an uncaught NSException) and PowerShell's thrown message: the
//   last one.
const buildError = /(?:^|\s|\))(?:fatal )?error ([A-Z][A-Z\d]*\d{3,})\s*:\s*(\S.*)$|\s:\s(?:fatal )?error()\s*:\s*(\S.*)$/;
const probeFailure = new RegExp([
  /exceeded its [\w-]+ deadline/, /did not finish within/, /^[A-Z][A-Z\d]*(?:_[A-Z\d]+)*_FAILED\b/, /^Unhandled exception\. /,
  /could not be downloaded/, /\battach failed\b/, /terminating due to uncaught exception/, /^curl: \(\d+\)/,
  /^npm error (?:network|code)\b/, /^(?:##\[warning\])?Attempt (\d+) of \1 failed with status/,
].map(pattern => pattern.source).join("|"));

// The line that names a failed step's error: the first `##[error]` annotation that says more than the step's exit code;
// else the first build error; else the last probe failure line or PowerShell error; else the last error line (a
// script's thrown error), not counting echoes of captured output and wrappers; else the last wrapper; else the
// exit code.
export function firstError(log) {
  const lines = log.split("\n").map(strip);
  const errors = lines.map(line => /^##\[error\](.*)$/.exec(line)?.[1].trim()).filter(Boolean);
  const annotated = errors.find(text => !/^Process completed with exit code/.test(text));
  if (annotated) return annotated;
  const end = lines.findIndex(line => line.startsWith("##[error]"));
  const before = lines.slice(0, end < 0 ? lines.length : end).map(line => line.trim()).filter(line => line && !echo.test(line));
  const build = before.map(line => buildError.exec(line)).map(match => match && { code: match[1] ?? "", text: match[2] ?? match[4] })
    .find(match => match && !wrapper.test(match.text));
  if (build) return `error${build.code ? ` ${build.code}` : ""}: ${build.text}`;
  // PowerShell prints a thrown message, then "At C:\…\script.ps1:92 char:5".
  const thrown = before.findLastIndex(line => /^At .+\.ps1:\d+ char:\d+$/.test(line));
  const probe = before.findLastIndex(line => probeFailure.test(line));
  if (probe >= 0 || thrown > 0) return probe > thrown - 1 ? before[probe] : before[thrown - 1];
  return before.findLast(line => errorLine.test(line) && !wrapper.test(line))
    ?? before.findLast(line => wrapper.test(line)) ?? errors[0] ?? "";
}

const dayMs = 24 * 60 * 60 * 1000;
// `2d`, `36h` or an ISO date, relative to `now`.
export function sinceDate(value, now = new Date()) {
  const match = /^(\d+)([dh])$/.exec(value);
  if (match) return new Date(now.getTime() - Number(match[1]) * (match[2] === "d" ? dayMs : dayMs / 24));
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) throw new Error(`--since takes 2d, 36h or a date, not ${value}`);
  return date;
}

async function mapLimited(items, limit, map) {
  const results = new Array(items.length);
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const index = next++;
      results[index] = await map(items[index], index);
    }
  }));
  return results;
}

// GitHub answers the runs list with HTTP 502 at per_page=100 when the runs are large; retry a server error with half
// the page size, down to 25.
export async function withSmallerPages(get, path) {
  for (let current = path; ;) {
    try {
      return await get(current);
    } catch (error) {
      const size = Number(/[?&]per_page=(\d+)/.exec(current)?.[1]);
      if (!/HTTP 5\d\d|\b5\d\d\b.*(Bad Gateway|Server Error|Unavailable|Timeout)/i.test(String(error?.message ?? error)) || !(size > 25)) throw error;
      current = current.replace(/([?&]per_page=)\d+/, `$1${Math.max(25, Math.floor(size / 2))}`);
    }
  }
}

// `source` reads GitHub: `json(path)` returns a REST response (paginated paths return every item), `log(jobId)` a job log.
// The CLI passes ghSource; the tests pass recorded responses.
export async function collect(source, { repository = defaultRepository, since, concurrency = 4 }) {
  const day = since.toISOString().slice(0, 10);
  const listed = (await withSmallerPages(source.json, `repos/${repository}/actions/workflows/ci.yml/runs?created=%3E%3D${day}&per_page=100`))
    .filter(run => new Date(run.created_at) >= since && run.status === "completed");
  // Cancelled runs (superseded pushes, a cancelled queue) are counted, not classified.
  const cancelled = listed.filter(run => run.conclusion === "cancelled").length;
  // A run that passed on a later attempt failed before it: rerun flakes count too.
  const runs = listed.filter(run => run.conclusion === "failure" || run.run_attempt > 1);
  const failures = (await mapLimited(runs, concurrency, async run => {
    const jobs = await withSmallerPages(source.json, `repos/${repository}/actions/runs/${run.id}/jobs?filter=all&per_page=100`);
    return mapLimited(jobs.filter(job => job.conclusion === "failure" && !aggregateJobs.has(job.name)), concurrency, async job => {
      const attempt = job.run_attempt ?? run.run_attempt;
      // The same job of a later attempt (the same commit) passed: the failure is a flake, not a break.
      const passedOnRerun = jobs.some(other => other.name === job.name && (other.run_attempt ?? 0) > attempt && other.conclusion === "success");
      const failed = job.steps?.find(item => item.conclusion === "failure");
      const step = failed?.name ?? "(no failed step)";
      const log = stepLog(await source.log(job.id).catch(() => ""), failed);
      const tests = failingTests(log);
      const named = tests.length ? tests.map(item => ({ ...item, test: true }))
        : [{ name: `step: ${step}`, test: false, raw: firstError(log), error: normalizeError(firstError(log)) }];
      return named.map(({ name, test, error, raw }) => ({
        name, error, raw, test, job: job.name, step, run: run.id, attempt, sha: run.head_sha, passedOnRerun,
        event: run.event, branch: run.head_branch, pullRequests: (run.pull_requests ?? []).map(item => item.number),
        url: job.html_url, at: job.started_at ?? run.created_at,
      }));
    });
  })).flat(2);
  // Issues updated in the last 90 days, opened or closed, to match tests and errors against.
  const issueSince = new Date(since.getTime() - 90 * dayMs).toISOString();
  const issues = (await withSmallerPages(source.json, `repos/${repository}/issues?state=all&since=${issueSince}&per_page=100`))
    .filter(issue => !issue.pull_request)
    .map(issue => ({ number: issue.number, state: issue.state, closedAt: issue.closed_at ?? null, title: issue.title,
      text: `${issue.title}\n${issue.body ?? ""}`, url: issue.html_url }));
  return { repository, since: since.toISOString(), runs: runs.length, cancelled, failures, issues };
}

// Failures of the runners and services rather than the code: a mirror or registry that did not answer, an artifact
// service error, setup-dotnet's download, a runner that shut down. Only a step's error counts; a named test's failure
// is the test's. ci.yml retries most of these steps (eng/ci/README.md, "Flakes").
export const infrastructure = [
  /could not be downloaded/, /^(?:##\[warning\])?Attempt (\d+) of \1 failed with status \d+: (?:sudo |\S*\/)?\S*(?:apt-get|curl|playwright-core install)/,
  /Failed to install dotnet/, /Unable to download artifact|Artifact download failed|Failed to (?:Create|Finalize|Upload)Artifact|Unable to make request/,
  /runner has received a shutdown signal|lost communication with the server|hosted runner encountered an error/,
  /\b(?:ECONNRESET|ETIMEDOUT|EAI_AGAIN)\b|socket hang up|^npm error network\b|^curl: \(\d+\).* error: 5\d\d\b/, /\bNU1301\b/,
];
const infrastructureFailure = item => !item.test && infrastructure.some(pattern => pattern.test(item.raw ?? item.error));

// A closed issue still counts as known for this long after it closed: a fix that did not hold shows as a regression
// of a known flake (re-queue, and reopen the issue), not as a new failure to investigate.
export const recentlyClosedDays = 7;
// Where a sighting came from: its pull request (the queue's temporary branch names it), or its branch (main).
const origin = item => String(item.pullRequests?.[0] ?? /^gh-readonly-queue\/[^/]+\/pr-(\d+)-/.exec(item.branch ?? "")?.[1] ?? item.branch);

// Group failures by signature (test or step, and first error line) across runs, jobs and legs. An entry's issues are
// those whose title or body mention its test, or the words of its error (messageHead). The status of an entry:
//   known            an open issue mentions it: a known flake (a queue entry failing only in known flakes is re-queued);
//   infrastructure   a step failed on a runner or service (`infrastructure`), not the code: re-queue or rerun;
//   needs-issue      seen in `min` or more runs and no issue mentions it: file one today;
//   regressed        only closed issues mention it, and it failed after the last of them closed: reopen or file one;
//   fixed            only closed issues mention it, and every sighting predates the last closing;
//   repeated-step    seen in `min` or more runs, the logs name no test, and no issue mentions the error: read one;
//   first-sighting   seen once.
// `flake` is set when a sighting's job passed on a rerun of the same commit; `evidence` when it did, or the entry
// failed for two or more pull requests or branches (a failure one change causes stays with that change).
//
// Each failed merge queue run gets a verdict:
//   re-queue      every failure in it is known, infrastructure, or matches an issue that was open when it failed or
//                 closed less than `recentlyClosedDays` before;
//   file-issue    the others are flakes by evidence that no open issue names: file or reopen the issue (D069), then
//                 re-queue;
//   investigate   a failure is neither: it may be the change.
export function report({ failures, issues, runs, cancelled = 0, since, repository }, { min = 2 } = {}) {
  const signature = item => `${item.name}\n${item.error}`;
  const groups = new Map();
  for (const failure of failures) {
    const key = signature(failure);
    const group = groups.get(key) ?? { key, name: failure.name, error: failure.error, raw: failure.raw, test: failure.test, jobs: [], sightings: [] };
    if (!group.jobs.includes(failure.job)) group.jobs.push(failure.job);
    if (!group.sightings.some(item => item.run === failure.run && item.attempt === failure.attempt && item.job === failure.job)) group.sightings.push(failure);
    groups.set(key, group);
  }
  const mentions = group => {
    const head = messageHead(group.raw);
    const needles = [...group.test ? [group.name] : [], ...head ? [head] : []];
    return issues.filter(issue => needles.some(needle => issue.text.includes(needle)));
  };
  const all = [...groups.values()].map(group => {
    const matched = mentions(group);
    const open = matched.filter(issue => issue.state === "open");
    const runs = new Set(group.sightings.map(item => `${item.run}/${item.attempt}`)).size;
    const closed = matched.map(issue => issue.closedAt).filter(Boolean).sort().at(-1);
    const status = open.length ? "known"
      : infrastructureFailure(group) ? "infrastructure"
      : closed ? (group.sightings.some(item => item.at > closed) ? "regressed" : "fixed")
      : runs < min ? "first-sighting" : group.test ? "needs-issue" : "repeated-step";
    const flake = group.sightings.some(item => item.passedOnRerun);
    const { raw, ...rest } = group;
    return { ...rest, runs, status, flake, evidence: flake || new Set(group.sightings.map(origin)).size > 1,
      issues: matched.map(({ number, state, title, url, closedAt }) => ({ number, state, title, url, closedAt })) };
  }).sort((a, b) => b.runs - a.runs || a.key.localeCompare(b.key));
  const byKey = new Map(all.map(group => [group.key, group]));
  const recent = recentlyClosedDays * dayMs;
  // Known when it failed: an open issue, an infrastructure failure, or an issue closed after it or shortly before.
  const knownFailure = item => {
    const group = byKey.get(signature(item));
    return group.status === "known" || group.status === "infrastructure"
      || group.issues.some(issue => issue.closedAt && Date.parse(issue.closedAt) > Date.parse(item.at) - recent);
  };
  const queue = [...new Set(failures.filter(item => item.event === "merge_group").map(item => item.run))].map(run => {
    const inRun = failures.filter(item => item.run === run);
    const keys = [...new Set(inRun.map(signature))];
    const unknown = [...new Set(inRun.filter(item => !knownFailure(item)).map(signature))].map(key => byKey.get(key));
    const verdict = unknown.length === 0 ? "re-queue"
      : unknown.every(group => group.evidence && group.runs >= min) ? "file-issue" : "investigate";
    const first = inRun[0];
    return { run, branch: first.branch, url: first.url.replace(/\/job\/\d+$/, ""), verdict, failures: keys,
      unknown: unknown.map(group => group.key) };
  });
  return { repository, since, runs, cancelled, groups: all, queue };
}

const sighting = item => `${item.event}${item.pullRequests.length ? ` #${item.pullRequests.join(",#")}` : item.event === "merge_group"
  ? ` ${item.branch.replace(/^gh-readonly-queue\/main\/(pr-\d+).*/, "$1")}` : ` ${item.branch}`} ${item.job}`
  + `${item.attempt > 1 ? ` (attempt ${item.attempt})` : ""}${item.passedOnRerun ? " (passed on rerun)" : ""}`
  + ` (${item.at.slice(0, 16).replace("T", " ")}Z) ${item.url}`;

export function format(result) {
  const section = (title, groups, describe) => groups.length === 0 ? [] : [`${title}:`, ...groups.flatMap(group => [
    `  ${group.name}: ${group.runs} run${group.runs === 1 ? "" : "s"}, ${group.jobs.join(", ")}${describe(group)}`
      + `${group.flake ? " — flake: passed on a rerun of the same commit" : ""}`,
    ...group.error ? [`    ${group.error}`] : [],
    ...group.sightings.map(item => `    ${sighting(item)}`),
  ]), ""];
  const by = status => result.groups.filter(group => group.status === status);
  const issueList = group => group.issues.map(issue => ` #${issue.number} (${issue.state})`).join(",");
  const byKey = new Map(result.groups.map(group => [group.key, group]));
  const verdicts = ["re-queue", "file-issue", "investigate"].map(verdict => [verdict, result.queue.filter(entry => entry.verdict === verdict).length]);
  return [
    `${result.repository}: ${result.runs} failed or rerun ci.yml runs since ${result.since.slice(0, 16).replace("T", " ")}Z`
      + `; ${result.cancelled} cancelled (not classified)`, "",
    ...section("Seen twice or more, no issue mentions it (file one today; fix or skip it, linking the issue, within the cycle)", by("needs-issue"), () => ""),
    ...section("Failed again after its issue closed (reopen it or file one)", by("regressed"), group => ` —${issueList(group)}`),
    ...section("Seen twice or more in a step whose log names no test (read one: a real break or a flake)", by("repeated-step"), () => ""),
    ...section("Known flakes (an open issue mentions them)", by("known"), group => ` —${issueList(group)}`),
    ...section("Infrastructure: a runner, mirror, registry or artifact service failed (rerun; ci.yml retries most of these)", by("infrastructure"), () => ""),
    ...section("Fixed: only closed issues mention them, and no failure since they closed", by("fixed"), group => ` —${issueList(group)}`),
    ...section("Seen once", by("first-sighting"), group => group.test ? "" : " (the log names no test; read it)"),
    ...result.queue.length ? [
      `Failed merge queue entries (${verdicts.map(([verdict, count]) => `${verdict} ${count}`).join(", ")}):`,
      ...result.queue.flatMap(entry => [
        `  ${entry.verdict.padEnd(11)} ${entry.branch.replace(/^gh-readonly-queue\/main\//, "")} ${entry.url}`,
        ...entry.unknown.map(key => byKey.get(key)).map(group => `    ${group.status}: ${group.name}${group.error ? ` — ${group.error}` : ""}`),
      ]), ""] : [],
  ].join("\n").trimEnd();
}

const exec = promisify(execFile);
const gh = async (...args) => {
  try {
    return (await exec("gh", ["api", ...args], { maxBuffer: 512 * 1024 * 1024 })).stdout;
  } catch (error) {
    throw new Error(`gh api ${args.at(-1)}: ${error.stderr || error.message}`);
  }
};
export const ghSource = {
  json: async path => {
    // --paginate --slurp gives one array of pages; list endpoints wrap items in an object.
    const pages = JSON.parse(await gh("--paginate", "--slurp", path));
    return pages.flatMap(page => Array.isArray(page) ? page : page.workflow_runs ?? page.jobs ?? []);
  },
  log: jobId => gh("--allow-escape-sequences", `repos/${defaultRepository}/actions/jobs/${jobId}/logs`),
};

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const option = (name, fallback) => args.includes(name) ? args[args.indexOf(name) + 1] : fallback;
  const repository = option("--repo", defaultRepository);
  const source = { ...ghSource, log: jobId => gh("--allow-escape-sequences", `repos/${repository}/actions/jobs/${jobId}/logs`) };
  const result = report(await collect(source, { repository, since: sinceDate(option("--since", "2d")) }), { min: Number(option("--min", "2")) });
  console.log(args.includes("--json") ? JSON.stringify(result, null, 2) : format(result));
}
