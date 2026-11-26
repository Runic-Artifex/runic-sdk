// Resolves each repository's input (a published version or a CI run's package artifact) into the candidate
// packages of one gate run: their ids, versions, nuspecs and, for CI runs, local files with SHA-256 values.
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { zipEntries } from '../nupkg.mjs';
import { repos, repoOf, npmRepoOf, parseNuspec, nupkgNuspec } from './nuget.mjs';
import { compareVersions, parseVersion, releaseOf } from './versions.mjs';

const NUGET_SEARCH = 'https://azuresearch-usnc.nuget.org/query';
const FLAT = 'https://api.nuget.org/v3-flatcontainer';
const GITHUB = 'https://api.github.com';
export const NUGET_OWNER = 'runic-artifex';
export const CI_WORKFLOW = 'ci.yml';
const CI_EVENTS = ['push', 'schedule', 'workflow_dispatch'];
// A merge queue entry's ci.yml run: the queue moves main to exactly the commit it tested, so a passed run of a commit
// on main counts as that commit's main run (D077.22; eng/ci/README.md, "Merge queue"). Only runic-sdk has a queue.
export const QUEUE_EVENT = 'merge_group';
const QUEUE_BRANCH = 'gh-readonly-queue/main/';

export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

// `published` (the newest version on nuget.org), `published:<version>`, `run@<commit>` (the newest successful ci.yml
// run on main at or after that commit, a passed merge queue run of a commit on main included), `run:<run id>`, `run`
// (the newest successful main run; only for the
// informational N-main row, never for a train row) or, locally, `local:<dir>` (packages packed in a checkout,
// <dir>/nuget and <dir>/npm).
export const COMMIT = /^[0-9a-f]{40}$/;
export function parseSpec(text) {
  const value = String(text ?? '').trim();
  const match = /^(published|run|local)(?:([:@])(.+))?$/.exec(value);
  if (!match) throw new Error(`Expected published, published:<version>, run@<commit>, run:<ci run id>, run or local:<dir>, not '${value}'`);
  const [, kind, separator, argument] = match;
  if (separator === '@') {
    if (kind !== 'run' || !COMMIT.test(argument)) throw new Error(`Expected run@<full 40-character commit SHA>, not '${value}'`);
    return { kind, argument: null, commit: argument, text: value };
  }
  if (kind === 'published' && argument !== undefined) parseVersion(argument);
  if (kind === 'run' && argument !== undefined && !/^[1-9]\d*$/.test(argument)) throw new Error(`Not a run id: ${argument}`);
  if (kind === 'local' && !argument) throw new Error('local needs a directory: local:<dir>');
  return { kind, argument: argument ?? null, commit: null, text: value };
}

// A `run` without a run id or commit: the newest main run, whatever it built (I2, I6). Train rows refuse it.
export const followsMain = spec => spec.kind === 'run' && !spec.argument && !spec.commit;

async function request(url, { token, accept = 'application/json', fetchImpl = fetch, attempts = 3 } = {}) {
  const headers = { accept, 'user-agent': 'runic-train-compat' };
  if (token && url.startsWith(GITHUB)) Object.assign(headers, { authorization: `Bearer ${token}`, 'x-github-api-version': '2022-11-28' });
  for (let attempt = 1; ; attempt++) {
    const response = await fetchImpl(url, { headers, redirect: 'follow' });
    if (response.ok || attempt >= attempts || (response.status < 500 && response.status !== 429)) return response;
    await new Promise(done => setTimeout(done, 2000 * attempt));
  }
}

export async function json(url, options) {
  const response = await request(url, options);
  if (!response.ok) throw Object.assign(new Error(`GET ${url.split('?')[0]}: HTTP ${response.status}`), { status: response.status });
  return response.json();
}

// Every package id the runic-artifex nuget.org account owns, with its newest listed version.
export async function ownedPackages(options = {}) {
  const query = new URLSearchParams({ q: `owner:${NUGET_OWNER}`, prerelease: 'true', semVerLevel: '2.0.0', take: '1000' });
  const result = await json(`${NUGET_SEARCH}?${query}`, options);
  return result.data.map(item => ({ id: item.id, version: item.version }));
}

export async function flatVersions(id, options = {}) {
  return (await json(`${FLAT}/${id.toLowerCase()}/index.json`, options)).versions;
}

