#!/usr/bin/env bun
// The release candidate feed (D077.24; eng/release/README.md, "The editors checklist on the candidate"): serves the
// packages a runic-sdk release would publish to a Windows machine over SSH, so the runic-editors checklist runs against
// them while the publish and nuget.org's indexing are still ahead. A coordinator runs it; CI never does.
//
//   bun eng/ci/candidate-feed.mjs prepare (--commit <sha> | --run <id>) [--editors-commit <sha> | --editors-run <id>] [--output <dir>]
//   bun eng/ci/candidate-feed.mjs push --host <user@host> --identity <key> --known-hosts <file> [--remote <dir>] [--port <n>] [--output <dir>]
//   bun eng/ci/candidate-feed.mjs serve [--output <dir>]
//   bun eng/ci/candidate-feed.mjs clean [--output <dir>] [--remote <dir>]
//
// prepare downloads the runic-sdk-<run id> package artifact into <output> (default artifacts/candidate-feed). With
// --commit it takes exactly the run and artifact that Publish preview would publish for that commit
// (eng/release/ci-run.mjs, findCiPackages): after a release pull request's merge queue run passed, those are its bytes.
// --run takes a given successful ci.yml run and says whether the publish would take it. --editors-* add the
// vscode-vsix and visualstudio-vsix artifacts of a runic-editors ci.yml run. GH_TOKEN or GITHUB_TOKEN reads them.
//
// push copies the NuGet packages, the VSIX files and the scripts of eng/ci/candidate-feed/ to <remote> on the machine
// (default runic-candidate in the SSH user's home) and writes the configuration there: NuGet.config (the candidate
// folder feed and nuget.org, with its own globalPackagesFolder), candidate.npmrc and bunfig.toml (the @runic-artifex
// scope from the forwarded registry, with their own caches). A project uses them through Use-Candidate.ps1, so nothing
// outside <remote> changes. A folder of an earlier push is replaced; any other existing folder is refused.
//
// serve runs runic-sdk's candidate npm registry (tests/templates/template-npm-registry.mjs) on the host and forwards
// 127.0.0.1:<port> on the machine to it (ssh -R) until it is stopped; its tarball URLs name the port the machine asked
// (the request's Host), so an unbound lock fetches through the tunnel too. clean removes <remote> and the machine's .NET tool
// resolver cache entries that point into it. SSH runs with BatchMode and strict host key checking against the given
// known-hosts file only; the script never reads key material.
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { zipEntries } from '../nupkg.mjs';
import { findCiPackages } from '../release/ci-run.mjs';
import { nugetConfig, repos } from '../train/nuget.mjs';
import { resolveRun, runTrainFeed } from '../train/sources.mjs';

const root = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const SCHEMA = 'runic.sdk.candidate-feed/1';
export const EDITORS = 'Runic-Artifex/runic-editors';
export const EDITORS_ARTIFACTS = ['vscode-vsix', 'visualstudio-vsix'];
export const DEFAULT_PORT = 48731;
const GITHUB = 'https://api.github.com';
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const integrity = bytes => `sha512-${createHash('sha512').update(bytes).digest('base64')}`;

// A Windows path with forward slashes, which NuGet, npm, Bun, PowerShell and scp all accept.
export const windowsPath = path => path.replaceAll('\\', '/').replace(/\/+$/, '');
// scp and sftp address an absolute Windows path as /C:/...
export const scpPath = path => /^[A-Za-z]:\//.test(path) ? `/${path}` : path;
const xml = value => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');

// The machine's NuGet.config: nuget.org and the candidate folder for runic-sdk's package ids. Both sources serve those
// ids, because the editors' fixtures restore published Runic releases too; until the publish only the folder has the
// candidate's version. The packages go to a folder of their own, so a candidate never stands in for the published
// package of the same version in the user's global packages folder.
export function machineNugetConfig(remote) {
  const feed = `${remote}/nuget`;
  const text = nugetConfig({ sdk: feed }).replaceAll('"train-sdk"', '"runic-candidate"');
  const sdk = repos.sdk.patterns.map(pattern => `      <package pattern="${xml(pattern)}" />`).join('\n');
  const nugetOrg = '    <packageSource key="nuget.org">\n';
  if (!text.includes(nugetOrg) || !text.includes('"runic-candidate"')) throw new Error('eng/train/nuget.mjs nugetConfig() changed; update machineNugetConfig()');
  return text
    .replace(nugetOrg, `${nugetOrg}${sdk}\n`)
    .replace('<configuration>\n', `<configuration>\n  <config>\n    <add key="globalPackagesFolder" value="${xml(`${remote}/packages`)}" />\n  </config>\n`);
}

