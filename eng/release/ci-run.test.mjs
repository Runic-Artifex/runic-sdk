import { test, expect } from 'bun:test';
import { successfulCiRuns, selectCiRun, missingReleaseJobs, selectArtifact, findCiPackages } from './ci-run.mjs';
import { releaseJobs } from '../ci/platforms.mjs';
const repository = 'Runic-Artifex/runic-sdk', sha = 'a'.repeat(40);
const run = (id, extra = {}) => ({id, head_sha: sha, event: 'push', head_branch: 'main', path: '.github/workflows/ci.yml',
  repository: {full_name: repository}, head_repository: {full_name: repository}, status: 'completed', conclusion: 'success',
  html_url: `https://github.com/${repository}/actions/runs/${id}`, ...extra});
// Every Windows and macOS leg passed, plus a Linux job.
const complete = () => [...releaseJobs, 'Native / linux-x64'].map(name => ({name, conclusion: 'success'}));
const without = (name, conclusion) => complete().flatMap(job => job.name !== name ? [job] : conclusion ? [{name, conclusion}] : []);
const jobs = byRun => async run => byRun[run.id] ?? complete();

test('the release jobs are every Windows and macOS leg of the native, WPF and packaging jobs', () => {
  expect(releaseJobs).toEqual(['Native / win-x64', 'Native / win-arm64', 'Native / osx-arm64', 'WPF navigation / win-x64',
    'WPF navigation / win-arm64', 'Packaging / windows', 'Packaging / macos']);
});
test('selects the newest successful push, scheduled or manual run of ci.yml on main for the exact commit', async () => {
  expect(successfulCiRuns([run(1), run(3), run(2, {conclusion: 'failure'})], {repository, sha}).map(r => r.id)).toEqual([3, 1]);
  for (const event of ['schedule', 'workflow_dispatch'])
    expect((await selectCiRun([run(4, {event})], {repository, sha}, jobs({}))).id).toBe(4);
  // A merge queue run tests the same commit before main has it; the push run that follows counts.
  for (const other of [{head_sha: 'b'.repeat(40)}, {event: 'pull_request'}, {head_branch: 'feature'},
    {event: 'merge_group', head_branch: 'gh-readonly-queue/main/pr-7-' + 'c'.repeat(40)}, {event: 'merge_group'},
    {path: '.github/workflows/publish-preview.yml'}, {head_repository: {full_name: 'fork/runic-sdk'}}])
    expect(() => successfulCiRuns([run(9, other)], {repository, sha})).toThrow('No run of .github/workflows/ci.yml on main');
});
test('fails clearly when CI for the commit is missing, running or failed', () => {
  expect(() => successfulCiRuns([], {repository, sha})).toThrow(`No run of .github/workflows/ci.yml on main exists for ${sha}`);
  expect(() => successfulCiRuns([run(4, {status: 'in_progress', conclusion: null})], {repository, sha})).toThrow('still in_progress');
  expect(() => successfulCiRuns([run(5, {conclusion: 'failure'})], {repository, sha})).toThrow('concluded failure');
});
test('a run that skipped or lacks a Windows or macOS leg is rejected, and an older complete run is taken', async () => {
  expect(missingReleaseJobs(complete())).toEqual([]);
  expect(missingReleaseJobs(without('Native / win-arm64', 'skipped'))).toEqual(['Native / win-arm64=skipped']);
  expect(missingReleaseJobs(without('Packaging / macos'))).toEqual(['Packaging / macos=missing']);
  // A push run that planned only some legs, and a later manual run of the same commit with all of them.
  const partial = jobs({7: without('WPF navigation / win-arm64', 'skipped')});
  expect((await selectCiRun([run(7), run(6, {event: 'workflow_dispatch'})], {repository, sha}, partial)).id).toBe(6);
  const failure = selectCiRun([run(7)], {repository, sha}, partial);
  await expect(failure).rejects.toThrow('WPF navigation / win-arm64=skipped');
  await expect(selectCiRun([run(7)], {repository, sha}, partial)).rejects.toThrow('Dispatch SDK CI on main');
});
test('requires the single unexpired package artifact of that run', () => {
  const artifact = {id: 70, name: 'runic-sdk-7', expired: false, workflow_run: {id: 7, head_sha: sha}};
  expect(selectArtifact([artifact, {...artifact, name: 'sdk-build-7'}], run(7))).toBe(artifact);
  expect(() => selectArtifact([], run(7))).toThrow('no single runic-sdk-7');
  expect(() => selectArtifact([{...artifact, expired: true}], run(7))).toThrow('expired');
  expect(() => selectArtifact([{...artifact, workflow_run: {id: 7, head_sha: 'c'.repeat(40)}}], run(7))).toThrow();
  expect(() => selectArtifact([{...artifact, id: undefined}], run(7))).toThrow('no id');
});
test('queries GitHub for runs of the commit, their jobs, and reports the run and artifact to download', async () => {
  const requests = [];
  const fetchImpl = async (url, init) => {
    requests.push({url: new URL(url), auth: init.headers.authorization});
    return Response.json(url.includes('/artifacts') ? {artifacts: [{id: 80, name: 'runic-sdk-8', expired: false, workflow_run: {id: 8, head_sha: sha}}]}
      : url.includes('/jobs') ? {jobs: complete()} : {workflow_runs: [run(8)]});
  };
  expect(await findCiPackages({repository, sha, token: 't', fetchImpl})).toEqual({runId: '8', runUrl: run(8).html_url, artifact: 'runic-sdk-8', artifactId: '80'});
  expect(requests[0].url.pathname).toBe(`/repos/${repository}/actions/workflows/ci.yml/runs`);
  expect(Object.fromEntries(requests[0].url.searchParams)).toMatchObject({head_sha: sha, branch: 'main'});
  expect(requests[0].url.searchParams.has('event')).toBe(false);
  expect(requests[1].url.pathname).toBe(`/repos/${repository}/actions/runs/8/jobs`);
  expect(requests.every(r => r.auth === 'Bearer t')).toBe(true);
  await expect(findCiPackages({repository, sha, token: 't', fetchImpl: async () => new Response('', {status: 403})})).rejects.toThrow('failed: 403');
  await expect(findCiPackages({repository, sha: 'main', token: 't', fetchImpl})).rejects.toThrow('full commit SHA');
});