export async function downloadNupkg(id, version, directory, options = {}) {
  const url = `${FLAT}/${id.toLowerCase()}/${version.toLowerCase()}/${id.toLowerCase()}.${version.toLowerCase()}.nupkg`;
  const response = await request(url, options);
  if (!response.ok) throw new Error(`GET ${url}: HTTP ${response.status}`);
  mkdirSync(directory, { recursive: true });
  const file = join(directory, `${id}.${version}.nupkg`);
  writeFileSync(file, Buffer.from(await response.arrayBuffer()));
  return file;
}

// The version of a package that belongs to a release: the release itself, or the newest prerelease of it for a
// package that ships on its own prerelease line (Runic.Application.Views.CsWebUi 0.17.1-preview.1).
export function versionForRelease(versions, release) {
  if (versions.some(version => version.toLowerCase() === release.toLowerCase())) return versions.find(v => v.toLowerCase() === release.toLowerCase());
  const base = releaseOf(release);
  const prereleases = versions.filter(version => { try { return parseVersion(version).prerelease.length && releaseOf(version) === base; } catch { return false; } });
  return prereleases.sort(compareVersions).at(-1) ?? null;
}

async function resolvePublished(repo, spec, owned, options) {
  const ids = owned.filter(item => repoOf(item.id) === repo.key);
  const anchor = ids.find(item => item.id === repo.anchor);
  if (!anchor) throw new Error(`${repo.anchor} is not on nuget.org`);
  const version = spec.argument ?? anchor.version;
  const packages = [];
  for (const { id } of ids) {
    const found = versionForRelease(await flatVersions(id, options), version);
    if (!found) continue;
    const nuspec = await (await request(`${FLAT}/${id.toLowerCase()}/${found.toLowerCase()}/${id.toLowerCase()}.nuspec`, options)).text();
    packages.push({ id, version: found, source: 'nuget.org', file: null, sha256: null, nuspec: parseNuspec(nuspec) });
  }
  if (!packages.some(item => item.id === repo.anchor)) throw new Error(`${repo.anchor} ${version} is not on nuget.org`);
  return { version, run: null, packages, npm: [] };
}

const isCiRun = (run, repo) => run.path?.split('@')[0] === `.github/workflows/${CI_WORKFLOW}`
  && run.repository?.full_name === repo.repository && (CI_EVENTS.includes(run.event) || isQueueRun(run));
const isQueueRun = run => run.event === QUEUE_EVENT && Boolean(run.head_branch?.startsWith(QUEUE_BRANCH));
// A named run may also be a pull request's, from a branch of the same repository (a fork's run could upload
// anything): a train candidate checked before it merges. Publish workflows only pass main runs, or the merge queue run
// of a commit on main that eng/release/ci-run.mjs found (D077.22).
export const isNamedCiRun = (run, repo) => isCiRun(run, repo) || (run.path?.split('@')[0] === `.github/workflows/${CI_WORKFLOW}`
  && run.repository?.full_name === repo.repository && run.event === 'pull_request' && run.head_repository?.full_name === repo.repository);

async function runArtifact(repo, run, options) {
  const name = repo.artifact(run.id);
  const { artifacts } = await json(`${GITHUB}/repos/${repo.repository}/actions/runs/${run.id}/artifacts?name=${encodeURIComponent(name)}&per_page=10`, options);
  const live = artifacts.filter(item => item.name === name && !item.expired);
  return live.length === 1 ? live[0] : null;
}

// Command Line and Translations run their macOS jobs on an ordinary push to main only when the push changes a path
// those jobs exercise. Nightly and dispatched runs and the push run of a version or release commit run every job, and
// every run that runs every job uploads the ci-full-run marker artifact (their CONTRIBUTING.md, "CI triggers and
// macOS jobs"). A train input of those repositories (`fullRun` in nuget.mjs) takes only a full run. A run without the
// marker counts as full when every one of its jobs succeeded and none was skipped: the runs from before the marker
// existed all ran every job, and a run that skipped its macOS jobs is never taken. runic-sdk's own runs are not
// checked; its plan and the merge queue decide their coverage (D077.22).
export const FULL_RUN_ARTIFACT = 'ci-full-run';
export async function fullRunVerdict(repo, run, options = {}) {
  if (!repo.fullRun) return { full: true };
  const base = `${GITHUB}/repos/${repo.repository}/actions/runs/${run.id}`;
  const { artifacts = [] } = await json(`${base}/artifacts?name=${FULL_RUN_ARTIFACT}&per_page=10`, options);
  if (artifacts.some(item => item.name === FULL_RUN_ARTIFACT)) return { full: true, marked: true };
  const jobs = [];
  for (let page = 1; page <= 5; page++) {
    const listed = await json(`${base}/jobs?filter=latest&per_page=100&page=${page}`, options);
    jobs.push(...(listed.jobs ?? []));
    if (!listed.jobs?.length || jobs.length >= (listed.total_count ?? 0)) break;
  }
  const incomplete = jobs.filter(job => job.conclusion !== 'success').map(job => `${job.name} ${job.conclusion ?? job.status}`);
  if (jobs.length && !incomplete.length) return { full: true, marked: false };
  return { full: false, reason: `not a full run (no ${FULL_RUN_ARTIFACT} artifact; ${incomplete.length ? incomplete.join(', ') : 'no jobs listed'})` };
}

