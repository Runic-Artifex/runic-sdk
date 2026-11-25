#!/usr/bin/env bun
// The train feed (local-planning COMPATIBILITY.md 3.4; eng/train/README.md, "Train feed"): builds runic-sdk against
// the Runic Command Line and Runic Translations releases of its train before they are on nuget.org.
//
//   bun eng/ci/train-feed.mjs prepare [--output <dir>] [--cli run@<commit>|run:<id>] [--translations run@<commit>|run:<id>]
//   bun eng/ci/train-feed.mjs exit [--wait <minutes>] [--out <dir>] [--tree <checkout>] [--dry-run]
//   bun eng/ci/train-feed.mjs exit-report [--out <dir>] [--tree <checkout>]
//
// Directory.Packages.props names the train's Runic.CommandLine and Runic.Translations versions, and for a version
// that is not on nuget.org yet the upstream commit of the train candidate (`TrainCommit="<sha>"` on the same
// PackageVersion). For each version that nuget.org does not have, prepare puts the packages of the upstream
// repository's CI artifact into <output>/nuget (default artifacts/train-feed/nuget): the given run (`run:<id>`), or
// the newest successful ci.yml run on its main at or after the pinned commit whose artifact has exactly that version
// (`run@<commit>`, by default the TrainCommit; eng/train/sources.mjs, selectRunAtOrAfter). It never takes "the
// newest main run" (H2). It downloads them as the train gate does (GH_TOKEN or GITHUB_TOKEN). With every version on
// nuget.org it does nothing.
//
// The npm packages of the same artifact whose version the npm registry does not have go into <output>/npm; consumers
// install them from there (trainFeedNpmSpec()).
//
// It writes <output>/train-feed.json, which names each upstream run and the SHA-256 of each package. Builds use the
// feed through RUNIC_TRAIN_FEED=<output>/nuget: eng/build/nuget-lock.targets adds it as a restore source and restores
// unlocked, because a CI candidate is not the package nuget.org will serve (nuget.org adds its repository signature),
// and consumers outside the repository add it to their NuGet.config (trainFeedSource()); `dotnet tool restore` of the
// repository's manifest needs `--add-source` once. A CI run built this way
// uploads the train-feed artifact, and Publish preview refuses it (eng/release/ci-run.mjs).
//
// exit is the feed exit (eng/train/README.md, "Once Command Line and Translations are published"; train-feed-exit.yml).
// Every PackageVersion with a TrainCommit leaves the feed, all of them at once. It reads train-feed.json of the newest
// successful runic-sdk main run at or after the commit that last changed Directory.Packages.props, waits up to
// --wait minutes (default 70) until nuget.org lists every NuGet package that feed served and npm has every npm
// package, checks that npm serves the same bytes (SHA-256) as the feed did, then drops the TrainCommit attributes
// (and moves the Nix completions fixture to the train's Command Line). It writes <out>/exit.json and <out>/pr-body.md
// (default artifacts/feed-exit). Without a TrainCommit it prints "nothing to exit". --dry-run leaves the checkout
// alone and prints the edits. --tree computes the exit in another checkout, such as a past main commit for a rehearsal
// (train-feed-exit.yml's `from`), which predates this script. exit-report runs after `bun run lock:nuget`: it checks that Directory.Packages.props
// only lost TrainCommit attributes and the fixture only its version, and adds the lock file changes to both files.
import { execFileSync } from 'node:child_process';
import { appendFileSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { nugetLists, registryMatches, waitForNuget } from '../release/registry.mjs';
import { repos } from '../train/nuget.mjs';
import { flatVersions, followsMain, json, parseSpec, resolveRun, runTrainFeed, selectRunAtOrAfter } from '../train/sources.mjs';

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

// The upstream commit each train version is pinned to (`TrainCommit` on its PackageVersion), or null.
export function trainCommits(props = readFileSync(join(root, 'Directory.Packages.props'), 'utf8')) {
  const commits = {};
  for (const key of UPSTREAMS) {
    const id = repos[key].anchor.replaceAll('.', '\\.');
    const element = new RegExp(`<PackageVersion\\s+Include="${id}"[^>]*>`).exec(props)?.[0] ?? '';
    const commit = /\sTrainCommit="([^"]*)"/.exec(element)?.[1] ?? null;
    if (commit !== null && !/^[0-9a-f]{40}$/.test(commit)) throw new Error(`TrainCommit of ${repos[key].anchor} is not a full commit SHA: '${commit}'`);
    commits[key] = commit;
  }
  return commits;
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

