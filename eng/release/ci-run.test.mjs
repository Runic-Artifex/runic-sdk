import { test, expect } from 'bun:test';
import { successfulCiRuns, selectCiRun, missingReleaseJobs, selectArtifact, findCiPackages, refuseTrainFeed, QUEUE_BRANCH } from './ci-run.mjs';
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
  // win-x64, win-arm64 and osx-arm64 run as two shards each (C2 of D077.25, R1 of D077.26); every shard must pass.
  expect(releaseJobs).toEqual(['Native / win-x64 (window)', 'Native / win-x64 (platform)', 'Native / win-arm64 (window)',
    'Native / win-arm64 (platform)', 'Native / osx-arm64 (window)', 'Native / osx-arm64 (platform)', 'WPF navigation / win-x64',
    'WPF navigation / win-arm64', 'Packaging / windows', 'Packaging / macos']);
});
test('selects the newest successful push, scheduled, manual or merge queue run of ci.yml for the exact commit', async () => {
  expect(successfulCiRuns([run(1), run(3), run(2, {conclusion: 'failure'})], {repository, sha}).map(r => r.id)).toEqual([3, 1]);
  for (const event of ['schedule', 'workflow_dispatch'])
    expect((await selectCiRun([run(4, {event})], {repository, sha}, jobs({}))).id).toBe(4);
  for (const other of [{head_sha: 'b'.repeat(40)}, {event: 'pull_request'}, {head_branch: 'feature'},
    {event: 'merge_group'}, {event: 'merge_group', head_branch: 'gh-readonly-queue/release/pr-7-' + 'c'.repeat(40)},
    {path: '.github/workflows/publish-preview.yml'}, {head_repository: {full_name: 'fork/runic-sdk'}}])
    expect(() => successfulCiRuns([run(9, other)], {repository, sha})).toThrow('No run of .github/workflows/ci.yml on main or in its merge queue');
});
// D077.22: the queue moves main to exactly the commit its run tested.
const queued = (id, extra = {}) => run(id, {event: 'merge_group', head_branch: `${QUEUE_BRANCH}pr-7-${'c'.repeat(40)}`, ...extra});
test('a passed merge queue run of the commit counts as its main run once the commit is on main', async () => {
  expect(successfulCiRuns([queued(5)], {repository, sha}).map(r => r.id)).toEqual([5]);
  const onMain = value => async () => value;
  expect((await selectCiRun([queued(5)], {repository, sha}, jobs({}), onMain(true))).id).toBe(5);
  await expect(selectCiRun([queued(5)], {repository, sha}, jobs({}), onMain(false))).rejects.toThrow(`${sha} is not on main`);
  await expect(selectCiRun([queued(5)], {repository, sha}, jobs({}))).rejects.toThrow('is not on main');
  // The push run that the queue run covered ran no Windows or macOS leg (eng/ci/covered.mjs): the queue run is taken.
  const covered = jobs({8: complete().map(job => ({...job, conclusion: 'skipped'}))});
  expect((await selectCiRun([run(8), queued(5)], {repository, sha}, covered, onMain(true))).id).toBe(5);
  // A queue run that planned only some legs (not a release entry) is not enough either.
  await expect(selectCiRun([queued(5)], {repository, sha}, jobs({5: without('Native / osx-arm64 (platform)', 'skipped')}), onMain(true)))
    .rejects.toThrow('Native / osx-arm64 (platform)=skipped');
  // A failed queue run of the commit is reported like a failed main run.
  expect(() => successfulCiRuns([queued(5, {conclusion: 'failure'})], {repository, sha})).toThrow('concluded failure');
});
test('fails clearly when CI for the commit is missing, running or failed', () => {
  expect(() => successfulCiRuns([], {repository, sha})).toThrow(`No run of .github/workflows/ci.yml on main or in its merge queue exists for ${sha}`);
  expect(() => successfulCiRuns([run(4, {status: 'in_progress', conclusion: null})], {repository, sha})).toThrow('still in_progress');
  expect(() => successfulCiRuns([run(5, {conclusion: 'failure'})], {repository, sha})).toThrow('concluded failure');
});
test('a run that skipped or lacks a Windows or macOS leg is rejected, and an older complete run is taken', async () => {
  expect(missingReleaseJobs(complete())).toEqual([]);
  expect(missingReleaseJobs(without('Native / win-arm64 (platform)', 'skipped'))).toEqual(['Native / win-arm64 (platform)=skipped']);
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
    const artifacts = [{id: 80, name: 'runic-sdk-8', expired: false, workflow_run: {id: 8, head_sha: sha}}];
    return Response.json(url.includes('/artifacts') ? {artifacts: artifacts.filter(a => a.name === new URL(url).searchParams.get('name'))}
      : url.includes('/jobs') ? {jobs: complete()} : {workflow_runs: new URL(url).searchParams.has('event') ? [] : [run(8)]});
  };
  expect(await findCiPackages({repository, sha, token: 't', fetchImpl})).toEqual({runId: '8', runUrl: run(8).html_url, artifact: 'runic-sdk-8', artifactId: '80'});
  expect(requests[0].url.pathname).toBe(`/repos/${repository}/actions/workflows/ci.yml/runs`);
  expect(Object.fromEntries(requests[0].url.searchParams)).toMatchObject({head_sha: sha, branch: 'main'});
  expect(requests[0].url.searchParams.has('event')).toBe(false);
  // The merge queue runs of the commit.
  expect(Object.fromEntries(requests[1].url.searchParams)).toMatchObject({head_sha: sha, event: 'merge_group'});
  expect(requests[1].url.searchParams.has('branch')).toBe(false);
  expect(requests[2].url.pathname).toBe(`/repos/${repository}/actions/runs/8/jobs`);
  expect(Object.fromEntries(requests[3].url.searchParams)).toEqual({name: 'train-feed'});
  expect(requests.every(r => r.auth === 'Bearer t')).toBe(true);
  await expect(findCiPackages({repository, sha, token: 't', fetchImpl: async () => new Response('', {status: 403})})).rejects.toThrow('failed: 403');
  await expect(findCiPackages({repository, sha: 'main', token: 't', fetchImpl})).rejects.toThrow('full commit SHA');
});
test('finds a merge queue run of a commit on main through the compare API', async () => {
  const requests = [];
  const fetchFor = status => async url => {
    const {pathname, searchParams} = new URL(url);
    requests.push(pathname);
    if (pathname.endsWith(`/compare/${sha}...main`)) return status ? Response.json({status}) : new Response('', {status: 404});
    if (pathname.endsWith('/artifacts')) return Response.json({artifacts: searchParams.get('name') === 'runic-sdk-5'
      ? [{id: 50, name: 'runic-sdk-5', expired: false, workflow_run: {id: 5, head_sha: sha}}] : []});
    if (pathname.endsWith('/jobs')) return Response.json({jobs: pathname.includes('/runs/8/') ? complete().map(job => ({...job, conclusion: 'skipped'})) : complete()});
    return Response.json({workflow_runs: searchParams.get('event') === 'merge_group' ? [queued(5)] : [run(8)]});
  };
  for (const status of ['identical', 'ahead'])
    expect(await findCiPackages({repository, sha, token: 't', fetchImpl: fetchFor(status)})).toEqual({runId: '5', runUrl: queued(5).html_url, artifact: 'runic-sdk-5', artifactId: '50'});
  expect(requests).toContain(`/repos/${repository}/compare/${sha}...main`);
  for (const status of ['behind', 'diverged', null])
    await expect(findCiPackages({repository, sha, token: 't', fetchImpl: fetchFor(status)})).rejects.toThrow('is not on main');
});
test('refuses a CI run built against the train feed', async () => {
  expect(() => refuseTrainFeed([{name: 'runic-sdk-8'}], run(8))).not.toThrow();
  expect(() => refuseTrainFeed([{name: 'train-feed'}], run(8))).toThrow('cannot be published');
  const fetchImpl = async url => Response.json(url.includes('/artifacts') ? {artifacts: [{id: 81, name: 'train-feed'}]}
    : url.includes('/jobs') ? {jobs: complete()} : {workflow_runs: [run(8)]});
  await expect(findCiPackages({repository, sha, token: 't', fetchImpl})).rejects.toThrow('built against the train-feed');
});
