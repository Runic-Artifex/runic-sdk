// Find the successful push run of ci.yml on main for this exact commit and the
// package artifact it uploaded. Publication reuses those verified bytes instead
// of rerunning the tests, packing and package verification.
// Runs with the runner's Node before any toolchain setup: Node built-ins only.
import assert from 'node:assert/strict';
import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export const CI_WORKFLOW = '.github/workflows/ci.yml';
export const artifactName = runId => `runic-sdk-${runId}`;

export function selectCiRun(runs, {repository, sha}) {
  const candidates = runs.filter(run => run.head_sha === sha && run.event === 'push' && run.head_branch === 'main'
    && run.path?.split('@')[0] === CI_WORKFLOW && run.repository?.full_name === repository
    && run.head_repository?.full_name === repository);
  const green = candidates.filter(run => run.status === 'completed' && run.conclusion === 'success').sort((a, b) => b.id - a.id);
  if (green.length) return green[0];
  const latest = candidates.sort((a, b) => b.id - a.id)[0];
  if (!latest) throw new Error(`No push run of ${CI_WORKFLOW} on main exists for ${sha}. Publish only a commit on main whose CI succeeded.`);
  if (latest.status !== 'completed') throw new Error(`CI for ${sha} is still ${latest.status} (${latest.html_url}). Dispatch again after it succeeds.`);
  throw new Error(`CI for ${sha} concluded ${latest.conclusion} (${latest.html_url}). Rerun its failed jobs; publish needs a successful run.`);
}

export function selectArtifact(artifacts, run) {
  const name = artifactName(run.id);
  const found = artifacts.filter(a => a.name === name);
  if (found.length !== 1) throw new Error(`CI run ${run.html_url} has no single ${name} package artifact.`);
  const [artifact] = found;
  if (artifact.expired) throw new Error(`The ${name} artifact of ${run.html_url} has expired. Rerun all jobs of that CI run to regenerate it, or prepare a new version.`);
  assert.equal(artifact.workflow_run?.id, run.id, 'Artifact belongs to a different run');
  assert.equal(artifact.workflow_run?.head_sha, run.head_sha, 'Artifact belongs to a different commit');
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
  const query = new URLSearchParams({head_sha: sha, event: 'push', branch: 'main', per_page: '100'});
  const run = selectCiRun((await api(`actions/workflows/ci.yml/runs?${query}`)).workflow_runs, {repository, sha});
  const artifact = selectArtifact((await api(`actions/runs/${run.id}/artifacts?name=${artifactName(run.id)}`)).artifacts, run);
  return {runId: String(run.id), runUrl: run.html_url, artifact: artifact.name};
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const {GITHUB_REPOSITORY: repository, GITHUB_SHA: sha, GH_TOKEN: token, GITHUB_OUTPUT: output} = process.env;
  assert(repository && sha && token && output, 'Run in GitHub Actions with GH_TOKEN');
  try {
    const found = await findCiPackages({repository, sha, token});
    appendFileSync(output, `run-id=${found.runId}\nartifact=${found.artifact}\n`);
    console.log(`Reusing ${found.artifact} from ${found.runUrl}`);
  } catch (error) {
    console.log(`::error title=No reusable CI packages::${error.message}`);
    process.exit(1);
  }
}