const dispatchFullRun = repo => `gh workflow run ci.yml -R ${repo.repository} --ref main`;

// Downloads and unpacks a CI artifact. A refusal is reported as an access failure (D061.12): the gate then needs a
// read-only token or GitHub App, which only the maintainer can provide.
export async function downloadArtifact(repo, artifact, directory, options = {}) {
  const url = `${GITHUB}/repos/${repo.repository}/actions/artifacts/${artifact.id}/zip`;
  const response = await request(url, { ...options, accept: 'application/vnd.github+json' });
  if (!response.ok) {
    const detail = (await response.text().catch(() => '')).slice(0, 300);
    throw Object.assign(new Error(`Downloading artifact ${artifact.name} (${artifact.id}) of ${repo.repository} was refused: HTTP ${response.status} ${detail}`),
      { artifactAccess: { repository: repo.repository, status: response.status } });
  }
  mkdirSync(directory, { recursive: true });
  const archive = join(directory, `${artifact.name}.zip`);
  writeFileSync(archive, Buffer.from(await response.arrayBuffer()));
  const files = [];
  for (const entry of zipEntries(archive)) {
    const name = decodeURIComponent(entry.name);
    if (name.endsWith('/') || !/\.(nupkg|tgz)$/.test(name)) continue;
    const target = join(directory, name.endsWith('.nupkg') ? 'nuget' : 'npm', basename(name));
    mkdirSync(join(target, '..'), { recursive: true });
    writeFileSync(target, entry.read());
    files.push(target);
  }
  return files;
}

const npmManifest = file => JSON.parse(execFileSync('tar', ['-xOzf', file, 'package/package.json'], { encoding: 'utf8' }));

// The NuGet packages and npm archives of one repository among candidate files.
function readCandidates(repo, files, origin) {
  const packages = [], npm = [];
  for (const file of files) {
    const bytes = readFileSync(file);
    if (file.endsWith('.nupkg')) {
      const nuspec = parseNuspec(nupkgNuspec(file));
      if (repoOf(nuspec.id) !== repo.key) throw new Error(`${origin} contains ${nuspec.id}, which another repository owns`);
      packages.push({ id: nuspec.id, version: nuspec.version, source: 'run', file, sha256: sha256(bytes), nuspec });
    } else {
      const manifest = npmManifest(file);
      if (npmRepoOf(manifest.name) !== repo.key) continue;
      npm.push({ name: manifest.name, version: manifest.version, file, sha256: sha256(bytes) });
    }
  }
  return { packages, npm };
}

// Packages packed in a checkout (`bun eng/run.mjs pack` writes artifacts/packages/nuget and npm), for a local run of
// the gate before CI has run. Its result names no CI run, so no publish workflow accepts it.
async function resolveLocal(repo, spec, directory) {
  const from = resolve(spec.argument);
  const files = [];
  for (const [kind, pattern] of [['nuget', /\.nupkg$/], ['npm', /\.tgz$/]]) {
    const source = join(from, kind);
    if (!existsSync(source)) continue;
    mkdirSync(join(directory, repo.key, kind), { recursive: true });
    for (const name of readdirSync(source).filter(item => pattern.test(item) && !item.endsWith('.symbols.nupkg'))) {
      const target = join(directory, repo.key, kind, name);
      copyFileSync(join(source, name), target);
      files.push(target);
    }
  }
  const { packages, npm } = readCandidates(repo, files, from);
  const anchor = packages.find(item => item.id === repo.anchor);
  if (!anchor) throw new Error(`${from}/nuget has no ${repo.anchor} package`);
  return { version: anchor.version, packages, npm, run: null };
}

