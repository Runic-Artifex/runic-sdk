import { createHash } from 'node:crypto';
import { promises as fs } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { spawn } from 'node:child_process';

export const EXAMPLES_REVISION = '494b7325d08ba405713f6ea0fe26680772caa3f1';
export const EXAMPLES_TREE = '773e3e19cfceb365bd3feb1357f61645b31594b4';
export const EDITOR_REVISION = '5bcb157004deaf196a3dc8e6c7d911d7c6f881d7';
export const EDITOR_TREE = '7741959d4342bbe9c486e9018b9668e1f866346e';
export const REQUIRED_METRICS = [
  'creation-ceremony', 'host-wiring', 'starter-package-references', 'consumer-package-pins', 'editor-orchestration',
  'typescript-consumer', 'clean-frontend-build', 'change-to-visible-reload', 'launch-to-visible-startup',
  'package-only-canaries', 'nativeaot-linux-x64'
];
export const CLOCK = 'process.hrtime.bigint-monotonic-nanoseconds';

export function isReleaseAuthorityRoot(fileName, gitPrefix) {
  return fileName === 'runic.release.json' && gitPrefix === '';
}

export function metric(id, category, unit, options = {}) {
  return { id, category, status: options.status ?? 'passed', required: options.required ?? true, unit,
    argv: options.argv ?? ['static-source-inspection'], cwd: options.cwd ?? 'archive', clock: options.clock ?? 'none',
    warmups: options.warmups ?? 0, samples: options.samples ?? 0, observations: options.observations ?? [],
    summary: options.summary ?? null, details: options.details ?? {}, reasonCode: options.reasonCode ?? null };
}
export const failed = (id, category, unit, reasonCode, options = {}) => metric(id, category, unit, { ...options, status: 'failed', reasonCode });
export const blocked = (id, category, unit, reasonCode, options = {}) => metric(id, category, unit, { ...options, status: 'blocked', reasonCode });
export function timingSummary(values) {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const rank = p => sorted[Math.ceil((p / 100) * sorted.length) - 1];
  return { min: sorted[0], p50: rank(50), p95: rank(95), max: sorted.at(-1) };
}
export function ordered(measurements) { return [...measurements].sort((a, b) => a.id.localeCompare(b.id)); }

