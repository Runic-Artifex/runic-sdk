// Resolves each repository's input (a published version or a CI run's package artifact) into the candidate
// packages of one gate run: their ids, versions, nuspecs and, for CI runs, local files with SHA-256 values.
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
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

export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

// `published` (the newest version on nuget.org), `published:<version>`, `run` (the newest successful ci.yml run on
// main) or `run:<run id>`.
export function parseSpec(text) {
  const value = String(text ?? '').trim();
  const match = /^(published|run)(?::(.+))?$/.exec(value);
  if (!match) throw new Error(`Expected published, published:<version>, run or run:<ci run id>, not '${value}'`);
  const [, kind, argument] = match;
  if (kind === 'published' && argument !== undefined) parseVersion(argument);
  if (kind === 'run' && argument !== undefined && !/^[1-9]\d*$/.test(argument)) throw new Error(`Not a run id: ${argument}`);
  return { kind, argument: argument ?? null, text: value };
}

async function request(url, { token, accept = 'application/json', fetchImpl = fetch, attempts = 3 } = {}) {
  const headers = { accept, 'user-agent': 'runic-train-compat' };
  if (token && url.startsWith(GITHUB)) Object.assign(headers, { authorization: `Bearer ${token}`, 'x-github-api-version': '2022-11-28' });
  for (let attempt = 1; ; attempt++) {
    const response = await fetchImpl(url, { headers, redirect: 'follow' });
    if (response.ok || attempt >= attempts || (response.status < 500 && response.status !== 429)) return response;
    await new Promise(done => setTimeout(done, 2000 * attempt));
  }
}

async function json(url, options) {
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
  && run.repository?.full_name === repo.repository && CI_EVENTS.includes(run.event);

async function runArtifact(repo, run, options) {
  const name = repo.artifact(run.id);
  const { artifacts } = await json(`${GITHUB}/repos/${repo.repository}/actions/runs/${run.id}/artifacts?name=${encodeURIComponent(name)}&per_page=10`, options);
  const live = artifacts.filter(item => item.name === name && !item.expired);
  return live.length === 1 ? live[0] : null;
}

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

async function resolveRun(repo, spec, directory, options) {
  let run;
  if (spec.argument) {
    run = await json(`${GITHUB}/repos/${repo.repository}/actions/runs/${spec.argument}`, options);
    if (!isCiRun(run, repo)) throw new Error(`Run ${spec.argument} is not a ${CI_WORKFLOW} run of ${repo.repository}`);
    if (run.status !== 'completed' || run.conclusion !== 'success') throw new Error(`CI run ${run.html_url} is ${run.status}/${run.conclusion}, not a success`);
  } else {
    const query = new URLSearchParams({ branch: 'main', status: 'success', per_page: '30' });
    const { workflow_runs: runs } = await json(`${GITHUB}/repos/${repo.repository}/actions/workflows/${CI_WORKFLOW}/runs?${query}`, options);
    for (const candidate of runs.filter(item => isCiRun(item, repo) && item.head_branch === 'main').sort((a, b) => b.id - a.id))
      if (await runArtifact(repo, candidate, options)) { run = candidate; break; }
    if (!run) throw new Error(`${repo.repository} has no successful ${CI_WORKFLOW} run on main with a ${repo.artifact('<run id>')} artifact`);
  }
  const artifact = await runArtifact(repo, run, options);
  if (!artifact) throw new Error(`CI run ${run.html_url} has no unexpired ${repo.artifact(run.id)} artifact`);
  const files = await downloadArtifact(repo, artifact, join(directory, repo.key), options);
  const packages = [], npm = [];
  for (const file of files) {
    const bytes = readFileSync(file);
    if (file.endsWith('.nupkg')) {
      const nuspec = parseNuspec(nupkgNuspec(file));
      if (repoOf(nuspec.id) !== repo.key) throw new Error(`${repo.repository}'s artifact contains ${nuspec.id}, which another repository owns`);
      packages.push({ id: nuspec.id, version: nuspec.version, source: 'run', file, sha256: sha256(bytes), nuspec });
    } else {
      const manifest = npmManifest(file);
      if (npmRepoOf(manifest.name) !== repo.key) continue;
      npm.push({ name: manifest.name, version: manifest.version, file, sha256: sha256(bytes) });
    }
  }
  const anchor = packages.find(item => item.id === repo.anchor);
  if (!anchor) throw new Error(`CI run ${run.html_url} has no ${repo.anchor} package`);
  return {
    version: anchor.version, packages, npm,
    run: { id: run.id, url: run.html_url, headSha: run.head_sha, branch: run.head_branch, event: run.event,
      artifact: { id: artifact.id, name: artifact.name, digest: artifact.digest ?? null } },
  };
}

// Resolves every repository's input. `specs` maps cli, translations and sdk to their input texts.
export async function resolveSources(specs, directory, options = {}) {
  let owned;
  const result = {};
  for (const [key, text] of Object.entries(specs)) {
    const repo = repos[key];
    const spec = parseSpec(text);
    if (spec.kind === 'published') owned ??= await ownedPackages(options);
    const resolved = spec.kind === 'published'
      ? await resolvePublished(repo, spec, owned, options)
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
