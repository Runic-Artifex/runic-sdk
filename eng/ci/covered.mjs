// A1 (D077.25; eng/ci/README.md, "Merge queue"): the merge queue moves main to exactly the commit its run tested, so a
// push run on main finds that run and runs only what it skipped. Prints `{ url, plan }` for the newest passed ci.yml
// merge_group run of the commit (or, when that entry reused a passed pull request run of the same base and tree, that
// run), with the plan its ci-plan artifact records; prints nothing when there is none. The plan step then plans with
// `--covered`. A failed lookup prints nothing, so the push plans as before: it can only run more, never skip more.
//
//   bun eng/ci/covered.mjs <commit>        (GH_TOKEN, GITHUB_REPOSITORY)
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

// `api(path)` answers GitHub REST paths under repos/<repository>/ with JSON; `planOf(run)` returns the run's ci-plan
// record or null.
export async function coveringRun({ repository, sha, api, planOf }) {
  if (!/^[0-9a-f]{40}$/.test(sha ?? "")) throw new Error(`Expected a full commit SHA, not '${sha}'`);
  const ours = (run, event) => run?.event === event && run.path?.split("@")[0] === CI_WORKFLOW
    && run.repository?.full_name === repository && run.head_repository?.full_name === repository;
  // The latest attempt's `verify`: it passes only when every planned job passed.
  const verified = async run => (await api(`actions/runs/${run.id}/jobs?per_page=100`)).jobs?.some(job => job.name === "verify" && job.conclusion === "success");
  const { workflow_runs: runs = [] } = await api(`actions/workflows/ci.yml/runs?event=merge_group&head_sha=${sha}&per_page=20`);
  for (const run of [...runs].sort((a, b) => b.id - a.id)) {
    if (!ours(run, "merge_group") || run.head_sha !== sha || !await verified(run)) continue;
    const record = await planOf(run);
    if (!record || record.schema !== 1) continue;
    if (record.reused === null && validPlan(record.plan)) return { url: run.html_url, plan: record.plan };
    // The entry ran nothing: the pull request run it reused tested the same tree on the same base.
    const id = runId(record.reused);
    if (!id) continue;
    const reused = await api(`actions/runs/${id}`);
    if (!ours(reused, "pull_request") || !await verified(reused)) continue;
    const tested = await planOf(reused);
    if (tested?.schema === 1 && tested.reused === null && validPlan(tested.plan))
      return { url: `${run.html_url} (which reused ${reused.html_url})`, plan: tested.plan };
  }
  return null;
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
  const { GITHUB_REPOSITORY: repository, GH_TOKEN: token } = process.env;
  const sha = process.argv[2];
  try {
    if (!repository || !token) throw new Error("GH_TOKEN and GITHUB_REPOSITORY are required");
    const found = await coveringRun({ repository, sha, ...github(repository, token) });
    if (found) {
      console.error(`Merge queue run ${found.url} passed ${sha}.`);
      console.log(JSON.stringify(found));
    } else console.error(`No passed merge queue run with a plan record tested ${sha}; planning as a push without one.`);
  } catch (error) {
    console.error(`::warning title=Merge queue run not looked up::${error.message}; planning as a push without one.`);
  }
}