// The train feed's npm candidates, from its train-feed.json next to the NuGet folder: name -> { version, file }.
export function trainFeedNpm(environment = process.env) {
  const feed = activeTrainFeed(environment);
  const marker = feed && join(dirname(feed), 'train-feed.json');
  if (!marker || !existsSync(marker)) return {};
  const { upstreams } = JSON.parse(readFileSync(marker, 'utf8'));
  return Object.fromEntries(Object.values(upstreams).flatMap(upstream => upstream.npm ?? [])
    .map(item => [item.name, { version: item.version, file: join(dirname(feed), 'npm', item.file) }]));
}

// The package.json specifier of an upstream npm package: the train feed's tarball (file:) while that version is a
// candidate the registry does not have, the version otherwise.
export function trainFeedNpmSpec(name, version, environment = process.env) {
  const candidate = trainFeedNpm(environment)[name];
  return candidate?.version === version ? `file:${candidate.file}` : version;
}

export async function npmPublished(name, version, { fetchImpl = fetch } = {}) {
  const response = await fetchImpl(`https://registry.npmjs.org/${name.replace('/', '%2F')}`, { headers: { accept: 'application/vnd.npm.install-v1+json' } });
  if (response.status === 404) return false;
  if (!response.ok) throw new Error(`GET registry.npmjs.org/${name}: HTTP ${response.status}`);
  return Object.hasOwn((await response.json()).versions ?? {}, version);
}

async function published(id, version, options) {
  try {
    return (await flatVersions(id, options)).some(item => item.toLowerCase() === version.toLowerCase());
  } catch (error) {
    if (/HTTP 404/.test(error.message)) return false;
    throw error;
  }
}

// The upstream CI run whose artifact has exactly this version: the given run, or the newest successful main run at or
// after the given commit (the train's pin).
export async function upstreamRun(key, spec, version, work, options) {
  const repo = repos[key];
  if (spec.argument) {
    const resolved = await resolveRun(repo, spec, work, options);
    if (resolved.version !== version)
      throw new Error(`CI run ${resolved.run.url} has ${repo.anchor} ${resolved.version}, but Directory.Packages.props pins ${version}`);
    return resolved;
  }
  return selectRunAtOrAfter(repo, spec.commit, options, async run => {
    let resolved;
    try {
      resolved = await resolveRun(repo, { kind: 'run', argument: String(run.id) }, join(work, String(run.id)), options);
    } catch (error) {
      if (error.artifactAccess) throw error;
      return { reason: error.message };
    }
    return resolved.version === version ? { value: resolved } : { reason: `${repo.anchor} ${resolved.version}, not ${version}` };
  });
}

function option(args, name) {
  const index = args.indexOf(name);
  return index < 0 ? undefined : args[index + 1];
}

function setOutput(values, environment = process.env) {
  if (!environment.GITHUB_OUTPUT) return;
  appendFileSync(environment.GITHUB_OUTPUT, Object.entries(values).map(([key, value]) => `${key}=${value}\n`).join(''));
}

