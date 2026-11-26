// A1 (D077.25; eng/ci/README.md, "Merge queue"): the merge queue moves main to exactly the commit its run tested, so a
// push run on main finds that run and runs only what it skipped. Prints `{ url, plan }` for the newest passed ci.yml
// merge_group run of the commit (or, when that entry reused a passed pull request run of the same base and tree, that
// run), with the plan its ci-plan artifact records; prints nothing when there is none. The plan step then plans with
// `--covered`. A failed lookup prints nothing, so the push plans as before: it can only run more, never skip more.
//
// Coalesced pushes: when entries merge close together, main's concurrency group supersedes the earlier push runs, and
// the surviving push's before..after diff spans several queue entries. When every commit of before..after has such a
// run, the push is covered by the union of their plans (unionPlan); otherwise only the pushed commit's run covers it.
// GitHub also merges an earlier entry as soon as a later entry's run passes, while the earlier entry's own run may still
// be running: the push then waits up to CI_COVER_WAIT_MINUTES (default 0; ci.yml sets it for pushes) for that run to
// finish rather than run its jobs a second time.
//
//   bun eng/ci/covered.mjs <commit> [<before>]        (GH_TOKEN, GITHUB_REPOSITORY, CI_COVER_WAIT_MINUTES)
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { zipEntries } from "../nupkg.mjs";
import { validPlan } from "./plan.mjs";

export const CI_WORKFLOW = ".github/workflows/ci.yml";
export const PLAN_ARTIFACT = "ci-plan";
export const PLAN_FILE = "plan.json";
const runId = url => /\/actions\/runs\/(\d+)(?:\/|$)/.exec(url ?? "")?.[1] ?? null;
const ours = (repository, run, event) => run?.event === event && run.path?.split("@")[0] === CI_WORKFLOW
  && run.repository?.full_name === repository && run.head_repository?.full_name === repository;
const queueRuns = async (api, sha) =>
  (await api(`actions/workflows/ci.yml/runs?event=merge_group&head_sha=${sha}&per_page=20`)).workflow_runs ?? [];

// `api(path)` answers GitHub REST paths under repos/<repository>/ with JSON; `planOf(run)` returns the run's ci-plan
// record or null.
export async function coveringRun({ repository, sha, api, planOf }) {
  if (!/^[0-9a-f]{40}$/.test(sha ?? "")) throw new Error(`Expected a full commit SHA, not '${sha}'`);
  // The latest attempt's `verify`: it passes only when every planned job passed.
  const verified = async run => (await api(`actions/runs/${run.id}/jobs?per_page=100`)).jobs?.some(job => job.name === "verify" && job.conclusion === "success");
  for (const run of [...await queueRuns(api, sha)].sort((a, b) => b.id - a.id)) {
    if (!ours(repository, run, "merge_group") || run.head_sha !== sha || !await verified(run)) continue;
    const record = await planOf(run);
    if (!record || record.schema !== 1) continue;
    if (record.reused === null && validPlan(record.plan)) return { url: run.html_url, plan: record.plan };
    // The entry ran nothing: the pull request run it reused tested the same tree on the same base.
    const id = runId(record.reused);
    if (!id) continue;
    const reused = await api(`actions/runs/${id}`);
    if (!ours(repository, reused, "pull_request") || !await verified(reused)) continue;
    const tested = await planOf(reused);
    if (tested?.schema === 1 && tested.reused === null && validPlan(tested.plan))
      return { url: `${run.html_url} (which reused ${reused.html_url})`, plan: tested.plan };
  }
  return null;
}

// The plan that ran whatever any of `plans` ran: a job is skipped only when every plan skipped it, and a job's suites
// and legs are those of the plans that ran it.
export function unionPlan(plans) {
  const ran = job => plans.filter(plan => !plan.skip.includes(job));
  const unique = list => [...new Map(list.map(item => [JSON.stringify(item), item])).values()];
  const legs = job => ({ include: unique(ran(job).flatMap(plan => plan[job].include)) });
  return {
    full: plans.some(plan => plan.full),
    components: unique(plans.flatMap(plan => plan.components)),
    managed: unique(ran("managed").flatMap(plan => plan.managed)),
    web: unique(ran("web").flatMap(plan => plan.web)),
    frameworks: ran("web").some(plan => plan.frameworks),
    native: legs("native"),
    wpf: legs("wpf"),
    packaging: legs("packaging"),
    "screen-readers": legs("screen-readers"),
    platforms: unique(plans.flatMap(plan => plan.platforms)),
    why: plans.map(plan => plan.why).join("; "),
    skip: plans[0].skip.filter(job => plans.every(plan => plan.skip.includes(job))),
  };
}

// A merge queue run of `sha` that has not completed: an earlier entry that GitHub merged when a later entry's run
// passed, while its own run still ran (2026-11-26: #1365's run passed three minutes after the push of #1365 and #1371
// planned, so that push ran #1365's Windows and macOS legs again).
export async function pendingRun({ repository, sha, api }) {
  return (await queueRuns(api, sha)).find(run => ours(repository, run, "merge_group") && run.head_sha === sha && run.status !== "completed") ?? null;
}

