#!/usr/bin/env node
import { promises as fs } from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { BrowserCleanupError, SERVER_PORT, startupSample, warmReloadSamples } from './browser-probe.mjs';
import { CLOCK, EDITOR_REVISION, EDITOR_TREE, EXAMPLES_REVISION, EXAMPLES_TREE, archiveRevision, blocked, environmentFacts, exec, failed, findExecutable, inventory, metric, ordered, releaseManifestAfter, releaseManifestFacts, removeArchive, resetFrontend, staticMeasurements, timingSummary } from './metrics.mjs';
import { SCHEMA_ID } from './contract.mjs';
import { verifyReceipt } from './verify.mjs';

const harness = path.dirname(fileURLToPath(import.meta.url));
const examplesDefault = path.resolve(harness, '../..');
export function parseArguments(argv) {
  const options = { examplesRoot: examplesDefault, editorRoot: null, releaseManifest: null, dynamic: false, verify: false, help: false };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '-h' || arg === '--help') options.help = true;
    else if (arg === '--dynamic' || arg === '--verify') options[arg.slice(2)] = true;
    else if (arg === '--workspace-root' || arg === '--editor-root' || arg === '--release-manifest') { const value = argv[++index]; if (!value || value.startsWith('-')) throw new Error(`Missing value for ${arg}`); options[arg === '--workspace-root' ? 'examplesRoot' : arg === '--editor-root' ? 'editorRoot' : 'releaseManifest'] = path.resolve(value); }
    else throw new Error(`Unknown argument: ${arg}`);
  }
  return options;
}
export const helpText = `Usage: node eng/v0.2-baselines/measure.mjs --editor-root <editor> --release-manifest <runic.release.json> [--verify]\n\nThe receipt always archives and measures fixed v0.2 revisions. Dynamic measurement is disabled because the archived package graph uses retired GitHub feeds; current local validation uses the candidate feeds.`;
function waiting(id, category, unit, details, options = {}) { return blocked(id, category, unit, 'dynamic-run-not-requested', { ...options, clock: CLOCK, details }); }
function dynamicPlaceholders() {
  return [
    waiting('typescript-consumer', 'typescript', 'exit-code', { command: ['npm', 'run', 'typecheck'] }, { argv: ['npm', 'run', 'typecheck'], cwd: 'examples-archive' }),
    waiting('clean-frontend-build', 'build', 'nanoseconds', { preparation: ['npm', 'ci'] }, { argv: ['npm', 'run', 'build', '--workspace', '@runic-artifex/sveltekit-setup-application'], cwd: 'examples-archive', warmups: 1, samples: 5 }),
    waiting('change-to-visible-reload', 'reload', 'nanoseconds', { signal: 'unique-file-write-to-exact-rendered-token', renderedVisibility: 'element+computed-style+nonzero-rect', port: SERVER_PORT }, { argv: ['browser-dom-probe', 'reload', '--server-port=5173', '--cdp-port=9222', '--strictPort', '--isolated-profile'], cwd: 'examples-archive', warmups: 3, samples: 20 }),
    waiting('launch-to-visible-startup', 'startup', 'nanoseconds', { signal: 'process-launch-to-rendered-visible-DOM', renderedVisibility: 'element+computed-style+nonzero-rect', port: SERVER_PORT }, { argv: ['browser-dom-probe', 'startup', '--server-port=5173', '--cdp-port=9222', '--strictPort', '--isolated-profile'], cwd: 'examples-archive', warmups: 3, samples: 10 }),
    waiting('package-only-canaries', 'canary', 'canaries', { expectedCanaries: 4, expectedPackageReferences: 14, expectedProjectReferences: 0 }, { argv: ['dotnet', 'restore/build/run'], cwd: 'examples-archive' }),
    waiting('nativeaot-linux-x64', 'nativeaot', 'bytes', { rid: 'linux-x64' }, { argv: ['dotnet', 'publish'], cwd: 'examples-archive' })
  ];
}
function authReason(result, family) { return result.reasonCode === 'github-packages-auth-required' ? `${family}-github-auth-required` : result.reasonCode; }
function phase(name, result, argv, cwd) { return { name, status: result.ok ? 'passed' : result.reasonCode === 'github-packages-auth-required' ? 'blocked' : 'failed', reasonCode: result.ok ? null : result.reasonCode, exitCode: result.exitCode ?? null, argv, cwd }; }
function linuxX64Elf(fileOutput, readelfOutput) { return /ELF\s+64-bit.*(?:x86-64|x86_64)/i.test(fileOutput ?? '') && /Class:\s*ELF64/i.test(readelfOutput ?? '') && /Machine:\s*(?:Advanced Micro Devices X86-64|x86-64)/i.test(readelfOutput ?? ''); }
async function runCanaries(root, runner, nugetEnv) {
  const names = ['RunicAssets.Canary', 'RunicCommandLine.Canary', 'RunicFlow.Canary', 'RunicTranslations.Canary'];
  const outcomes = [];
  for (const name of names) {
    const project = `integrations/${name}/${name}.csproj`; const xml = await fs.readFile(path.join(root, project), 'utf8');
    const packages = [...xml.matchAll(/<PackageReference Include="([^"]+)"/g)].map(match => match[1]); const projects = [...xml.matchAll(/<ProjectReference /g)];
    const restoreArgv = ['restore', project, '--no-cache', '--force-evaluate', '--configfile', path.join(harness, 'scratch.NuGet.config')]; const logicalRestore = ['dotnet', 'restore', project, '--no-cache', '--force-evaluate', '--configfile', 'eng/v0.2-baselines/scratch.NuGet.config'];
    const restore = await runner('dotnet', restoreArgv, root, { env: nugetEnv, captureStderr: true, acquisition: 'github-packages', timeoutMs: 600_000 });
    const buildArgv = ['build', project, '--no-restore', '--configuration', 'Release']; const build = restore.ok ? await runner('dotnet', buildArgv, root, { env: nugetEnv, captureStderr: true, timeoutMs: 600_000 }) : { ok: false, reasonCode: restore.reasonCode, exitCode: null };
    const runArgv = ['run', '--project', project, '--no-build', '--configuration', 'Release']; const launched = build.ok ? await runner('dotnet', runArgv, root, { env: nugetEnv, captureStderr: true, timeoutMs: 300_000 }) : { ok: false, reasonCode: build.reasonCode, exitCode: null };
    const status = launched.ok ? 'passed' : launched.reasonCode === 'github-packages-auth-required' ? 'blocked' : 'failed';
    outcomes.push({ name, project, packageReferences: packages, projectReferences: projects.length, status, phases: [phase('restore', restore, logicalRestore, 'examples-archive'), phase('build', build, ['dotnet', ...buildArgv], 'examples-archive'), phase('run', launched, ['dotnet', ...runArgv], 'examples-archive')] });
  }
  const total = outcomes.reduce((sum, item) => sum + item.packageReferences.length, 0); const status = outcomes.every(item => item.status === 'passed') && total === 14 && outcomes.every(item => item.projectReferences === 0) ? 'passed' : outcomes.some(item => item.status === 'blocked') ? 'blocked' : 'failed';
  return metric('package-only-canaries', 'canary', 'canaries', { status, reasonCode: status === 'passed' ? null : status === 'blocked' ? 'nuget-github-auth-required' : 'canary-failed', argv: ['dotnet', 'restore/build/run'], cwd: 'examples-archive', details: { expectedCanaries: 4, expectedPackageReferences: 14, expectedProjectReferences: 0, totalPackageReferences: total, canaries: outcomes } });
}
async function runAot(root, runner, nugetEnv) {
  if (process.platform !== 'linux' || process.arch !== 'x64') return blocked('nativeaot-linux-x64', 'nativeaot', 'bytes', 'linux-x64-required', { argv: ['dotnet', 'publish'], cwd: 'examples-archive', details: { rid: 'linux-x64' } });
  const project = 'samples/04-SvelteKitSetupApplication/SvelteKitSetupApplication.csproj'; const output = path.join(root, '.baseline-nativeaot');
  const restoreArgv = ['restore', project, '--runtime', 'linux-x64', '--no-cache', '--force-evaluate', '--configfile', path.join(harness, 'scratch.NuGet.config'), '-p:PublishAot=true', '-p:PublishTrimmed=true', '-p:TrimMode=full', '-p:IlcTreatWarningsAsErrors=true'];
  const logicalRestore = ['dotnet', 'restore', project, '--runtime', 'linux-x64', '--no-cache', '--force-evaluate', '--configfile', 'eng/v0.2-baselines/scratch.NuGet.config', '-p:PublishAot=true', '-p:PublishTrimmed=true', '-p:TrimMode=full', '-p:IlcTreatWarningsAsErrors=true'];
  const restore = await runner('dotnet', restoreArgv, root, { env: nugetEnv, captureStderr: true, acquisition: 'github-packages', timeoutMs: 900_000 });
  const publishArgv = ['publish', project, '--no-restore', '--runtime', 'linux-x64', '--self-contained', 'true', '--configuration', 'Release', '--output', output, '-p:PublishAot=true', '-p:PublishTrimmed=true', '-p:TrimMode=full', '-p:IlcTreatWarningsAsErrors=true', '-p:RunicToolkitFrontendBuildCommand='];
  const publish = restore.ok ? await runner('dotnet', publishArgv, root, { env: nugetEnv, timeoutMs: 1_800_000 }) : { ok: false, reasonCode: restore.reasonCode, exitCode: null };
  const entry = path.join(output, 'SvelteKitSetupApplication'); const smokeArgv = [entry, '--smoke-test']; const logicalSmoke = ['.baseline-nativeaot/SvelteKitSetupApplication', '--smoke-test']; const smoke = publish.ok ? await runner(entry, ['--smoke-test'], root, { env: nugetEnv, timeoutMs: 300_000 }) : { ok: false, reasonCode: publish.reasonCode, exitCode: null };
  const file = smoke.ok ? await runner('file', ['--brief', entry], root, { captureStdout: true, captureStderr: true, timeoutMs: 30_000 }) : { ok: false, reasonCode: smoke.reasonCode, exitCode: null, stdout: null };
  const readelf = file.ok ? await runner('readelf', ['--file-header', entry], root, { captureStdout: true, captureStderr: true, timeoutMs: 30_000 }) : { ok: false, reasonCode: file.reasonCode, exitCode: null, stdout: null };
  const elf = linuxX64Elf(file.stdout, readelf.stdout); const listing = readelf.ok && elf ? await inventory(output, 'SvelteKitSetupApplication') : null; const status = readelf.ok && elf ? 'passed' : readelf.reasonCode === 'github-packages-auth-required' ? 'blocked' : 'failed';
  const logicalPublish = ['dotnet', ...publishArgv.map(value => value === output ? '.baseline-nativeaot' : value)];
  return metric('nativeaot-linux-x64', 'nativeaot', 'bytes', { status, reasonCode: status === 'passed' ? null : status === 'blocked' ? 'nuget-github-auth-required' : 'nativeaot-failed', argv: logicalPublish, cwd: 'examples-archive', details: { rid: 'linux-x64', inspection: { file: file.stdout ?? '', readelf: readelf.stdout ?? '' }, inventory: listing, phases: [phase('restore', restore, logicalRestore, 'examples-archive'), phase('publish', publish, logicalPublish, 'examples-archive'), phase('smoke', smoke, logicalSmoke, 'examples-archive'), phase('file', file, ['file', '--brief', '.baseline-nativeaot/SvelteKitSetupApplication'], 'examples-archive'), phase('readelf', readelf, ['readelf', '--file-header', '.baseline-nativeaot/SvelteKitSetupApplication'], 'examples-archive')] } });
}
async function runFrontend(root, runner) {
  const typeArgv = ['run', 'typecheck']; const typecheck = await runner('npm', typeArgv, root, { captureStderr: true, timeoutMs: 600_000 });
  const typeMetric = metric('typescript-consumer', 'typescript', 'exit-code', { status: typecheck.ok ? 'passed' : typecheck.reasonCode === 'github-packages-auth-required' ? 'blocked' : 'failed', reasonCode: typecheck.ok ? null : authReason(typecheck, 'npm'), argv: ['npm', ...typeArgv], cwd: 'examples-archive', details: { strictConsumer: true, exitCode: typecheck.exitCode } });
  const observations = []; const buildArgv = ['run', 'build', '--workspace', '@runic-artifex/sveltekit-setup-application'];
  for (let index = 0; index < 6; index += 1) { await resetFrontend(path.join(root, 'samples/04-SvelteKitSetupApplication/Frontend')); const result = await runner('npm', buildArgv, root, { captureStderr: true, timeoutMs: 600_000 }); if (!result.ok) return [typeMetric, metric('clean-frontend-build', 'build', 'nanoseconds', { status: result.reasonCode === 'github-packages-auth-required' ? 'blocked' : 'failed', reasonCode: authReason(result, 'npm'), argv: ['npm', ...buildArgv], cwd: 'examples-archive', clock: CLOCK, warmups: 1, samples: 5, details: { preparation: ['npm', 'ci'], cleanOutputs: ['Frontend/build', 'Frontend/.svelte-kit'] } })]; if (index) observations.push(result.nanoseconds); }
  return [typeMetric, metric('clean-frontend-build', 'build', 'nanoseconds', { argv: ['npm', ...buildArgv], cwd: 'examples-archive', clock: CLOCK, warmups: 1, samples: 5, observations, summary: timingSummary(observations), details: { preparation: ['npm', 'ci'], cleanOutputs: ['Frontend/build', 'Frontend/.svelte-kit'] } })];
}
async function runBrowserMetrics(root, chromePath) {
  const frontend = path.join(root, 'samples/04-SvelteKitSetupApplication/Frontend');
  const common = { chromePath, serverCommand: 'npm', serverArgs: ['run', 'dev:mock', '--workspace', '@runic-artifex/sveltekit-setup-application', '--', '--host', '127.0.0.1', '--port', String(SERVER_PORT), '--strictPort'], cwd: root, url: `http://127.0.0.1:${SERVER_PORT}/` };
  const details = (signal, port = SERVER_PORT) => ({ signal, renderedVisibility: 'element+computed-style+nonzero-rect', port });
  try {
    const page = path.join(frontend, 'src/routes/+page.svelte'); const original = await fs.readFile(page, 'utf8');
    let reload;
    try { reload = await warmReloadSamples({ ...common, warmups: 3, samples: 20, writeToken: token => fs.writeFile(page, original.replace('<main', `<main data-baseline-token="${token}"`)) }); }
    finally { await fs.writeFile(page, original); }
    const reloadMetric = metric('change-to-visible-reload', 'reload', 'nanoseconds', { argv: reload.argv, cwd: 'examples-archive', clock: CLOCK, warmups: 3, samples: 20, observations: reload.observations, summary: timingSummary(reload.observations), details: details('unique-file-write-to-exact-rendered-token') });
    const observations = []; let argv;
    for (let index = 0; index < 13; index += 1) { const sample = await startupSample(common); argv = sample.argv; if (index >= 3) observations.push(sample.nanoseconds); }
    return [reloadMetric, metric('launch-to-visible-startup', 'startup', 'nanoseconds', { argv, cwd: 'examples-archive', clock: CLOCK, warmups: 3, samples: 10, observations, summary: timingSummary(observations), details: details('process-launch-to-rendered-visible-DOM') })];
  } catch (error) {
    if (error instanceof BrowserCleanupError || error?.code === 'browser-cleanup-failed') throw error;
    const diagnostic = String(error.message ?? error).replace(/[\r\n].*/s, '').slice(0, 160);
    return ['change-to-visible-reload', 'launch-to-visible-startup'].map((id, index) => failed(id, index ? 'startup' : 'reload', 'nanoseconds', 'browser-dom-probe-failed', { argv: ['browser-dom-probe', index ? 'startup' : 'reload', '--server-port=5173', '--cdp-port=9222', '--strictPort', '--isolated-profile'], cwd: 'examples-archive', clock: CLOCK, warmups: 3, samples: index ? 10 : 20, details: { ...details(index ? 'process-launch-to-rendered-visible-DOM' : 'unique-file-write-to-exact-rendered-token'), diagnostic } }));
  }
}
export async function dynamicMeasurements(examples, runner, browser = runBrowserMetrics) {
  throw new Error('Dynamic v0.2 measurement is disabled: the archived package graph uses retired GitHub feeds. Use current candidate-feed package canaries for local validation.');
  const npmPrepare = await runner('npm', ['ci'], examples.directory, { captureStderr: true, acquisition: 'github-packages', timeoutMs: 1_200_000 });
  const nugetPackages = path.join(examples.directory, '.baseline-nuget-packages'); const nugetEnv = { NUGET_PACKAGES: nugetPackages, RestoreAdditionalProjectSources: '', RestoreSources: '', NUGET_RESTORE_SOURCES: '' };
  const frontend = npmPrepare.ok ? await runFrontend(examples.directory, runner) : dynamicPlaceholders().filter(item => ['typescript-consumer', 'clean-frontend-build'].includes(item.id)).map(item => ({ ...item, reasonCode: authReason(npmPrepare, 'npm'), status: npmPrepare.reasonCode === 'github-packages-auth-required' ? 'blocked' : 'failed' }));
  const chrome = await findExecutable(process.env.WEBUI_BROWSER_PATH ? [process.env.WEBUI_BROWSER_PATH] : ['chromium', 'chromium-browser', 'google-chrome', 'google-chrome-stable', 'chrome'], examples.directory);
  const browsers = npmPrepare.ok && frontend.every(item => item.status === 'passed') && chrome ? await browser(examples.directory, chrome) : dynamicPlaceholders().filter(item => item.id.includes('visible')).map(item => ({ ...item, status: 'failed', reasonCode: chrome ? 'verified-frontend-build-required' : 'browser-not-available' }));
  // NuGet work shares one scratch cache, so it is intentionally serialized.
  const canary = await runCanaries(examples.directory, runner, nugetEnv);
  const aot = frontend.every(item => item.status === 'passed') && canary.status === 'passed' ? await runAot(examples.directory, runner, nugetEnv) : blocked('nativeaot-linux-x64', 'nativeaot', 'bytes', 'verified-frontend-build-and-canaries-required', { argv: ['dotnet', 'publish'], cwd: 'examples-archive', details: { rid: 'linux-x64' } });
  return [...frontend, ...browsers, canary, aot];
}
export async function measure(options, dependencies = {}) {
  if (options.dynamic) throw new Error('Dynamic v0.2 measurement is disabled: the archived package graph uses retired GitHub feeds. Use current candidate-feed package canaries for local validation.');
  if (!options.editorRoot) throw new Error('--editor-root is required to pin the editor revision');
  if (!options.releaseManifest) throw new Error('--release-manifest is required to bind public package authority');
  const runner = dependencies.runner ?? exec; const archive = dependencies.archive ?? archiveRevision;
  const releaseManifest = await releaseManifestFacts(options.releaseManifest); const examples = await archive(options.examplesRoot, EXAMPLES_REVISION, EXAMPLES_TREE, 'examples', runner); let editor;
  try { editor = await archive(options.editorRoot, EDITOR_REVISION, EDITOR_TREE, 'editor', runner); const staticResults = await staticMeasurements(examples, editor); const dynamic = options.dynamic ? await dynamicMeasurements(examples, runner, dependencies.browser ?? runBrowserMetrics) : dynamicPlaceholders();
    return { schema: SCHEMA_ID, source: { examples: { revision: examples.revision, tree: examples.tree, before: examples.before, after: examples.after }, editor: { revision: editor.revision, tree: editor.tree, before: editor.before, after: editor.after }, releaseManifest: await releaseManifestAfter(options.releaseManifest, releaseManifest) }, environment: await environmentFacts(examples.directory, editor.directory, path.join(harness, 'scratch.NuGet.config')), methodology: { version: 2, clock: CLOCK, volatilePaths: ['measurements.clean-frontend-build.observations', 'measurements.clean-frontend-build.summary', 'measurements.change-to-visible-reload.observations', 'measurements.change-to-visible-reload.summary', 'measurements.launch-to-visible-startup.observations', 'measurements.launch-to-visible-startup.summary'] }, measurements: ordered([...staticResults, ...dynamic]) };
  } finally { await removeArchive(editor); await removeArchive(examples); }
}
async function main() { try { const options = parseArguments(process.argv.slice(2)); if (options.help) return void process.stdout.write(`${helpText}\n`); const receipt = await measure(options); process.stdout.write(`${JSON.stringify(receipt, null, 2)}\n`); if (options.dynamic || options.verify) { const report = verifyReceipt(receipt, await releaseManifestFacts(options.releaseManifest)); if (!report.ok) { process.stderr.write(`${report.errors.join('\n')}\n`); process.exitCode = 1; } } } catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 2; } }
if (import.meta.main) await main();