// The lines Use-Candidate.ps1 adds to a frontend's .npmrc (npm, pnpm and Bun read the scope's registry from it), and
// the bunfig.toml it writes there. npm and Bun keep the candidate's archives in caches of their own (pnpm's store is
// keyed by integrity).
export function machineNpmrc(remote, port) {
  return [`@runic-artifex:registry=http://127.0.0.1:${port}/`, `cache=${remote}/npm-cache`, 'fetch-timeout=60000', ''].join('\n');
}
export function machineBunfig(remote) {
  return `[install.cache]\ndir = "${remote}/bun-cache"\n`;
}

async function api(path, token) {
  const response = await fetch(`${GITHUB}/${path}`, { headers: {
    accept: 'application/vnd.github+json', 'x-github-api-version': '2022-11-28', ...(token ? { authorization: `Bearer ${token}` } : {}) } });
  if (!response.ok) throw new Error(`GitHub API ${path.split('?')[0]}: HTTP ${response.status}`);
  return response;
}

// The newest successful ci.yml run of the editors for a commit (a pull request's run is the release PR's candidate).
export function editorsRunOf(runs, commit) {
  const green = runs.filter(run => run.head_sha === commit && run.status === 'completed' && run.conclusion === 'success'
    && run.path?.split('@')[0] === '.github/workflows/ci.yml').sort((a, b) => b.id - a.id);
  if (!green.length) throw new Error(`${EDITORS} has no successful ci.yml run for ${commit}`);
  return green[0];
}

async function editorsCandidate(values, directory, token) {
  let run;
  if (values['editors-run']) run = await (await api(`repos/${EDITORS}/actions/runs/${values['editors-run']}`, token)).json();
  else {
    const commit = values['editors-commit'];
    if (!/^[0-9a-f]{40}$/.test(commit)) throw new Error('--editors-commit takes a full commit SHA');
    run = editorsRunOf((await (await api(`repos/${EDITORS}/actions/workflows/ci.yml/runs?head_sha=${commit}&per_page=50`, token)).json()).workflow_runs ?? [], commit);
  }
  if (run.status !== 'completed' || run.conclusion !== 'success') throw new Error(`${EDITORS} run ${run.html_url} is ${run.status}/${run.conclusion}, not a success`);
  const { artifacts } = await (await api(`repos/${EDITORS}/actions/runs/${run.id}/artifacts?per_page=100`, token)).json();
  const files = [];
  mkdirSync(join(directory, 'vsix'), { recursive: true });
  for (const name of EDITORS_ARTIFACTS) {
    const artifact = artifacts.find(item => item.name === name && !item.expired);
    if (!artifact) throw new Error(`${EDITORS} run ${run.html_url} has no unexpired ${name} artifact`);
    const archive = join(tmpdir(), `runic-candidate-${process.pid}-${name}.zip`);
    writeFileSync(archive, Buffer.from(await (await api(`repos/${EDITORS}/actions/artifacts/${artifact.id}/zip`, token)).arrayBuffer()));
    try {
      for (const entry of zipEntries(archive)) {
        if (!entry.name.endsWith('.vsix')) continue;
        const bytes = entry.read();
        writeFileSync(join(directory, 'vsix', basename(entry.name)), bytes);
        files.push({ name: basename(entry.name), artifact: { id: artifact.id, name }, sha256: sha256(bytes) });
      }
    } finally { rmSync(archive, { force: true }); }
  }
  return { repository: EDITORS, run: { id: run.id, url: run.html_url, headSha: run.head_sha, event: run.event, branch: run.head_branch }, files };
}

