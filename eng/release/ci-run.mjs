// Find a successful run of ci.yml on main for this exact commit that ran every Windows and
// macOS leg, and the package artifact it uploaded. Publication reuses those verified bytes
// instead of rerunning the tests, packing and package verification.
// Pull requests and pushes run only the Windows and macOS legs their changes need
// (eng/ci/platforms.mjs), so a push run may lack some; a scheduled (nightly) or manual run
// of main runs all of them. A release thus keeps the guarantee that its commit passed CI on
// every platform.
// Runs with the runner's Node before any toolchain setup: Node built-ins only.
import assert from 'node:assert/strict';
import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { releaseJobs } from '../ci/platforms.mjs';

export const CI_WORKFLOW = '.github/workflows/ci.yml';
export const CI_EVENTS = ['push', 'schedule', 'workflow_dispatch'];
export const artifactName = runId => `runic-sdk-${runId}`;
const dispatch = `Dispatch SDK CI on main (gh workflow run ci.yml --ref main) and publish after it succeeds.`;

// The successful runs of ci.yml on main for the commit, newest first.
export function successfulCiRuns(runs, {repository, sha}) {
  const candidates = runs.filter(run => run.head_sha === sha && CI_EVENTS.includes(run.event) && run.head_branch === 'main'
    && run.path?.split('@')[0] === CI_WORKFLOW && run.repository?.full_name === repository
    && run.head_repository?.full_name === repository);
  const green = candidates.filter(run => run.status === 'completed' && run.conclusion === 'success').sort((a, b) => b.id - a.id);
  if (green.length) return green;
  const latest = candidates.sort((a, b) => b.id - a.id)[0];
  if (!latest) throw new Error(`No run of ${CI_WORKFLOW} on main exists for ${sha}. Publish only a commit on main whose CI succeeded.`);
  if (latest.status !== 'completed') throw new Error(`CI for ${sha} is still ${latest.status} (${latest.html_url}). Dispatch again after it succeeds.`);
  throw new Error(`CI for ${sha} concluded ${latest.conclusion} (${latest.html_url}). Rerun its failed jobs; publish needs a successful run.`);
}

// The Windows and macOS legs a run did not pass, as `name=conclusion`.
export function missingReleaseJobs(jobs) {
  return releaseJobs.filter(name => !jobs.some(job => job.name === name && job.conclusion === 'success'))
    .map(name => `${name}=${jobs.find(job => job.name === name)?.conclusion ?? 'missing'}`);
}

// The newest successful run that passed every Windows and macOS leg. `jobsOf(run)` lists its jobs.
export async function selectCiRun(runs, {repository, sha}, jobsOf) {
  const green = successfulCiRuns(runs, {repository, sha});
  const missing = [];
  for (const run of green) {
    const absent = missingReleaseJobs(await jobsOf(run));
    if (!absent.length) return run;
    missing.push(`${run.html_url} (${run.event}): ${absent.join(', ')}`);
  }
  throw new Error(`No successful CI run for ${sha} ran every Windows and macOS job; ${missing.join('; ')}. ${dispatch}`);
}

export function selectArtifact(artifacts, run) {
  const name = artifactName(run.id);
  const found = artifacts.filter(a => a.name === name);
  if (found.length !== 1) throw new Error(`CI run ${run.html_url} has no single ${name} package artifact.`);
  const [artifact] = found;
  if (artifact.expired) throw new Error(`The ${name} artifact of ${run.html_url} has expired. Rerun all jobs of that CI run to regenerate it, or prepare a new version.`);
  assert.equal(artifact.workflow_run?.id, run.id, 'Artifact belongs to a different run');
  assert.equal(artifact.workflow_run?.head_sha, run.head_sha, 'Artifact belongs to a different commit');
  assert(Number.isSafeInteger(artifact.id) && artifact.id > 0, 'Artifact has no id');
  return artifact;
}

export async function findCiPackages({repository, sha, token, fetchImpl = fetch}) {
  assert.match(sha, /^[a-f0-9]{40}$/, 'Expected a full commit SHA');
  const api = async path => {
    const response = await fetchImpl(`https://api.github.com/repos/${repository}/${path}`, {headers: {
      accept: 'application/vnd.github+json', authorization: `Bearer ${token}`, 'x-github-api-version': '2022-11-28'}});
    if (!response.ok) throw new Error(`GitHub API ${path.split('?')[0]} failed: ${response.status}`);
    return response.json();
  };
  const query = new URLSearchParams({head_sha: sha, branch: 'main', per_page: '100'});
  // The latest attempt's jobs; a full run has fewer than 100.
  const jobsOf = async run => (await api(`actions/runs/${run.id}/jobs?per_page=100`)).jobs;
  const run = await selectCiRun((await api(`actions/workflows/ci.yml/runs?${query}`)).workflow_runs, {repository, sha}, jobsOf);
  const artifact = selectArtifact((await api(`actions/runs/${run.id}/artifacts?name=${artifactName(run.id)}`)).artifacts, run);
  // Download by id: re-uploading under the same name creates a new id, so the bytes
  // downloaded later are exactly the artifact selected here.
  return {runId: String(run.id), runUrl: run.html_url, artifact: artifact.name, artifactId: String(artifact.id)};
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const {GITHUB_REPOSITORY: repository, GITHUB_SHA: sha, GH_TOKEN: token, GITHUB_OUTPUT: output} = process.env;
  assert(repository && sha && token && output, 'Run in GitHub Actions with GH_TOKEN');
  try {
    const found = await findCiPackages({repository, sha, token});
    appendFileSync(output, `run-id=${found.runId}\nartifact-id=${found.artifactId}\n`);
    console.log(`Reusing ${found.artifact} (artifact ${found.artifactId}) from ${found.runUrl}`);
  } catch (error) {
    console.log(`::error title=No reusable CI packages::${error.message}`);
    process.exit(1);
  }
}