export async function exec(command, args, cwd, options = {}) {
  const started = process.hrtime.bigint();
  const timeoutMs = options.timeoutMs ?? 120_000;
  return await new Promise(resolve => {
    let done = false; let timedOut = false;
    const errors = []; const output = []; let errorBytes = 0; let outputBytes = 0;
    const child = spawn(command, args, { cwd, stdio: ['ignore', options.captureStdout ? 'pipe' : 'ignore', options.captureStderr ? 'pipe' : 'ignore'], env: { ...process.env, ...options.env }, detached: process.platform !== 'win32' });
    if (options.captureStderr) child.stderr.on('data', chunk => { if (errorBytes < 8192) { errors.push(chunk.subarray(0, 8192 - errorBytes)); errorBytes += chunk.length; } });
    if (options.captureStdout) child.stdout.on('data', chunk => { if (outputBytes < 8192) { output.push(chunk.subarray(0, 8192 - outputBytes)); outputBytes += chunk.length; } });
    const finish = value => { if (!done) { done = true; clearTimeout(timer); resolve({ ...value, stdout: options.captureStdout ? Buffer.concat(output).toString('utf8').trim().slice(0, 8192) : null, nanoseconds: Number(process.hrtime.bigint() - started) }); } };
    const timer = setTimeout(() => { timedOut = true; try { process.platform === 'win32' ? child.kill('SIGKILL') : process.kill(-child.pid, 'SIGKILL'); } catch {} }, timeoutMs);
    child.on('error', error => finish({ ok: false, exitCode: null, reasonCode: error.code === 'ENOENT' ? 'command-not-found' : 'command-spawn-failed' }));
    child.on('close', code => {
      // Auth is only meaningful while acquiring from GitHub Packages. Generic
      // 401/403 failures in build/run output must remain ordinary failures.
      const stderr = Buffer.concat(errors).toString('utf8');
      const githubPackages = options.acquisition === 'github-packages' && /(?:nuget|npm)\.pkg\.github\.com/i.test(stderr);
      const auth = githubPackages && /(?:\b401\b|\b403\b|unauthori[sz]ed|forbidden)/i.test(stderr);
      finish({ ok: !timedOut && code === 0, exitCode: code, reasonCode: timedOut ? 'command-timeout' : code === 0 ? null : auth ? 'github-packages-auth-required' : 'command-exit-nonzero' });
    });
  });
}
export async function commandText(command, args, cwd, timeoutMs = 15_000) {
  return await new Promise(resolve => {
    let done = false; let bytes = 0; const chunks = []; const child = spawn(command, args, { cwd, stdio: ['ignore', 'pipe', 'ignore'], detached: process.platform !== 'win32' });
    const finish = value => { if (!done) { done = true; clearTimeout(timer); resolve(value); } };
    const timer = setTimeout(() => { try { process.platform === 'win32' ? child.kill('SIGKILL') : process.kill(-child.pid, 'SIGKILL'); } catch {} finish(null); }, timeoutMs);
    child.stdout.on('data', part => { if (bytes < 8192) { chunks.push(part.subarray(0, 8192 - bytes)); bytes += part.length; } }); child.on('error', () => finish(null));
    child.on('close', code => finish(code === 0 ? Buffer.concat(chunks).toString('utf8').trim().slice(0, 8192) : null));
  });
}
async function commandBuffer(command, args, cwd, timeoutMs = 15_000) {
  return await new Promise(resolve => {
    let done = false; const chunks = []; const child = spawn(command, args, { cwd, stdio: ['ignore', 'pipe', 'ignore'], detached: process.platform !== 'win32' });
    const finish = value => { if (!done) { done = true; clearTimeout(timer); resolve(value); } };
    const timer = setTimeout(() => { try { process.platform === 'win32' ? child.kill('SIGKILL') : process.kill(-child.pid, 'SIGKILL'); } catch {} finish(null); }, timeoutMs);
    child.stdout.on('data', part => chunks.push(part)); child.on('error', () => finish(null)); child.on('close', code => finish(code === 0 ? Buffer.concat(chunks) : null));
  });
}
export async function sha256(file) { return createHash('sha256').update(await fs.readFile(file)).digest('hex'); }
async function releaseSnapshot(root) {
  const [revision, tree, status] = await Promise.all([
    commandText('git', ['rev-parse', 'HEAD'], root), commandText('git', ['rev-parse', 'HEAD^{tree}'], root), commandText('git', ['status', '--porcelain=v1', '--untracked-files=all'], root)
  ]);
  if (!revision || !tree || status === null) throw new Error('release-manifest-snapshot-failed');
  return { revision, tree, status };
}
async function verifyCommittedRelease(root, revision) {
  const directory = await fs.mkdtemp(path.join(tmpdir(), `runic-release-authority-${process.pid}-`)); const archive = path.join(directory, 'release.tar');
  try {
    const archived = await exec('git', ['archive', '--format=tar', '--output', archive, revision], root, { timeoutMs: 30_000 });
    const extracted = archived.ok ? await exec('tar', ['-xf', archive, '-C', directory], root, { timeoutMs: 30_000 }) : { ok: false };
    const verified = extracted.ok ? await exec(process.execPath, ['eng/verify-release-manifest.mjs', 'runic.release.json', 'runic.release.schema.json'], directory, { timeoutMs: 30_000 }) : { ok: false };
    if (!verified.ok) throw new Error('release-manifest-semantic-verification-failed');
  } finally { await fs.rm(directory, { recursive: true, force: true }); }
}
export async function releaseManifestFacts(manifestPath) {
  const file = path.resolve(manifestPath);
  const directory = path.dirname(file);
  const root = await commandText('git', ['rev-parse', '--show-toplevel'], directory);
  if (!root) throw new Error('release-manifest-git-root-not-found');
  const required = ['runic.release.json', 'runic.release.schema.json', 'eng/verify-release-manifest.mjs'];
  const [prefix, tracked] = await Promise.all([
    commandText('git', ['rev-parse', '--show-prefix'], directory),
    Promise.all(required.map(item => commandText('git', ['cat-file', '-e', `HEAD:${item}`], root))),
  ]);
  if (!isReleaseAuthorityRoot(path.basename(file), prefix) || tracked.some(value => value === null)) throw new Error('release-manifest-authority-inputs-must-be-tracked');
  const relative = 'runic.release.json';
  const before = await releaseSnapshot(root); if (before.status !== '') throw new Error('release-manifest-source-not-clean');
  const blob = await commandBuffer('git', ['show', `${before.revision}:${relative}`], root); if (!blob) throw new Error('release-manifest-committed-blob-missing');
  await verifyCommittedRelease(root, before.revision);
  let manifest;
  try { manifest = JSON.parse(blob.toString('utf8')); } catch { throw new Error('release-manifest-invalid-json'); }
  if (!Array.isArray(manifest.canonicalPackages)) throw new Error('release-manifest-canonical-packages-missing');
  const publicPackages = manifest.canonicalPackages.filter(item => item && typeof item === 'object' && item.state === 'approved');
  if (publicPackages.some(item => item.ecosystem !== 'nuget' && item.ecosystem !== 'npm')) throw new Error('release-manifest-public-package-ecosystem-invalid');
  const counts = Object.fromEntries(['nuget', 'npm'].map(ecosystem => [ecosystem, publicPackages.filter(item => item.ecosystem === ecosystem).length]));
  return { path: relative, revision: before.revision, tree: before.tree, digest: createHash('sha256').update(blob).digest('hex'), publicPackageCounts: counts, before, after: before };
}
export async function releaseManifestAfter(manifestPath, facts) {
  const root = await commandText('git', ['rev-parse', '--show-toplevel'], path.dirname(path.resolve(manifestPath)));
  if (!root) throw new Error('release-manifest-git-root-not-found');
  const after = await releaseSnapshot(root);
  if (after.status !== '' || JSON.stringify(after) !== JSON.stringify(facts.before)) throw new Error('release-manifest-source-changed-during-measurement');
  return { ...facts, after };
}
export async function snapshot(root) {
  return { revision: await commandText('git', ['rev-parse', 'HEAD'], root), tree: await commandText('git', ['rev-parse', 'HEAD^{tree}'], root), status: await commandText('git', ['status', '--porcelain=v1', '--untracked-files=no'], root) };
}
export async function archiveRevision(root, revision, tree, label, command = exec) {
  const before = await snapshot(root);
  if (before.status !== '') throw new Error(`${label}-source-checkout-not-clean`);
  const revisionTree = await commandText('git', ['rev-parse', `${revision}^{tree}`], root);
  if (revisionTree !== tree) throw new Error(`${label}-revision-tree-mismatch`);
  const archive = path.join(tmpdir(), `runic-v02-${label}-${process.pid}-${Date.now()}.tar`);
  const directory = await fs.mkdtemp(path.join(tmpdir(), `runic-v02-${label}-`));
  const archived = await command('git', ['archive', '--format=tar', '--output', archive, revision], root);
  const extracted = archived.ok ? await command('tar', ['-xf', archive, '-C', directory], root) : { ok: false };
  await fs.rm(archive, { force: true });
  const after = await snapshot(root);
  if (!archived.ok || !extracted.ok || JSON.stringify(before) !== JSON.stringify(after)) { await fs.rm(directory, { recursive: true, force: true }); throw new Error(`${label}-archive-or-source-immutability-failed`); }
  return { directory, revision, tree, before, after };
}
export async function removeArchive(archive) { if (archive?.directory) await fs.rm(archive.directory, { recursive: true, force: true, maxRetries: 2 }); }
async function read(root, relative) { return fs.readFile(path.join(root, relative), 'utf8'); }
function references(text, expression) { return [...text.matchAll(expression)].map(match => match[1]).sort(); }