export async function prepare(values, { token = process.env.GH_TOKEN ?? process.env.GITHUB_TOKEN } = {}) {
  if (Boolean(values.commit) === Boolean(values.run)) throw new Error('prepare takes --commit <sha> or --run <id>');
  if (values['editors-commit'] && values['editors-run']) throw new Error('prepare takes --editors-commit or --editors-run, not both');
  const output = resolve(values.output);
  const work = join(tmpdir(), `runic-candidate-feed-${process.pid}`);
  rmSync(output, { recursive: true, force: true });
  mkdirSync(output, { recursive: true });
  try {
    let resolved, publish;
    if (values.commit) {
      // Exactly the run and artifact Publish preview takes for this commit.
      const found = await findCiPackages({ repository: repos.sdk.repository, sha: values.commit, token });
      resolved = await resolveRun(repos.sdk, { kind: 'run', argument: found.runId }, work, { token });
      if (String(resolved.run.artifact.id) !== found.artifactId)
        throw new Error(`Run ${found.runUrl} lists artifact ${resolved.run.artifact.id}, but the publish would take ${found.artifactId}; prepare again`);
      publish = { selected: true, note: `Publish preview at ${values.commit} takes this run and artifact.` };
    } else {
      resolved = await resolveRun(repos.sdk, { kind: 'run', argument: values.run }, work, { token });
      const trainFeed = await runTrainFeed(repos.sdk, resolved.run.id, join(work, 'train-feed'), { token });
      try {
        const found = await findCiPackages({ repository: repos.sdk.repository, sha: resolved.run.headSha, token });
        const same = found.runId === String(resolved.run.id) && found.artifactId === String(resolved.run.artifact.id);
        publish = { selected: same, note: same ? `Publish preview at ${resolved.run.headSha} takes this run and artifact.`
          : `Publish preview at ${resolved.run.headSha} would take run ${found.runUrl} (artifact ${found.artifactId}), not this one.` };
      } catch (error) {
        publish = { selected: false, note: `Publish preview at ${resolved.run.headSha} would refuse: ${error.message}` };
      }
      if (trainFeed) publish.note += ' This run built against the train feed.';
    }
    for (const kind of ['nuget', 'npm']) mkdirSync(join(output, kind), { recursive: true });
    for (const item of resolved.packages) cpSync(item.file, join(output, 'nuget', basename(item.file)));
    const npm = resolved.npm.map(item => {
      const bytes = readFileSync(item.file);
      cpSync(item.file, join(output, 'npm', basename(item.file)));
      return { name: item.name, version: item.version, file: basename(item.file), sha256: item.sha256, integrity: integrity(bytes) };
    }).sort((a, b) => a.name.localeCompare(b.name));
    const marker = {
      schema: SCHEMA,
      sdk: {
        repository: repos.sdk.repository, version: resolved.version, run: resolved.run, publish,
        packages: resolved.packages.map(({ id, version, sha256: hash, file }) => ({ id, version, file: basename(file), sha256: hash })).sort((a, b) => a.id.localeCompare(b.id)),
        npm,
      },
      editors: values['editors-commit'] || values['editors-run'] ? await editorsCandidate(values, output, token) : null,
    };
    writeFileSync(join(output, 'candidate.json'), `${JSON.stringify(marker, null, 2)}\n`);
    console.log(`runic-sdk ${resolved.version}: ${marker.sdk.packages.length} NuGet and ${npm.length} npm packages from ${resolved.run.url}, artifact ${resolved.run.artifact.id} (${resolved.run.headSha}, ${resolved.run.event}).`);
    console.log(publish.note);
    if (marker.editors) console.log(`runic-editors: ${marker.editors.files.map(file => file.name).join(', ')} from ${marker.editors.run.url} (${marker.editors.run.headSha}).`);
    console.log(`Candidate in ${output}.`);
    return marker;
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

// SSH with the given key and known-hosts file only: no user configuration, no prompts, strict host key checking.
export function sshOptions({ identity, knownHosts }) {
  return ['-F', '/dev/null', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', `UserKnownHostsFile=${resolve(knownHosts)}`,
    '-o', 'IdentitiesOnly=yes', '-i', resolve(identity)];
}

// A PowerShell script for -EncodedCommand (UTF-16LE, base64), which reaches PowerShell intact from cmd or PowerShell.
export const encodedPowerShell = script => Buffer.from(script, 'utf16le').toString('base64');
const psString = value => `'${String(value).replaceAll("'", "''")}'`;

function remotePowerShell(machine, script) {
  const result = spawnSync('ssh', [...sshOptions(machine), machine.host,
    `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ${encodedPowerShell(script)}`], { encoding: 'utf8' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`ssh ${machine.host} failed (${result.status}): ${(result.stderr || result.stdout).trim().split('\n').slice(-5).join(' ')}`);
  return result.stdout.trim();
}

// The remote script that empties <remote>: only a folder an earlier push wrote (its candidate.json), and the tool
// resolver cache entries under it (a removed folder's entries break `dotnet tool run`, as on the host).
export function cleanScript(remote) {
  return `$ErrorActionPreference = 'Stop'
$remote = [IO.Path]::GetFullPath(${psString(remote)})
if (Test-Path -LiteralPath $remote) {
  $marker = Join-Path $remote 'candidate.json'
  if (!(Test-Path -LiteralPath $marker) -or (Get-Content -Raw -LiteralPath $marker) -notmatch '"${SCHEMA}"') { throw "$remote exists and is not a candidate feed folder; remove it by hand or choose another --remote." }
  Remove-Item -LiteralPath $remote -Recurse -Force
}
$cache = Join-Path $env:USERPROFILE '.dotnet\\toolResolverCache\\1'
$prefix = $remote.TrimEnd('\\') + '\\'
foreach ($file in @(Get-ChildItem -LiteralPath $cache -File -ErrorAction SilentlyContinue)) {
  # Windows PowerShell's ConvertFrom-Json emits a JSON array as one object; the variable enumerates it.
  $parsed = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
  $entries = @($parsed | ForEach-Object { $_ })
  $keep = @($entries | Where-Object { -not "$($_.PathToExecutable)".Replace('/', '\\').StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
  if ($keep.Count -eq $entries.Count) { continue }
  # Without a byte order mark, as dotnet writes it.
  if ($keep.Count) { [IO.File]::WriteAllText($file.FullName, (ConvertTo-Json -InputObject $keep -Compress -Depth 4)) } else { Remove-Item -LiteralPath $file.FullName }
  "pruned $($entries.Count - $keep.Count) resolver cache entries of $($file.Name)"
}
"clean $remote"
`;
}

const machineFile = output => join(output, 'machine.json');

function machineOf(values, output) {
  const saved = existsSync(machineFile(output)) ? JSON.parse(readFileSync(machineFile(output), 'utf8')) : {};
  const machine = { host: values.host ?? saved.host, identity: values.identity ?? saved.identity, knownHosts: values['known-hosts'] ?? saved.knownHosts,
    remote: values.remote ? windowsPath(values.remote) : saved.remote, port: Number(values.port ?? saved.port ?? DEFAULT_PORT) };
  for (const [key, name] of [['host', 'host'], ['identity', 'identity'], ['knownHosts', 'known-hosts']])
    if (!machine[key]) throw new Error(`--${name} is required (or push first, which records it)`);
  if (!Number.isInteger(machine.port) || machine.port < 1024 || machine.port > 65535) throw new Error(`--port must be 1024-65535, not ${values.port}`);
  return machine;
}

export function push(values) {
  const output = resolve(values.output);
  const marker = JSON.parse(readFileSync(join(output, 'candidate.json'), 'utf8'));
  if (marker.schema !== SCHEMA) throw new Error(`${output}/candidate.json is not a candidate feed`);
  const machine = machineOf(values, output);
  const home = windowsPath(remotePowerShell(machine, '[Environment]::GetFolderPath("UserProfile")'));
  const remote = windowsPath(values.remote ?? `${home}/runic-candidate`);
  if (!/^[A-Za-z]:\/.+\/[^/]+$/.test(remote)) throw new Error(`--remote must be an absolute Windows path below a folder (C:/Users/<user>/runic-candidate), not ${remote}`);
  console.log(remotePowerShell(machine, cleanScript(remote)));
  const staging = join(tmpdir(), `runic-candidate-push-${process.pid}`, basename(remote));
  rmSync(join(staging, '..'), { recursive: true, force: true });
  try {
    mkdirSync(staging, { recursive: true });
    cpSync(join(output, 'nuget'), join(staging, 'nuget'), { recursive: true });
    if (existsSync(join(output, 'vsix'))) cpSync(join(output, 'vsix'), join(staging, 'vsix'), { recursive: true });
    for (const file of readdirSync(join(root, 'eng/ci/candidate-feed'))) cpSync(join(root, 'eng/ci/candidate-feed', file), join(staging, file));
    mkdirSync(join(staging, 'apps'));
    writeFileSync(join(staging, 'candidate.json'), `${JSON.stringify({ ...marker, machine: { remote, port: machine.port } }, null, 2)}\n`);
    writeFileSync(join(staging, 'NuGet.config'), machineNugetConfig(remote));
    writeFileSync(join(staging, 'candidate.npmrc'), machineNpmrc(remote, machine.port));
    writeFileSync(join(staging, 'bunfig.toml'), machineBunfig(remote));
    // scp -r copies the staging folder, named as <remote>, into <remote>'s parent.
    const copied = spawnSync('scp', [...sshOptions(machine), '-q', '-r', staging, `${machine.host}:${scpPath(remote.slice(0, remote.lastIndexOf('/')))}/`], { stdio: 'inherit' });
    if (copied.status !== 0) throw new Error(`scp to ${machine.host} failed (${copied.status})`);
  } finally {
    rmSync(join(staging, '..'), { recursive: true, force: true });
  }
  writeFileSync(machineFile(output), `${JSON.stringify({ host: machine.host, identity: resolve(machine.identity), knownHosts: resolve(machine.knownHosts), remote, port: machine.port }, null, 2)}\n`);
  console.log(`Pushed runic-sdk ${marker.sdk.version}${marker.editors ? ' and the editors VSIX files' : ''} to ${machine.host}:${remote}. ` +
    `Serve the npm registry with: bun eng/ci/candidate-feed.mjs serve --output ${output}`);
  return { remote, port: machine.port };
}

export async function serve(values) {
  const output = resolve(values.output);
  const machine = machineOf(values, output);
  if (!machine.remote) throw new Error('push first: serve forwards the port that push wrote into the machine\'s configuration');
  const archives = readdirSync(join(output, 'npm')).filter(file => file.endsWith('.tgz')).map(file => join(output, 'npm', file));
  const ready = join(tmpdir(), `runic-candidate-registry-${process.pid}.url`);
  rmSync(ready, { force: true });
  const registry = spawn(process.execPath, [join(root, 'tests/templates/template-npm-registry.mjs'), ready, ...archives], { stdio: 'inherit' });
  for (let attempt = 0; attempt < 200 && !(existsSync(ready) && readFileSync(ready, 'utf8').trim()); attempt++) await new Promise(done => setTimeout(done, 50));
  if (!existsSync(ready)) { registry.kill(); throw new Error('The candidate npm registry did not start'); }
  const local = new URL(readFileSync(ready, 'utf8').trim()).port;
  rmSync(ready, { force: true });
  let stopping = false, tunnel = null, failures = 0;
  const stop = () => { stopping = true; tunnel?.kill(); registry.kill(); };
  process.on('SIGINT', stop);
  process.on('SIGTERM', stop);
  registry.on('exit', () => { if (!stopping) { console.error('The candidate npm registry exited.'); stop(); process.exitCode = 1; } });
  console.log(`Serving ${archives.length} npm packages on ${machine.host} at http://127.0.0.1:${machine.port}/ (host port ${local}); stop with Ctrl+C or SIGTERM.`);
  // The tunnel comes back after a dropped connection; ten failures in a row end the serve.
  while (!stopping) {
    const started = Date.now();
    tunnel = spawn('ssh', [...sshOptions(machine), '-N', '-o', 'ExitOnForwardFailure=yes', '-o', 'ServerAliveInterval=30', '-o', 'ServerAliveCountMax=3',
      '-R', `127.0.0.1:${machine.port}:127.0.0.1:${local}`, machine.host], { stdio: ['ignore', 'inherit', 'inherit'] });
    const code = await new Promise(done => tunnel.on('exit', done));
    if (stopping) break;
    failures = Date.now() - started > 60_000 ? 1 : failures + 1;
    if (failures >= 10) { console.error(`ssh -R exited ${code} ten times in a row; stopping.`); stop(); process.exitCode = 1; break; }
    console.error(`ssh -R exited ${code}; reconnecting in 5 s.`);
    await new Promise(done => setTimeout(done, 5000));
  }
}

export function clean(values) {
  const output = resolve(values.output);
  const machine = machineOf(values, output);
  if (!machine.remote) throw new Error(`${machineFile(output)} names no pushed folder; pass --remote <folder>`);
  console.log(remotePowerShell(machine, cleanScript(machine.remote)));
  rmSync(machineFile(output), { force: true });
}

if (import.meta.main) {
  const [command, ...args] = process.argv.slice(2);
  const { values } = parseArgs({ args, options: {
    output: { type: 'string', default: join(root, 'artifacts/candidate-feed') },
    commit: { type: 'string' }, run: { type: 'string' }, 'editors-commit': { type: 'string' }, 'editors-run': { type: 'string' },
    host: { type: 'string' }, identity: { type: 'string' }, 'known-hosts': { type: 'string' }, remote: { type: 'string' }, port: { type: 'string' },
  } });
  const commands = { prepare, push, serve, clean };
  if (!commands[command]) {
    console.error('Usage: bun eng/ci/candidate-feed.mjs prepare|push|serve|clean [options] (see the header of eng/ci/candidate-feed.mjs)');
    process.exit(2);
  }
  try {
    await commands[command](values);
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}
