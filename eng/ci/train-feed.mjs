#!/usr/bin/env bun
// The train feed (local-planning COMPATIBILITY.md 3.4; eng/train/README.md, "Train feed"): builds runic-sdk against
// the Runic Command Line and Runic Translations releases of its train before they are on nuget.org.
//
//   bun eng/ci/train-feed.mjs prepare [--output <dir>] [--cli run[:<id>]] [--translations run[:<id>]]
//
// Directory.Packages.props names the train's Runic.CommandLine and Runic.Translations versions. For each one that
// nuget.org does not have, prepare puts the packages of the upstream repository's CI artifact into <output>/nuget
// (default artifacts/train-feed/nuget): the given run (`run:<id>`), or the newest successful ci.yml run on its main
// whose artifact has exactly that version (`run`, the default). It downloads them as the train gate does
// (eng/train/sources.mjs, GH_TOKEN or GITHUB_TOKEN). With every version on nuget.org it does nothing.
//
// It writes <output>/train-feed.json, which names each upstream run and the SHA-256 of each package. Builds use the
// feed through RUNIC_TRAIN_FEED=<output>/nuget: eng/build/nuget-lock.targets adds it as a restore source and restores
// unlocked, because a CI candidate is not the package nuget.org will serve (nuget.org adds its repository signature),
// and consumers outside the repository add it to their NuGet.config (trainFeedSource()); `dotnet tool restore` of the
// repository's manifest needs `--add-source` once. A CI run built this way
// uploads the train-feed artifact, and Publish preview refuses it (eng/release/ci-run.mjs).
import { appendFileSync, copyFileSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { repos } from '../train/nuget.mjs';
import { flatVersions, parseSpec, resolveRun } from '../train/sources.mjs';

const root = resolve(fileURLToPath(new URL('../..', import.meta.url)));
export const TRAIN_FEED_ARTIFACT = 'train-feed';
const SCHEMA = 'runic.sdk.train-feed/1';
// The upstream repositories of runic-sdk in the train, each with the package whose version Directory.Packages.props pins.
export const UPSTREAMS = ['cli', 'translations'];

// The train's version of each upstream repository: the central version of its anchor package.
export function trainVersions(props = readFileSync(join(root, 'Directory.Packages.props'), 'utf8')) {
  const versions = {};
  for (const key of UPSTREAMS) {
    const id = repos[key].anchor.replaceAll('.', '\\.');
    const version = new RegExp(`<PackageVersion\\s+Include="${id}"\\s+Version="([^"]+)"`).exec(props)?.[1];
    if (!version) throw new Error(`Directory.Packages.props has no PackageVersion for ${repos[key].anchor}`);
    versions[key] = version;
  }
  return versions;
}

// The feed directory of the current build, or null without a train feed.
export function activeTrainFeed(environment = process.env) {
  return environment.RUNIC_TRAIN_FEED ? resolve(environment.RUNIC_TRAIN_FEED) : null;
}

const escapeXml = value => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');

// The train feed's <add> source and its <packageSource> mapping for a consumer's NuGet.config; empty strings without a
// feed. The mapping names the upstream repositories' package ids (eng/train/nuget.mjs), which are more specific than a
// nuget.org "*", so with package source mapping a candidate is never replaced by a nuget.org package. A config without
// mapping needs only the source: the feed has only versions nuget.org does not.
export function trainFeedSource(environment = process.env) {
  const feed = activeTrainFeed(environment);
  if (!feed) return { source: '', mapping: '' };
  const patterns = UPSTREAMS.flatMap(key => repos[key].patterns);
  return {
    source: `<add key="runic-train-feed" value="${escapeXml(feed)}"/>`,
    mapping: `<packageSource key="runic-train-feed">${patterns.map(pattern => `<package pattern="${pattern}"/>`).join('')}</packageSource>`,
  };
}

async function published(id, version, options) {
  try {
    return (await flatVersions(id, options)).some(item => item.toLowerCase() === version.toLowerCase());
  } catch (error) {
    if (/HTTP 404/.test(error.message)) return false;
    throw error;
  }
}

async function json(url, token) {
  const headers = { accept: 'application/vnd.github+json', 'user-agent': 'runic-train-feed', 'x-github-api-version': '2022-11-28' };
  if (token) headers.authorization = `Bearer ${token}`;
  const response = await fetch(url, { headers });
  if (!response.ok) throw new Error(`GET ${url.split('?')[0]}: HTTP ${response.status}`);
  return response.json();
}

// The upstream CI run whose artifact has exactly this version: the given run, or the newest successful main run.
async function upstreamRun(key, spec, version, work, options) {
  const repo = repos[key];
  if (spec.argument) {
    const resolved = await resolveRun(repo, spec, work, options);
    if (resolved.version !== version)
      throw new Error(`CI run ${resolved.run.url} has ${repo.anchor} ${resolved.version}, but Directory.Packages.props pins ${version}`);
    return resolved;
  }
  const query = new URLSearchParams({ branch: 'main', status: 'success', per_page: '20' });
  const { workflow_runs: runs } = await json(`https://api.github.com/repos/${repo.repository}/actions/workflows/ci.yml/runs?${query}`, options.token);
  const seen = [];
  for (const run of runs.filter(item => item.head_branch === 'main').sort((a, b) => b.id - a.id).slice(0, 5)) {
    let resolved;
    try {
      resolved = await resolveRun(repo, { kind: 'run', argument: String(run.id) }, join(work, String(run.id)), options);
    } catch (error) {
      if (error.artifactAccess) throw error;
      seen.push(`${run.html_url}: ${error.message}`);
      continue;
    }
    if (resolved.version === version) return resolved;
    seen.push(`${run.html_url}: ${repo.anchor} ${resolved.version}`);
  }
  throw new Error(`No recent successful ci.yml run on ${repo.repository} main has ${repo.anchor} ${version}` +
    (seen.length ? ` (${seen.join('; ')})` : '') + `. Pass --${key} run:<id>.`);
}

function option(args, name) {
  const index = args.indexOf(name);
  return index < 0 ? undefined : args[index + 1];
}

function setOutput(values) {
  if (!process.env.GITHUB_OUTPUT) return;
  appendFileSync(process.env.GITHUB_OUTPUT, Object.entries(values).map(([key, value]) => `${key}=${value}\n`).join(''));
}

export async function prepare(args = [], { versions = trainVersions(), token = process.env.GH_TOKEN ?? process.env.GITHUB_TOKEN } = {}) {
  const output = resolve(option(args, '--output') ?? join(root, 'artifacts/train-feed'));
  const feed = join(output, 'nuget');
  const options = { token };
  const missing = [];
  for (const key of UPSTREAMS) {
    if (await published(repos[key].anchor, versions[key], options)) console.log(`${repos[key].anchor} ${versions[key]} is on nuget.org.`);
    else missing.push(key);
  }
  rmSync(output, { recursive: true, force: true });
  if (!missing.length) {
    console.log('Every upstream release of this train is on nuget.org; no train feed.');
    setOutput({ active: 'false' });
    return null;
  }
  mkdirSync(feed, { recursive: true });
  const work = join(tmpdir(), `runic-train-feed-${process.pid}`);
  rmSync(work, { recursive: true, force: true });
  const upstreams = {};
  try {
    for (const key of missing) {
      const spec = parseSpec(option(args, `--${key}`) ?? 'run');
      if (spec.kind !== 'run') throw new Error(`--${key} takes run or run:<id>, not ${spec.text}`);
      const resolved = await upstreamRun(key, spec, versions[key], join(work, key), options);
      const packages = resolved.packages.filter(item => item.version === versions[key])
        .map(item => { copyFileSync(item.file, join(feed, basename(item.file))); return { id: item.id, version: item.version, sha256: item.sha256 }; })
        .sort((a, b) => a.id.localeCompare(b.id));
      upstreams[key] = { repository: repos[key].repository, version: versions[key], run: resolved.run, packages };
      console.log(`${repos[key].repository} ${versions[key]}: ${packages.length} packages from ${resolved.run.url} (${resolved.run.headSha}).`);
    }
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
  const marker = {
    schema: SCHEMA,
    publishable: false,
    reason: `${missing.map(key => `${repos[key].anchor} ${versions[key]}`).join(' and ')} not on nuget.org; this build used train-feed candidates and restored without its lock files.`,
    upstreams,
  };
  writeFileSync(join(output, 'train-feed.json'), `${JSON.stringify(marker, null, 2)}\n`);
  console.log(`Train feed in ${feed}. Builds use it with RUNIC_TRAIN_FEED=${feed}.`);
  setOutput({ active: 'true' });
  return marker;
}

if (import.meta.main) {
  const [command, ...args] = process.argv.slice(2);
  if (command !== 'prepare') {
    console.error('Usage: bun eng/ci/train-feed.mjs prepare [--output <dir>] [--cli run[:<id>]] [--translations run[:<id>]]');
    process.exit(2);
  }
  try {
    await prepare(args);
  } catch (error) {
    console.error(process.env.GITHUB_ACTIONS ? `::error title=Train feed::${error.message}` : error.message);
    process.exit(1);
  }
}