export async function staticMeasurements(examples, editor) {
  const [readme, program, project, frontend, pins, editorProgram, editorProject] = await Promise.all([
    read(examples.directory, 'samples/04-SvelteKitSetupApplication/README.md'), read(examples.directory, 'samples/04-SvelteKitSetupApplication/Program.cs'),
    read(examples.directory, 'samples/04-SvelteKitSetupApplication/SvelteKitSetupApplication.csproj'), read(examples.directory, 'samples/04-SvelteKitSetupApplication/Frontend/package.json'),
    read(examples.directory, 'Directory.Packages.props'), read(editor.directory, 'Program.cs'), read(editor.directory, 'RunicTranslations.Editor.csproj')
  ]);
  const commands = ['npm ci', 'npm run verify', 'dotnet run --project samples/04-SvelteKitSetupApplication'];
  const markers = ['string webRoot =', 'new FrontendAssetManifestBuilder()', 'new DirectoryFrontendAssetProvider(', 'WebUiApp.CreateBuilder(args)', 'new ApplicationBridgeFrontendApplicationOptions(', 'builder.UseApplicationBridge(', 'builder.RunAsync()'];
  const found = markers.filter(marker => program.includes(marker));
  const dotnet = references(project, /<PackageReference Include="([^"]+)"/g);
  const packageJson = JSON.parse(frontend); const npm = Object.keys({ ...packageJson.dependencies, ...packageJson.devDependencies }).filter(key => key.startsWith('@runic-artifex/')).sort();
  const central = references(pins, /<PackageVersion Include="(Runic[^"]+)"/g);
  const editorBindings = (editorProgram.match(/\.Bind(?:Async)?\(/g) ?? []).length;
  const parserHelpers = ['ArgumentValue(', 'PositionalWorkspace('].filter(item => editorProgram.includes(item)).length;
  const execs = (editorProject.match(/<Exec\s+WorkingDirectory="Frontend"/g) ?? []).length;
  const editorPackages = references(editorProject, /<PackageReference Include="([^"]+)"/g);
  return [
    metric('creation-ceremony', 'static', 'commands', { details: { expected: 3, commands, noTemplate: true }, status: commands.every(command => readme.includes(command)) ? 'passed' : 'failed', reasonCode: commands.every(command => readme.includes(command)) ? null : 'creation-ceremony-drift' }),
    metric('host-wiring', 'static', 'markers', { details: { expected: 7, markers: found }, status: found.length === 7 ? 'passed' : 'failed', reasonCode: found.length === 7 ? null : 'host-marker-drift' }),
    metric('starter-package-references', 'static', 'references', { details: { dotnet, npm, expectedDotnet: 5, expectedNpm: 4 }, status: dotnet.length === 5 && npm.length === 4 ? 'passed' : 'failed', reasonCode: dotnet.length === 5 && npm.length === 4 ? null : 'starter-reference-drift' }),
    metric('consumer-package-pins', 'static', 'pins', { details: { pins: central, expected: 24 }, status: central.length === 24 ? 'passed' : 'failed', reasonCode: central.length === 24 ? null : 'central-pin-drift' }),
    metric('editor-orchestration', 'static', 'bindings', { details: { bindings: editorBindings, parserHelpers, frontendExecs: execs, directPackageReferences: editorPackages, expected: { bindings: 15, parserHelpers: 2, frontendExecs: 2, directPackageReferences: 4 } }, status: editorBindings === 15 && parserHelpers === 2 && execs === 2 && editorPackages.length === 4 ? 'passed' : 'failed', reasonCode: editorBindings === 15 && parserHelpers === 2 && execs === 2 && editorPackages.length === 4 ? null : 'editor-orchestration-drift' })
  ];
}
export async function sourceHashes(root) {
  const files = ['package-lock.json', 'NuGet.config', 'Directory.Packages.props', 'global.json'];
  return Object.fromEntries(await Promise.all(files.map(async file => [file, await sha256(path.join(root, file))])));
}
export async function findExecutable(candidates, root) {
  for (const candidate of candidates.filter(Boolean)) {
    const resolved = await commandText('sh', ['-lc', 'command -v -- "$1"', 'sh', candidate], root);
    if (resolved) return resolved.split('\n')[0];
  }
  return null;
}
export async function environmentFacts(root, editorRoot = null, scratchNugetConfig = null) {
  const names = { node: ['node'], npm: ['npm'], dotnet: ['dotnet'], chromium: [process.env.WEBUI_BROWSER_PATH, 'chromium', 'chromium-browser', 'google-chrome', 'google-chrome-stable', 'chrome'], git: ['git'], tar: ['tar'], cc: ['cc'], linker: ['ld'], file: ['file'], readelf: ['readelf'] };
  const tools = Object.fromEntries(await Promise.all(Object.entries(names).map(async ([name, candidates]) => {
    const executableName = await findExecutable(candidates, root); const version = executableName ? await commandText(executableName, ['--version'], root) : null;
    return [name, { path: executableName, version }];
  })));
  const packageJson = JSON.parse(await read(root, 'package.json')); const lock = JSON.parse(await read(root, 'package-lock.json'));
  const props = await read(root, 'Directory.Packages.props');
  const nuget = [...props.matchAll(/<PackageVersion Include="([^"]+)" Version="([^"]+)"/g)].map(match => `${match[1]}@${match[2]}`).sort();
  const npm = Object.entries(lock.packages ?? {}).filter(([location, value]) => location.startsWith('node_modules/') && value.version).map(([location, value]) => `${location.slice('node_modules/'.length)}@${value.version}`).sort();
  if (!npm.length) npm.push(...Object.entries(packageJson.devDependencies ?? {}).map(([name, version]) => `${name}@${version}`));
  const hashes = await sourceHashes(root); if (scratchNugetConfig) hashes['eng/v0.2-baselines/scratch.NuGet.config'] = await sha256(scratchNugetConfig);
  if (editorRoot) for (const file of ['NuGet.config', 'global.json']) { try { hashes[`editor/${file}`] = await sha256(path.join(editorRoot, file)); } catch {} }
  return { os: process.platform, arch: process.arch, tools, packageIdentities: { npm, nuget }, hashes };
}
export async function resetFrontend(frontend) { await Promise.all(['build', '.svelte-kit'].map(name => fs.rm(path.join(frontend, name), { recursive: true, force: true }))); }
export async function inventory(directory, entry) {
  const files = []; async function walk(relative = '') { for (const child of await fs.readdir(path.join(directory, relative), { withFileTypes: true })) { const next = path.join(relative, child.name); if (child.isDirectory()) await walk(next); else if (child.isFile()) { const info = await fs.stat(path.join(directory, next)); files.push({ path: next.split(path.sep).join('/'), bytes: info.size, mode: info.mode & 0o777 }); } } }
  await walk(); files.sort((a, b) => a.path.localeCompare(b.path)); const executable = files.find(file => file.path === entry);
  return { files, totalBytes: files.reduce((total, file) => total + file.bytes, 0), entryBytes: executable?.bytes ?? null, entryMode: executable?.mode ?? null };
}