// The main runs of a workflow, newest first. GitHub's `status=success` filter on this listing can omit a run that
// completed successfully, for an hour or more (2026-11-16: run 43913357796 was missing from it while the unfiltered
// listing showed it completed/success), so the gate lists every run and filters here.
// The merge queue's runs are listed apart: their head_branch is the queue's ref, not main.
export const mainRunsQuery = new URLSearchParams({ branch: 'main', per_page: '50' });
export const queueRunsQuery = new URLSearchParams({ event: QUEUE_EVENT, per_page: '50' });
export function successfulMainRuns(runs) {
  return (runs ?? []).filter(run => (run.head_branch === 'main' || isQueueRun(run)) && run.status === 'completed' && run.conclusion === 'success')
    .sort((a, b) => b.id - a.id);
}

// The ci.yml runs of these listings that are main or merge queue runs of the repository, newest first, without duplicates.
async function listCiRuns(repo, queries, options) {
  const listed = new Map();
  for (const query of queries)
    for (const run of (await json(`${GITHUB}/repos/${repo.repository}/actions/workflows/${CI_WORKFLOW}/runs?${query}`, options)).workflow_runs ?? [])
      if (isCiRun(run, repo)) listed.set(run.id, run);
  return [...listed.values()].sort((a, b) => b.id - a.id);
}

// Whether a run's commit is on main: always for a main run; for a merge queue run, when main is its commit or a
// descendant (a queue entry whose group failed never reaches main).
async function onMain(repo, run, options, cache) {
  if (!isQueueRun(run)) return true;
  return ['identical', 'ahead'].includes(await atOrAfter(repo, run.head_sha, 'main', options, cache));
}

export const describeRun = run => `${run.id} (${String(run.head_sha ?? '').slice(0, 9)}, ${run.event}, ${run.status}${run.conclusion ? `/${run.conclusion}` : ''})`;

// Whether `head` is `commit` or a descendant of it, by GitHub's compare API: `identical` or `ahead`.
async function atOrAfter(repo, commit, head, options, cache) {
  if (head === commit) return 'identical';
  const key = `${commit}...${head}`;
  if (!cache.has(key)) cache.set(key, (async () => {
    try {
      return (await json(`${GITHUB}/repos/${repo.repository}/compare/${commit}...${head}?per_page=1`, options)).status;
    } catch (error) {
      if (error.status === 404) return 'unknown';
      throw error;
    }
  })());
  return cache.get(key);
}

// The selection rule for a train input (H2; the lessons' C6: "the newest green main run at or after commit X"):
// among the ci.yml runs on main and the merge queue runs of commits on main, newest first, the first that completed
// successfully, whose head is `commit` or a descendant of it, that has its package artifact, that is a full run for a
// repository that needs one (fullRunVerdict) and that `accept` takes. `accept(run, artifact)` returns
// `{ value }` or `{ reason }` (a rejection, reported with the others). The listing is unfiltered (see above) plus the
// runs of `commit` itself, so neither GitHub's status filter nor a busy main pushes the commit's own run out of it.
// Without such a run the error names the commit and every run it considered.
export async function selectRunAtOrAfter(repo, commit, options = {}, accept = async run => ({ value: run })) {
  if (!COMMIT.test(commit)) throw new Error(`Expected a full commit SHA, not '${commit}'`);
  const runs = await listCiRuns(repo, [mainRunsQuery, new URLSearchParams({ branch: 'main', head_sha: commit, per_page: '20' }),
    queueRunsQuery, new URLSearchParams({ event: QUEUE_EVENT, head_sha: commit, per_page: '20' })], options);
  const green = new Set(successfulMainRuns(runs));
  const seen = [], cache = new Map();
  for (const run of runs) {
    if (!green.has(run)) { seen.push(`${describeRun(run)}: not a success`); continue; }
    const relation = await atOrAfter(repo, commit, run.head_sha, options, cache);
    if (relation === 'unknown') throw new Error(`${repo.repository} has no commit ${commit} (GitHub's compare API answered 404)`);
    if (relation !== 'identical' && relation !== 'ahead') { seen.push(`${describeRun(run)}: ${relation} ${commit.slice(0, 9)}, not at or after it`); continue; }
    if (!await onMain(repo, run, options, cache)) { seen.push(`${describeRun(run)}: a merge queue run of a commit not on main`); continue; }
    const artifact = await runArtifact(repo, run, options);
    if (!artifact) { seen.push(`${describeRun(run)}: no unexpired ${repo.artifact(run.id)} artifact`); continue; }
    const scope = await fullRunVerdict(repo, run, options);
    if (!scope.full) { seen.push(`${describeRun(run)}: ${scope.reason}`); continue; }
    const verdict = await accept(run, artifact);
    if ('value' in verdict) return verdict.value;
    seen.push(`${describeRun(run)}: ${verdict.reason}`);
  }
  throw new Error(`${repo.repository} has no successful ${CI_WORKFLOW} run on main (or merge queue run of a commit on main) at or after ${commit}. ` +
    `Runs found: ${seen.length ? seen.slice(0, 12).join('; ') + (seen.length > 12 ? `; and ${seen.length - 12} more` : '') : 'none'}. ` +
    (repo.fullRun
      ? `Only a full run counts for ${repo.title} (every job, the macOS jobs included: a nightly or dispatched run, or the push run of a version or release commit). ` +
        `If the commit is on main, dispatch ci.yml on main once (\`${dispatchFullRun(repo)}\`), let it finish and run this step again (eng/train/README.md, "Run selection by commit").`
      : 'If the commit is on main, dispatch ci.yml on main once and let it finish (eng/train/README.md, "Choreography").'));
}

