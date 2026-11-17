// The flake policy's report (eng/ci/README.md, "Flakes"; D069, C10). It reads the failed jobs of recent ci.yml runs
// (pull requests, merge queue entries, main, schedules) through `gh api`, names each failure by its failing test or,
// failing that, its step, and groups the names across runs. A name seen in two or more runs without an open issue
// that mentions it needs an issue the same day; one with an open issue is a known flake, so a queue entry that failed
// only in known flakes is re-queued. Read-only: it never reruns, comments or files anything.
//
//   bun eng/ci/flakes.mjs [--since 2d|<ISO date>] [--repo owner/name] [--min 2] [--json]
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

// The failing tests a job log names. Each pattern is one runner's failure line:
// TUnit (`failed Name (1s 2ms)`), bun test (`(fail) name [12.3ms]`), node:test's spec reporter (`✖ name (12ms)`), and the
// native probes and template checks (`FAIL name: reason`, optionally after `[12.3s]`). A TUnit session stopped by its
// --timeout or cancelled names no failed test; the test then active is the one that hung.
export function failingTests(log) {
  const names = new Set();
  let active;
  let aborted = false;
  for (const raw of log.split("\n")) {
    const line = strip(raw);
    let match;
    if ((match = /^\s*failed (\S.*?) \(\d[^)]*\)\s*$/.exec(line))) names.add(baseName(match[1]));
    else if ((match = /^\(fail\) (.+?) \[[\d.]+ ?m?s\]\s*$/.exec(line))) names.add(match[1].trim());
    else if ((match = /^\s*✖ (.+?) \([\d.]+m?s\)\s*$/.exec(line))) names.add(match[1].trim());
    else if ((match = /^(?:\[[\d.]+s\] )?FAIL\s+([\w.:/-]+):/.exec(line))) names.add(match[1]);
    if ((match = /\| active: (.+?)\s*$/.exec(line))) active = baseName(match[1]);
    if (/Test session was aborted|The test run was cancelled|Test session timed out/.test(line)) aborted = true;
  }
  if (names.size === 0 && aborted && active) names.add(`${active} (session aborted while it ran)`);
  return [...names];
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

// `source` reads GitHub: `json(path)` returns a REST response (paginated paths return every item), `log(jobId)` a job log.
// The CLI passes ghSource; the tests pass recorded responses.
export async function collect(source, { repository = defaultRepository, since, concurrency = 4 }) {
  const day = since.toISOString().slice(0, 10);
  const runs = (await source.json(`repos/${repository}/actions/workflows/ci.yml/runs?created=%3E%3D${day}&per_page=100`))
    .filter(run => new Date(run.created_at) >= since && run.status === "completed")
    // A run that passed on a later attempt failed before it: rerun flakes count too.
    .filter(run => run.conclusion === "failure" || run.run_attempt > 1);
  const failures = (await mapLimited(runs, concurrency, async run => {
    const jobs = await source.json(`repos/${repository}/actions/runs/${run.id}/jobs?filter=all&per_page=100`);
    return mapLimited(jobs.filter(job => job.conclusion === "failure" && !aggregateJobs.has(job.name)), concurrency, async job => {
      const step = job.steps?.find(item => item.conclusion === "failure")?.name ?? "(no failed step)";
      const tests = failingTests(await source.log(job.id).catch(() => ""));
      return (tests.length ? tests : [`step: ${step}`]).map(name => ({
        name, test: tests.length > 0, job: job.name, step, run: run.id, attempt: job.run_attempt ?? run.run_attempt,
        event: run.event, branch: run.head_branch, pullRequests: (run.pull_requests ?? []).map(item => item.number),
        url: job.html_url, at: job.started_at ?? run.created_at,
      }));
    });
  })).flat(2);
  // Issues and pull requests updated in the last 90 days, opened or closed, to match names against.
  const issueSince = new Date(since.getTime() - 90 * dayMs).toISOString();
  const issues = (await source.json(`repos/${repository}/issues?state=all&since=${issueSince}&per_page=100`))
    .filter(issue => !issue.pull_request)
    .map(issue => ({ number: issue.number, state: issue.state, closedAt: issue.closed_at ?? null, title: issue.title,
      text: `${issue.title}\n${issue.body ?? ""}`, url: issue.html_url }));
  return { repository, since: since.toISOString(), runs: runs.length, failures, issues };
}