// The push of `sha` from `before`: the union of the covering runs of every commit of before..sha when each has one
// (main is linear: squash merges only), else the covering run of `sha` alone. An earlier commit whose queue run is still
// running is waited for, polling every `pollSeconds`, until `waitMinutes` after the lookup started. `api` also answers
// compare/<a>...<b>; `sleep`, `now` and `log` are for tests.
export async function coveringPush({ repository, sha, before, api, planOf, waitMinutes = 0, pollSeconds = 30,
  sleep = ms => new Promise(done => setTimeout(done, ms)), now = Date.now, log = message => console.error(message) }) {
  const deadline = now() + waitMinutes * 60_000;
  const own = await coveringRun({ repository, sha, api, planOf });
  if (!own || !/^[0-9a-f]{40}$/.test(before ?? "") || /^0+$/.test(before)) return own;
  const { status, total_commits: total, commits = [] } = await api(`compare/${before}...${sha}?per_page=100`);
  const shas = commits.map(commit => commit.sha);
  if (status !== "ahead" || total !== shas.length || shas.at(-1) !== sha || shas.length < 2) return own;
  const found = [];
  for (const commit of shas.slice(0, -1)) {
    let run = await coveringRun({ repository, sha: commit, api, planOf });
    for (let pending; !run && now() < deadline && (pending = await pendingRun({ repository, sha: commit, api }));) {
      const left = Math.ceil((deadline - now()) / 60_000);
      log(`Merge queue run ${pending.html_url} of ${commit.slice(0, 9)} is ${pending.status}; waiting for it (up to ${left} more min).`);
      await sleep(Math.max(0, Math.min(pollSeconds * 1000, deadline - now())));
      run = await coveringRun({ repository, sha: commit, api, planOf });
    }
    if (!run) return own;
    found.push({ ...run, sha: commit });
  }
  found.push({ ...own, sha });
  return {
    url: found.map(run => `${run.url} (${run.sha.slice(0, 9)})`).join(" and run "),
    plan: unionPlan(found.map(run => run.plan)),
  };
}

// The GitHub REST client of the CLI: GH_TOKEN, retrying server errors.
export function github(repository, token, fetchImpl = fetch) {
  const headers = { accept: "application/vnd.github+json", authorization: `Bearer ${token}`, "x-github-api-version": "2022-11-28",
    "user-agent": "runic-sdk-ci-plan" };
  const get = async url => {
    for (let attempt = 1; ; attempt++) {
      const response = await fetchImpl(url, { headers, redirect: "follow" });
      if (response.ok || attempt >= 3 || (response.status < 500 && response.status !== 429)) return response;
      await new Promise(done => setTimeout(done, 2000 * attempt));
    }
  };
  const api = async path => {
    const response = await get(`https://api.github.com/repos/${repository}/${path}`);
    if (!response.ok) throw new Error(`GitHub API ${path.split("?")[0]} failed: ${response.status}`);
    return response.json();
  };
  // The run's single unexpired ci-plan artifact, unzipped and parsed.
  const planOf = async run => {
    const { artifacts = [] } = await api(`actions/runs/${run.id}/artifacts?name=${PLAN_ARTIFACT}&per_page=10`);
    const live = artifacts.filter(item => item.name === PLAN_ARTIFACT && !item.expired && item.workflow_run?.id === run.id);
    if (live.length !== 1) return null;
    const response = await get(`https://api.github.com/repos/${repository}/actions/artifacts/${live[0].id}/zip`);
    if (!response.ok) return null;
    const directory = mkdtempSync(join(tmpdir(), "runic-ci-plan-"));
    try {
      const archive = join(directory, "plan.zip");
      writeFileSync(archive, Buffer.from(await response.arrayBuffer()));
      const entry = zipEntries(archive).find(item => item.name === PLAN_FILE);
      return entry ? JSON.parse(entry.read().toString("utf8")) : null;
    } finally { rmSync(directory, { recursive: true, force: true }); }
  };
  return { api, planOf };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { GITHUB_REPOSITORY: repository, GH_TOKEN: token, CI_COVER_WAIT_MINUTES: wait = "0" } = process.env;
  const [sha, before] = process.argv.slice(2);
  try {
    if (!repository || !token) throw new Error("GH_TOKEN and GITHUB_REPOSITORY are required");
    const waitMinutes = Number.parseInt(wait, 10);
    if (!Number.isSafeInteger(waitMinutes) || waitMinutes < 0) throw new Error(`CI_COVER_WAIT_MINUTES must be a whole number of minutes, not '${wait}'`);
    const found = await coveringPush({ repository, sha, before, waitMinutes, ...github(repository, token) });
    if (found) {
      console.error(`Merge queue run ${found.url} passed ${before && found.url.includes(" and run ") ? `${before.slice(0, 9)}..${sha}` : sha}.`);
      console.log(JSON.stringify(found));
    } else console.error(`No passed merge queue run with a plan record tested ${sha}; planning as a push without one.`);
  } catch (error) {
    console.error(`::warning title=Merge queue run not looked up::${error.message}; planning as a push without one.`);
  }
}