// A CI run's package artifact of one repository (exported for eng/ci/train-feed.mjs).
export async function resolveRun(repo, spec, directory, options) {
  let run, artifact;
  if (spec.argument) {
    run = await json(`${GITHUB}/repos/${repo.repository}/actions/runs/${spec.argument}`, options);
    if (!isNamedCiRun(run, repo)) throw new Error(`Run ${spec.argument} is not a ${CI_WORKFLOW} run of ${repo.repository}`);
    if (run.status !== 'completed' || run.conclusion !== 'success') throw new Error(`CI run ${run.html_url} is ${run.status}/${run.conclusion}, not a success`);
  } else if (spec.commit) {
    ({ run, artifact } = await selectRunAtOrAfter(repo, spec.commit, options, async (found, file) => ({ value: { run: found, artifact: file } })));
  } else {
    // A push run that its merge queue run covered (eng/ci/covered.mjs) uploads no artifact; the queue run of its commit does.
    const cache = new Map();
    for (const candidate of successfulMainRuns(await listCiRuns(repo, [mainRunsQuery, queueRunsQuery], options)))
      if (await onMain(repo, candidate, options, cache) && await runArtifact(repo, candidate, options)) { run = candidate; break; }
    if (!run) throw new Error(`${repo.repository} has no successful ${CI_WORKFLOW} run on main with a ${repo.artifact('<run id>')} artifact`);
  }
  artifact ??= await runArtifact(repo, run, options);
  if (!artifact) throw new Error(`CI run ${run.html_url} has no unexpired ${repo.artifact(run.id)} artifact`);
  const files = await downloadArtifact(repo, artifact, join(directory, repo.key), options);
  const { packages, npm } = readCandidates(repo, files, `CI run ${run.html_url}`);
  const anchor = packages.find(item => item.id === repo.anchor);
  if (!anchor) throw new Error(`CI run ${run.html_url} has no ${repo.anchor} package`);
  return {
    version: anchor.version, packages, npm,
    run: { id: run.id, url: run.html_url, headSha: run.head_sha, branch: run.head_branch, event: run.event,
      artifact: { id: artifact.id, name: artifact.name, digest: artifact.digest ?? null } },
  };
}

// The train-feed.json of a runic-sdk CI run that built against the train feed (eng/ci/train-feed.mjs), or null for a
// run without the `train-feed` artifact.
export const TRAIN_FEED_ARTIFACT = 'train-feed';
export async function runTrainFeed(repo, runId, directory, options = {}) {
  const { artifacts } = await json(`${GITHUB}/repos/${repo.repository}/actions/runs/${runId}/artifacts?name=${TRAIN_FEED_ARTIFACT}&per_page=10`, options);
  const live = (artifacts ?? []).filter(item => item.name === TRAIN_FEED_ARTIFACT && !item.expired);
  if (!live.length) return null;
  const url = `${GITHUB}/repos/${repo.repository}/actions/artifacts/${live[0].id}/zip`;
  const response = await request(url, { ...options, accept: 'application/vnd.github+json' });
  if (!response.ok) throw Object.assign(new Error(`Downloading the ${TRAIN_FEED_ARTIFACT} artifact of ${repo.repository} run ${runId} was refused: HTTP ${response.status}`),
    { artifactAccess: { repository: repo.repository, status: response.status } });
  mkdirSync(directory, { recursive: true });
  const archive = join(directory, `${TRAIN_FEED_ARTIFACT}-${runId}.zip`);
  writeFileSync(archive, Buffer.from(await response.arrayBuffer()));
  const entry = zipEntries(archive).find(item => decodeURIComponent(item.name) === 'train-feed.json');
  if (!entry) throw new Error(`The ${TRAIN_FEED_ARTIFACT} artifact of ${repo.repository} run ${runId} has no train-feed.json`);
  return JSON.parse(entry.read().toString('utf8'));
}