// Group failures by job and name. A name's issues are those whose title or body mention it (a test name, or a probe
// check). Step-only failures match no issue: their log names no test, so a person reads it. The status of a name:
//   known            an open issue mentions it: a known flake (a queue entry failing only in known flakes is re-queued);
//   needs-issue      seen in `min` or more runs and no issue mentions it: file one today;
//   regressed        only closed issues mention it, and it failed after the last of them closed: reopen or file one;
//   fixed            only closed issues mention it, and every sighting predates the last closing;
//   repeated-step    seen in `min` or more runs, but the logs name no test: read one (a real break or a flake);
//   first-sighting   seen once.
export function report({ failures, issues, runs, since, repository }, { min = 2 } = {}) {
  const groups = new Map();
  for (const failure of failures) {
    const key = `${failure.job} › ${failure.name}`;
    const group = groups.get(key) ?? { key, job: failure.job, name: failure.name, test: failure.test, sightings: [] };
    if (!group.sightings.some(item => item.run === failure.run && item.attempt === failure.attempt)) group.sightings.push(failure);
    groups.set(key, group);
  }
  const mentions = name => {
    const needle = name.replace(/ \(session aborted while it ran\)$/, "");
    return issues.filter(issue => issue.text.includes(needle));
  };
  const all = [...groups.values()].map(group => {
    const matched = group.test ? mentions(group.name) : [];
    const open = matched.filter(issue => issue.state === "open");
    const runs = new Set(group.sightings.map(item => `${item.run}/${item.attempt}`)).size;
    const closed = matched.map(issue => issue.closedAt).filter(Boolean).sort().at(-1);
    const status = open.length ? "known"
      : closed ? (group.sightings.some(item => item.at > closed) ? "regressed" : "fixed")
      : runs < min ? "first-sighting" : group.test ? "needs-issue" : "repeated-step";
    return { ...group, runs, status, issues: matched.map(({ number, state, title, url }) => ({ number, state, title, url })) };
  }).sort((a, b) => b.runs - a.runs || a.key.localeCompare(b.key));
  const known = new Set(all.filter(group => group.status === "known").map(group => group.key));
  // Each failed merge queue run: re-queue when every failure in it is a known flake.
  const queue = [...new Set(failures.filter(item => item.event === "merge_group").map(item => item.run))].map(run => {
    const keys = [...new Set(failures.filter(item => item.run === run).map(item => `${item.job} › ${item.name}`))];
    const first = failures.find(item => item.run === run);
    return { run, branch: first.branch, url: first.url.replace(/\/job\/\d+$/, ""), verdict: keys.every(key => known.has(key)) ? "re-queue" : "investigate", failures: keys };
  });
  return { repository, since, runs, groups: all, queue };
}

const sighting = item => `${item.event}${item.pullRequests.length ? ` #${item.pullRequests.join(",#")}` : item.event === "merge_group"
  ? ` ${item.branch.replace(/^gh-readonly-queue\/main\/(pr-\d+).*/, "$1")}` : ` ${item.branch}`} (${item.at.slice(0, 16).replace("T", " ")}Z) ${item.url}`;

export function format(result) {
  const section = (title, groups, describe) => groups.length === 0 ? [] : [`${title}:`, ...groups.flatMap(group => [
    `  ${group.key}: ${group.runs} run${group.runs === 1 ? "" : "s"}${describe(group)}`,
    ...group.sightings.map(item => `    ${sighting(item)}`),
  ]), ""];
  const by = status => result.groups.filter(group => group.status === status);
  const issueList = group => group.issues.map(issue => ` #${issue.number} (${issue.state})`).join(",");
  return [
    `${result.repository}: ${result.runs} failed or rerun ci.yml runs since ${result.since.slice(0, 16).replace("T", " ")}Z`, "",
    ...section("Seen twice or more, no issue mentions it (file one today; fix or skip it, linking the issue, within the cycle)", by("needs-issue"), () => ""),
    ...section("Failed again after its issue closed (reopen it or file one)", by("regressed"), group => ` —${issueList(group)}`),
    ...section("Seen twice or more in a step whose log names no test (read one: a real break or a flake)", by("repeated-step"), () => ""),
    ...section("Known flakes (an open issue mentions them)", by("known"), group => ` —${issueList(group)}`),
    ...section("Fixed: only closed issues mention them, and no failure since they closed", by("fixed"), group => ` —${issueList(group)}`),
    ...section("Seen once", by("first-sighting"), group => group.test ? "" : " (the log names no test; read it)"),
    ...result.queue.length ? ["Failed merge queue entries:", ...result.queue.map(entry =>
      `  ${entry.verdict.padEnd(11)} ${entry.branch.replace(/^gh-readonly-queue\/main\//, "")} ${entry.url}`), ""] : [],
  ].join("\n").trimEnd();
}

const exec = promisify(execFile);
const gh = async (...args) => (await exec("gh", ["api", ...args], { maxBuffer: 512 * 1024 * 1024 })).stdout;
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