export async function prepare(args = [], { versions = trainVersions(), commits = trainCommits(), token = process.env.GH_TOKEN ?? process.env.GITHUB_TOKEN } = {}) {
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
      const given = option(args, `--${key}`) ?? (commits[key] && `run@${commits[key]}`);
      if (!given) throw new Error(`${repos[key].anchor} ${versions[key]} is not on nuget.org, and Directory.Packages.props names no TrainCommit for it: ` +
        `add TrainCommit="<${repos[key].repository} commit of the train candidate>" to its PackageVersion, or pass --${key} run:<id> (eng/train/README.md, "Train feed").`);
      const spec = parseSpec(given);
      if (spec.kind !== 'run' || followsMain(spec)) throw new Error(`--${key} takes run@<commit> or run:<id>, not ${spec.text}`);
      const resolved = await upstreamRun(key, spec, versions[key], join(work, key), options);
      const packages = resolved.packages.filter(item => item.version === versions[key])
        .map(item => { copyFileSync(item.file, join(feed, basename(item.file))); return { id: item.id, version: item.version, sha256: item.sha256 }; })
        .sort((a, b) => a.id.localeCompare(b.id));
      const npm = [];
      for (const item of resolved.npm.filter(entry => entry.version === versions[key])) {
        if (await npmPublished(item.name, item.version)) continue;
        mkdirSync(join(output, 'npm'), { recursive: true });
        copyFileSync(item.file, join(output, 'npm', basename(item.file)));
        npm.push({ name: item.name, version: item.version, file: basename(item.file), sha256: item.sha256 });
      }
      npm.sort((a, b) => a.name.localeCompare(b.name));
      upstreams[key] = { repository: repos[key].repository, version: versions[key], run: resolved.run, packages, npm };
      console.log(`${repos[key].repository} ${versions[key]}: ${packages.length} NuGet and ${npm.length} npm packages from ${resolved.run.url} (${resolved.run.headSha}).`);
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

// The feed exit (E1; eng/train/README.md, "Once Command Line and Translations are published").
export const EXIT_SCHEMA = 'runic.sdk.train-feed-exit/1';
export const PROPS = 'Directory.Packages.props';
// nix/tests/completions.nix builds this project from nuget.org by lock-file hash, so it stays on the published Command
// Line during a train and moves to the train's Command Line at the exit.
export const NIX_FIXTURE = 'tests/fixtures/nix/completions/App/App.csproj';
const TRAIN_COMMIT = /\sTrainCommit="[0-9a-f]{40}"/;
const NIX_REFERENCE = /(<PackageReference\s+Include="Runic\.CommandLine"\s+Version=")([^"]+)(")/;
const README = 'eng/train/README.md, "Train feed"';

// The feed exit may change Directory.Packages.props in one way only: lines lose their TrainCommit="<commit>"
// attribute, and none is left. Anything else (a version, a source, a new element) fails.
export function assertPropsExit(before, after) {
  const from = before.split('\n'), to = after.split('\n'), problems = [];
  if (from.length !== to.length) problems.push(`it has ${to.length} lines instead of ${from.length}`);
  else from.forEach((line, index) => {
    if (line !== to[index] && !(TRAIN_COMMIT.test(line) && line.replace(TRAIN_COMMIT, '') === to[index]))
      problems.push(`line ${index + 1} '${line.trim()}' became '${to[index].trim()}'`);
  });
  if (/\sTrainCommit\s*=/.test(after)) problems.push('a TrainCommit is left');
  if (before === after) problems.push('no TrainCommit was removed');
  if (problems.length) throw new Error(`The feed exit may only remove TrainCommit="<commit>" attributes from ${PROPS}, but ${problems.slice(0, 5).join('; ')}`);
}

// Directory.Packages.props without the TrainCommit of each anchor PackageVersion (the elements trainCommits() reads).
export function dropTrainCommits(props) {
  let text = props;
  for (const key of UPSTREAMS) {
    const id = repos[key].anchor.replaceAll('.', '\\.');
    text = text.replace(new RegExp(`<PackageVersion\\s+Include="${id}"[^>]*>`), element => element.replace(TRAIN_COMMIT, ''));
  }
  assertPropsExit(props, text);
  return text;
}

export function nixFixtureVersion(text) {
  const version = NIX_REFERENCE.exec(text)?.[2];
  if (!version) throw new Error(`${NIX_FIXTURE} has no PackageReference to Runic.CommandLine with a Version`);
  return version;
}
export const moveNixFixture = (text, version) => text.replace(NIX_REFERENCE, (_, head, _old, tail) => `${head}${version}${tail}`);

// The tag's commit in an upstream repository (`v<version>`, which its publish creates), or null without the tag.
export async function upstreamTagCommit(repository, tag, options = {}) {
  let ref;
  try {
    ref = await json(`https://api.github.com/repos/${repository}/git/ref/tags/${tag}`, options);
  } catch (error) {
    if (error.status === 404) return null;
    throw error;
  }
  if (ref.object.type === 'tag') return (await json(`https://api.github.com/repos/${repository}/git/tags/${ref.object.sha}`, options)).object.sha;
  return ref.object.sha;
}

// The newest successful runic-sdk main run at or after `commit` whose train-feed artifact serves each leaving
// upstream at the version Directory.Packages.props names (selectRunAtOrAfter's rule; the feed's artifact, not the
// package artifact, is the one it needs).
export async function feedRun(commit, versions, keys, options = {}) {
  const repo = { ...repos.sdk, artifact: () => TRAIN_FEED_ARTIFACT };
  const work = mkdtempSync(join(tmpdir(), 'runic-feed-exit-'));
  try {
    return await selectRunAtOrAfter(repo, commit, options, async run => {
      const feed = await runTrainFeed(repos.sdk, run.id, join(work, String(run.id)), options);
      if (!feed) return { reason: `no ${TRAIN_FEED_ARTIFACT} artifact` };
      const wrong = keys.filter(key => feed.upstreams?.[key]?.version !== versions[key]);
      if (wrong.length) return { reason: wrong.map(key => `its feed serves ${repos[key].anchor} ${feed.upstreams?.[key]?.version ?? 'not at all'}, not ${versions[key]}`).join('; ') };
      return { value: { run: { id: run.id, url: run.html_url, headSha: run.head_sha, event: run.event }, feed } };
    });
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

const describePackages = items => items.map(item => `${item.name} ${item.version}`).join(', ');

// All or nothing: the exit waits until every package of every leaving upstream is indexed, or fails naming the
// upstreams and packages that are not.
async function notIndexed(missing, registry, minutes, versions) {
  const upstreams = [...new Set(missing.map(item => item.upstream))];
  return new Error(`${upstreams.map(key => `${repos[key].title} ${versions[key]}`).join(' and ')} ${upstreams.length > 1 ? 'are' : 'is'} not on ${registry} ` +
    `after ${minutes} minutes (missing: ${describePackages(missing)}); the feed exit leaves the whole feed at once (${README}), ` +
    'so it waits until every TrainCommit release is published and indexed. Dispatch it again after the last publish.');
}

async function waitIndexed(nuget, npm, { minutes, intervalMs, now, sleep, log, lists, npmListed, versions, options }) {
  const timeoutMs = minutes * 60000, deadline = now() + timeoutMs;
  const listed = new Map();
  const recording = async (name, version) => {
    const result = await lists(name, version, options);
    listed.set(`${name}@${version}`, result);
    return result;
  };
  try {
    await waitForNuget(nuget, { timeoutMs, intervalMs, now, sleep, log, lists: recording });
  } catch (error) {
    if (!/still does not list/.test(error.message)) throw error;
    throw await notIndexed(nuget.filter(item => !listed.get(`${item.name}@${item.version}`)), 'nuget.org', minutes, versions);
  }
  let missing = npm;
  for (;;) {
    const found = await Promise.all(missing.map(item => npmListed(item.name, item.version, options)));
    missing = missing.filter((_, index) => !found[index]);
    if (!missing.length) return;
    if (now() + intervalMs >= deadline) throw await notIndexed(missing, 'npm', minutes, versions);
    log(`Waiting for npm to serve ${missing.length} package(s): ${describePackages(missing)}`);
    await sleep(intervalMs);
  }
}

const short = sha => sha ? sha.slice(0, 9) : 'none';
const runLink = run => run ? `[${run.id}](${run.url}) on \`${short(run.headSha)}\`` : 'none';

export const exitMarker = record => `<!-- train-handoff: feed-exit ${Object.entries(record.upstreams)
  .map(([key, upstream]) => `${key}=${upstream.version}@${upstream.trainCommit}`).join(' ')} -->`;
export const exitTitle = record => `Train feed exit: ${Object.values(record.upstreams).map(upstream => `${upstream.title} ${upstream.version}`).join(' and ')} from nuget.org`;

// The pull request body (and commit message body) of a feed exit, from exit.json.
export function exitBody(record) {
  const upstreams = Object.values(record.upstreams);
  const locks = record.locks;
  const lockLine = !locks ? 'not rewritten yet: `bun run lock:nuget` runs after `train-feed.mjs exit`, and `exit-report` records its result here.'
    : !locks.files.length ? 'unchanged: the published packages are the candidates the feed served.'
      : `${locks.files.length} changed, ${locks.contentHash} \`contentHash\` line(s)${locks.other.length ? `; **other lines changed** in ${locks.other.map(item => `\`${item.file}\` (${item.lines})`).join(', ')}: review them` : ''}: ${locks.files.map(file => `\`${file}\``).join(', ')}.`;
  const npm = upstreams.flatMap(upstream => upstream.npm);
  const lines = [
    record.marker,
    `${upstreams.map(upstream => `${upstream.title} ${upstream.version}`).join(' and ')} ${upstreams.length > 1 ? 'are' : 'is'} on nuget.org and npm, so runic-sdk leaves the train feed ` +
      '(`eng/train/README.md`, "Train feed", "Once Command Line and Translations are published").',
    '',
    '## What changed',
    '',
    ...upstreams.map(upstream => `- \`${PROPS}\` drops the \`TrainCommit\` of ${upstream.anchor} ${upstream.version} (\`${upstream.trainCommit}\`).`),
    `- Lock files (\`bun run lock:nuget\` without \`RUNIC_TRAIN_FEED\`, NuGet cache filled only from nuget.org): ${lockLine}`,
    record.nix ? `- The Nix completions fixture \`${record.nix.project}\` moves from Runic.CommandLine ${record.nix.from} to ${record.nix.to}, with its lock file.`
      : `- The Nix completions fixture stays on Runic.CommandLine ${record.nixVersion}.`,
    '',
    '## Evidence',
    '',
    '| Upstream | Version | TrainCommit | runic-sdk feed run | Upstream run it served | Release tag |',
    '| --- | --- | --- | --- | --- | --- |',
    ...upstreams.map(upstream => `| ${upstream.title} | ${upstream.version} | \`${short(upstream.trainCommit)}\` | ${runLink(record.feedRun)} | ${runLink(upstream.servedRun)} | ` +
      `${upstream.tag.commit ? `\`${upstream.tag.name}\` → \`${short(upstream.tag.commit)}\`${upstream.tag.equalsTrainCommit ? ' (the TrainCommit)' : ' (**not** the TrainCommit; the lock files are the check)'}` : `no \`${upstream.tag.name}\` tag`} |`),
    '',
    `- nuget.org lists all ${upstreams.reduce((sum, upstream) => sum + upstream.nuget.length, 0)} NuGet packages the feed served.`,
    npm.length ? `- npm serves the same bytes (SHA-256) as the feed for ${npm.map(item => `\`${item.name}@${item.version}\``).join(', ')}, so the template locks keep their integrities.`
      : '- The feed served no npm package.',
    '',
    '## Left for an agent',
    '',
    '- Prose that names the candidate or the feed (docs, upgrading notes; #1133 and #1244 had some) is not part of this pull request.',
    '',
    '## Merging',
    '',
    'Merge through the merge queue after every check. The queue run of a `TrainCommit` change runs every leg (`eng/ci/plan.mjs`) ' +
      'and is the green `main` run (D077.22).',
    '',
    record.workflowRun ? `Opened by ${record.workflowRun.name} run [${record.workflowRun.id}](${record.workflowRun.url}).` : 'Computed locally by `bun eng/ci/train-feed.mjs exit`.',
  ];
  return `${lines.join('\n')}\n`;
}

function workflowRun(environment) {
  if (!environment.GITHUB_RUN_ID) return null;
  return { name: basename((environment.GITHUB_WORKFLOW_REF ?? '').split('@')[0]) || environment.GITHUB_WORKFLOW,
    id: environment.GITHUB_RUN_ID, url: `${environment.GITHUB_SERVER_URL}/${environment.GITHUB_REPOSITORY}/actions/runs/${environment.GITHUB_RUN_ID}` };
}

function writeRecord(out, record) {
  mkdirSync(out, { recursive: true });
  writeFileSync(join(out, 'exit.json'), `${JSON.stringify(record, null, 2)}\n`);
  writeFileSync(join(out, 'pr-body.md'), exitBody(record));
}

const gitIn = cwd => args => execFileSync('git', args, { cwd, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });

export async function exit(args = [], {
  cwd = root, environment = process.env, token = environment.GH_TOKEN ?? environment.GITHUB_TOKEN, log = console.log, now = Date.now,
  sleep = ms => new Promise(done => setTimeout(done, ms)), intervalMs = 30000, git, findFeed = feedRun, lists = nugetLists,
  npmListed = npmPublished, npmMatches = registryMatches, tagCommit = upstreamTagCommit,
} = {}) {
  // --tree: the checkout to compute the exit in (a dry run's `from` commit, which predates this script); default this one.
  const tree = resolve(cwd, option(args, '--tree') ?? '.');
  git ??= gitIn(tree);
  const out = resolve(cwd, option(args, '--out') ?? 'artifacts/feed-exit');
  const minutes = Number(option(args, '--wait') ?? 70);
  if (!(minutes > 0)) throw new Error(`--wait takes a number of minutes, not ${option(args, '--wait')}`);
  const dryRun = args.includes('--dry-run');
  const props = readFileSync(join(tree, PROPS), 'utf8');
  const versions = trainVersions(props), commits = trainCommits(props);
  const leaving = UPSTREAMS.filter(key => commits[key]);
  rmSync(out, { recursive: true, force: true });
  if (!leaving.length) {
    log(`nothing to exit: ${PROPS} names no TrainCommit, so runic-sdk already restores from nuget.org only.`);
    setOutput({ exit: 'false' }, environment);
    return null;
  }
  const options = { token };
  const since = git(['log', '-1', '--format=%H', '--', PROPS]).trim();
  const found = await findFeed(since, versions, leaving, options);
  log(`Feed run ${found.run.url} (${found.run.headSha}), at or after ${since}, the last change of ${PROPS}.`);
  const nixText = readFileSync(join(tree, NIX_FIXTURE), 'utf8');
  const nixVersion = nixFixtureVersion(nixText);
  const nix = nixVersion === versions.cli ? null : { project: NIX_FIXTURE, from: nixVersion, to: versions.cli };

  const nuget = leaving.flatMap(key => (found.feed.upstreams[key].packages ?? []).map(item => ({ registry: 'nuget', name: item.id, version: item.version, upstream: key })));
  if (nix && !nuget.some(item => item.name === repos.cli.anchor && item.version === versions.cli))
    nuget.push({ registry: 'nuget', name: repos.cli.anchor, version: versions.cli, upstream: 'cli' });
  const npm = leaving.flatMap(key => (found.feed.upstreams[key].npm ?? []).map(item => ({ name: item.name, version: item.version, sha256: item.sha256, upstream: key })));
  log(`Waiting up to ${minutes} minutes for nuget.org to list ${nuget.length} and npm to serve ${npm.length} package(s).`);
  await waitIndexed(nuget, npm, { minutes, intervalMs, now, sleep, log, lists, npmListed, versions, options });

  // The template locks hold the integrity of the npm archive the feed served; they stay valid only if npm serves
  // those bytes. Rebinding 15 template locks is agent work, so a difference stops the exit.
  for (const item of npm) {
    let same;
    try {
      same = await npmMatches({ registry: 'npm', name: item.name, version: item.version, sha256: item.sha256 }, options);
    } catch (error) {
      if (!/Registry bytes differ/.test(error.message)) throw error;
      same = false;
    }
    if (!same) throw new Error(`The published npm package ${item.name}@${item.version} differs from the candidate the feed served (SHA-256 ${item.sha256}); ` +
      'the template locks need rebinding to npm\'s integrity (agent work). No pull request is opened.');
  }

  const upstreams = {};
  for (const key of leaving) {
    const served = found.feed.upstreams[key];
    const tag = `v${versions[key]}`;
    const commit = await tagCommit(repos[key].repository, tag, options);
    upstreams[key] = {
      title: repos[key].title, repository: repos[key].repository, anchor: repos[key].anchor, version: versions[key], trainCommit: commits[key],
      servedRun: served.run ? { id: served.run.id, url: served.run.url, headSha: served.run.headSha } : null,
      tag: { name: tag, commit, equalsTrainCommit: commit === commits[key] },
      nuget: nuget.filter(item => item.upstream === key).map(item => `${item.name} ${item.version}`),
      npm: npm.filter(item => item.upstream === key).map(({ name, version, sha256 }) => ({ name, version, sha256, npmMatches: true })),
    };
  }

  const after = dropTrainCommits(props);
  const edits = [];
  const record = (file, before, text) => before.split('\n').forEach((line, index) => {
    const to = text.split('\n')[index];
    if (line !== to) edits.push({ file, line: index + 1, before: line.trim(), after: to.trim() });
  });
  record(PROPS, props, after);
  const nixAfter = nix ? moveNixFixture(nixText, nix.to) : nixText;
  record(NIX_FIXTURE, nixText, nixAfter);
  if (!dryRun) {
    writeFileSync(join(tree, PROPS), after);
    if (nix) writeFileSync(join(tree, NIX_FIXTURE), nixAfter);
  }
  const result = {
    schema: EXIT_SCHEMA, dryRun, base: git(['rev-parse', 'HEAD']).trim(), since,
    feedRun: found.run, upstreams, nix, nixVersion, edits, locks: null, workflowRun: workflowRun(environment),
  };
  result.title = exitTitle(result);
  result.marker = exitMarker(result);
  writeRecord(out, result);
  for (const edit of edits) log(`${edit.file}:${edit.line}\n  - ${edit.before}\n  + ${edit.after}`);
  log(`${dryRun ? 'Dry run: the checkout is unchanged. ' : ''}${result.title}. ${join(out, 'exit.json')} and pr-body.md written.`);
  setOutput({ exit: 'true' }, environment);
  return result;
}

// What the lock files' diff changed: the changed files, the changed contentHash lines, and the files with other
// changed lines (the Nix fixture's lock, which moves with its Command Line, is expected to).
export function lockChanges(diff, expected = []) {
  const files = new Map();
  let file = null;
  for (const line of diff.split('\n')) {
    const header = /^diff --git a\/(.+) b\//.exec(line);
    if (header) { file = header[1]; files.set(file, { contentHash: 0, other: 0 }); continue; }
    if (!file || !/^[+-]/.test(line) || /^(\+\+\+|---) /.test(line)) continue;
    if (/^[+-]\s*"contentHash":/.test(line)) { if (line.startsWith('+')) files.get(file).contentHash++; }
    else files.get(file).other++;
  }
  return {
    files: [...files.keys()],
    contentHash: [...files.values()].reduce((sum, item) => sum + item.contentHash, 0),
    other: [...files].filter(([name, item]) => item.other && !expected.includes(name)).map(([name, item]) => ({ file: name, lines: item.other })),
  };
}

// After `bun run lock:nuget` (and the Nix fixture's restore): the required checks of the whole change, then the lock
// file result in exit.json and pr-body.md.
export function exitReport(args = [], { cwd = root, git, log = console.log } = {}) {
  const tree = resolve(cwd, option(args, '--tree') ?? '.');
  git ??= gitIn(tree);
  const out = resolve(cwd, option(args, '--out') ?? 'artifacts/feed-exit');
  const result = JSON.parse(readFileSync(join(out, 'exit.json'), 'utf8'));
  if (result.dryRun) throw new Error('exit.json is of a dry run (--dry-run), which leaves the checkout unchanged');
  assertPropsExit(git(['show', `HEAD:${PROPS}`]), readFileSync(join(tree, PROPS), 'utf8'));
  const nixBefore = git(['show', `HEAD:${NIX_FIXTURE}`]);
  if (readFileSync(join(tree, NIX_FIXTURE), 'utf8') !== (result.nix ? moveNixFixture(nixBefore, result.nix.to) : nixBefore))
    throw new Error(`${NIX_FIXTURE} may only change its Runic.CommandLine version${result.nix ? ` to ${result.nix.to}` : ', and only when Command Line moves'}`);
  const nixLock = join(dirname(NIX_FIXTURE), 'packages.lock.json');
  result.locks = lockChanges(git(['diff', '-U0', '--', ':(glob)**/packages.lock.json', ':(glob)**/packages.*.lock.json']), result.nix ? [nixLock] : []);
  writeRecord(out, result);
  log(`Lock files: ${result.locks.files.length} changed, ${result.locks.contentHash} contentHash line(s).`);
  for (const item of result.locks.other) {
    const text = `${item.file} changed ${item.lines} line(s) other than contentHash; only contentHash lines should change (${README}).`;
    log(process.env.GITHUB_ACTIONS ? `::warning title=Feed exit::${text}` : text);
  }
  return result;
}

const USAGE = `Usage: bun eng/ci/train-feed.mjs prepare [--output <dir>] [--cli run@<commit>|run:<id>] [--translations run@<commit>|run:<id>]
       bun eng/ci/train-feed.mjs exit [--wait <minutes>] [--out <dir>] [--tree <checkout>] [--dry-run]
       bun eng/ci/train-feed.mjs exit-report [--out <dir>] [--tree <checkout>]`;

if (import.meta.main) {
  const [command, ...args] = process.argv.slice(2);
  const commands = { prepare, exit, 'exit-report': exitReport };
  if (!Object.hasOwn(commands, command)) {
    console.error(USAGE);
    process.exit(2);
  }
  try {
    await commands[command](args);
  } catch (error) {
    console.error(process.env.GITHUB_ACTIONS ? `::error title=${command === 'prepare' ? 'Train feed' : 'Feed exit'}::${error.message}` : error.message);
    process.exit(1);
  }
}