// H5 (the lessons' C4): an upstream candidate of this gate run must be the one runic-sdk's train candidate built
// against. `feed` is the train-feed.json of the runic-sdk run; `sources` the resolved inputs. For each upstream with
// a CI-run input whose version the feed also serves, the commits must match, or, if the upstream's main moved
// (I4), every package the feed served must be byte-identical in the candidate (reproducible archives, H1: a commit
// that changed no package input). Returns `{ checked, problems }`, one text each.
export function upstreamMovement(feed, sources) {
  const checked = [], problems = [];
  for (const [key, upstream] of Object.entries(feed?.upstreams ?? {})) {
    const source = sources[key];
    if (!source?.run || source.version !== upstream.version) continue;
    const from = upstream.run?.headSha, to = source.run.headSha;
    const where = `${source.repository} ${upstream.version}: runic-sdk's train candidate built against run ${upstream.run?.id} (${from})`;
    if (from === to) { checked.push(`${where}, the same commit as this candidate`); continue; }
    const have = new Map([...source.packages.map(item => [`nuget:${item.id}@${item.version}`.toLowerCase(), item.sha256]),
      ...source.npm.map(item => [`npm:${item.name}@${item.version}`, item.sha256])]);
    const served = [...(upstream.packages ?? []).map(item => [`nuget:${item.id}@${item.version}`.toLowerCase(), item.sha256]),
      ...(upstream.npm ?? []).map(item => [`npm:${item.name}@${item.version}`, item.sha256])];
    const differ = served.filter(([name, hash]) => have.get(name) !== hash).map(([name]) => name);
    const extra = [...have.keys()].filter(name => name.startsWith('nuget:') && name.endsWith(`@${upstream.version}`.toLowerCase())
      && !served.some(([other]) => other === name));
    if (served.length && !differ.length && !extra.length) {
      checked.push(`${where}; this candidate is ${to}, but every package is byte-identical`);
      continue;
    }
    problems.push(`${source.repository} main moved since runic-sdk's train candidate used it: the feed served run ${upstream.run?.id} on ${from}, ` +
      `this candidate is run ${source.run.id} on ${to}, and ${[...differ, ...extra].slice(0, 5).join(', ') || 'no package'}${differ.length + extra.length > 5 ? ', ...' : ''} ` +
      'differ. Publish the commit the train candidate used, or rebuild runic-sdk\'s candidate on this one (eng/train/README.md, "Choreography").');
  }
  return { checked, problems };
}

// Resolves every repository's input. `specs` maps cli, translations and sdk to their input texts.
export async function resolveSources(specs, directory, options = {}) {
  let owned;
  const result = {};
  for (const [key, text] of Object.entries(specs)) {
    const repo = repos[key];
    const spec = parseSpec(text);
    if (spec.kind === 'published') owned ??= await ownedPackages(options);
    const resolved = spec.kind === 'published' ? await resolvePublished(repo, spec, owned, options)
      : spec.kind === 'local' ? await resolveLocal(repo, spec, directory)
        : await resolveRun(repo, spec, directory, options);
    result[key] = { repo: key, repository: repo.repository, input: spec.text, kind: spec.kind, ...resolved };
  }
  return result;
}

// The candidate package file, downloading a published one on demand.
export async function packageFile(source, id, directory, options = {}) {
  const item = source.packages.find(candidate => candidate.id.toLowerCase() === id.toLowerCase());
  if (!item) return null;
  if (!item.file) {
    item.file = await downloadNupkg(item.id, item.version, join(directory, 'published'), options);
    item.sha256 = sha256(readFileSync(item.file));
  }
  return item.file;
}
