import { test, expect } from 'bun:test';
import { selectCiRun, selectArtifact, findCiPackages } from './ci-run.mjs';
const repository = 'Runic-Artifex/runic-sdk', sha = 'a'.repeat(40);
const run = (id, extra = {}) => ({id, head_sha: sha, event: 'push', head_branch: 'main', path: '.github/workflows/ci.yml',
  repository: {full_name: repository}, head_repository: {full_name: repository}, status: 'completed', conclusion: 'success',
  html_url: `https://github.com/${repository}/actions/runs/${id}`, ...extra});
test('selects the newest successful push run of ci.yml on main for the exact commit', () => {
  expect(selectCiRun([run(1), run(3), run(2, {conclusion: 'failure'})], {repository, sha}).id).toBe(3);
  for (const other of [{head_sha: 'b'.repeat(40)}, {event: 'workflow_dispatch'}, {event: 'pull_request'}, {head_branch: 'feature'},
    {path: '.github/workflows/publish-preview.yml'}, {head_repository: {full_name: 'fork/runic-sdk'}}])
    expect(() => selectCiRun([run(9, other)], {repository, sha})).toThrow('No push run');
});
test('fails clearly when CI for the commit is missing, running or failed', () => {
  expect(() => selectCiRun([], {repository, sha})).toThrow(`No push run of .github/workflows/ci.yml on main exists for ${sha}`);
  expect(() => selectCiRun([run(4, {status: 'in_progress', conclusion: null})], {repository, sha})).toThrow('still in_progress');
  expect(() => selectCiRun([run(5, {conclusion: 'failure'})], {repository, sha})).toThrow('concluded failure');
});
test('requires the single unexpired package artifact of that run', () => {
  const artifact = {name: 'runic-sdk-7', expired: false, workflow_run: {id: 7, head_sha: sha}};
  expect(selectArtifact([artifact, {...artifact, name: 'sdk-build-7'}], run(7))).toBe(artifact);
  expect(() => selectArtifact([], run(7))).toThrow('no single runic-sdk-7');
  expect(() => selectArtifact([{...artifact, expired: true}], run(7))).toThrow('expired');
  expect(() => selectArtifact([{...artifact, workflow_run: {id: 7, head_sha: 'c'.repeat(40)}}], run(7))).toThrow();
});
test('queries GitHub for push runs of the commit and reports the run and artifact to download', async () => {
  const requests = [];
  const fetchImpl = async (url, init) => {
    requests.push({url: new URL(url), auth: init.headers.authorization});
    return Response.json(url.includes('/artifacts') ? {artifacts: [{name: 'runic-sdk-8', expired: false, workflow_run: {id: 8, head_sha: sha}}]}
      : {workflow_runs: [run(8)]});
  };
  expect(await findCiPackages({repository, sha, token: 't', fetchImpl})).toEqual({runId: '8', runUrl: run(8).html_url, artifact: 'runic-sdk-8'});
  expect(requests[0].url.pathname).toBe(`/repos/${repository}/actions/workflows/ci.yml/runs`);
  expect(Object.fromEntries(requests[0].url.searchParams)).toMatchObject({head_sha: sha, event: 'push', branch: 'main'});
  expect(requests.every(r => r.auth === 'Bearer t')).toBe(true);
  await expect(findCiPackages({repository, sha, token: 't', fetchImpl: async () => new Response('', {status: 403})})).rejects.toThrow('failed: 403');
  await expect(findCiPackages({repository, sha: 'main', token: 't', fetchImpl})).rejects.toThrow('full commit SHA');
});
