// The cross-repository compatibility gate of a release train (eng/train/README.md).
//
//   bun eng/train/gate.mjs --cli <input> --translations <input> --sdk <input> [--row <name>] [--out <dir>]
//                          [--only S1,S2] [--keep]
//
// Each input is `published`, `published:<version>`, `run`, `run:<ci run id>` or, locally, `local:<dir>`. CI artifacts are read with
// GH_TOKEN (GITHUB_TOKEN in Actions). Writes <out>/train-result.json, <out>/summary.md and the logs; exits 1 when a
// required scenario has a failure that expected-failures.json does not name, and 3 when an artifact was refused.
import { spawn, spawnSync } from 'node:child_process';
import { appendFileSync, cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { activeEntries, classify, loadExpected, matchExpected } from './expected.mjs';
import { repoKeys, repos, writeNugetConfig } from './nuget.mjs';
import { createRunner, scenarios } from './scenarios.mjs';
import { packageFile, resolveSources } from './sources.mjs';

const root = resolve(import.meta.dirname, '../..');
export const RESULT_SCHEMA = 1;

function options() {
  const { values } = parseArgs({ options: {
    cli: { type: 'string', default: 'published' }, translations: { type: 'string', default: 'published' }, sdk: { type: 'string', default: 'published' },
    row: { type: 'string', default: 'custom' }, out: { type: 'string', default: join(root, 'artifacts/train-compat') },
    only: { type: 'string' }, keep: { type: 'boolean', default: false }, work: { type: 'string' },
  } });
  return values;
}

async function startNpmRegistry(context, archives) {
  const ready = join(context.work, 'npm-registry.url');
  const server = spawn('bun', [join(root, 'tests/templates/template-npm-registry.mjs'), ready, ...archives], { stdio: 'ignore', env: context.env });
  for (let attempt = 0; attempt < 200 && !(existsSync(ready) && readFileSync(ready, 'utf8').trim()); attempt++) await new Promise(done => setTimeout(done, 50));
  if (!existsSync(ready)) { server.kill(); throw new Error('The candidate npm registry did not start'); }
  return { url: readFileSync(ready, 'utf8').trim(), archives, server };
}

function summary(result) {
  const { stale } = result.expected;
  const icon = { passed: '✅', expected: '🟡', failed: '❌', informational: 'ℹ️', skipped: '⏭️' };
  const lines = [`## Train compatibility: ${result.verdict === 'pass' ? 'pass' : 'FAIL'} (${result.row})`, '',
    '| Repository | Input | Version | Source |', '| --- | --- | --- | --- |'];
  for (const input of Object.values(result.inputs))
    lines.push(`| ${input.repository} | \`${input.input}\` | ${input.version} | ${input.run ? `[run ${input.run.id}](${input.run.url}) (${input.run.headSha.slice(0, 9)})` : input.kind === 'local' ? 'local packages' : 'nuget.org'} |`);
  lines.push('', '| Scenario | Result | Details |', '| --- | --- | --- |');
  for (const scenario of result.scenarios) {
    const details = [...scenario.failures.map(item => `${item.expected ? `expected ${item.expected}: ` : ''}${item.check}: ${item.message}`), ...scenario.notes]
      .map(text => text.replace(/\|/g, '\\|')).join('<br>');
    lines.push(`| ${scenario.id} ${scenario.title} | ${icon[scenario.status]} ${scenario.status} | ${details} |`);
  }
  if (result.expected.applied.length || result.expected.stale.length) {
    lines.push('', '### Expected failures', '');
    for (const entry of result.expected.entries)
      lines.push(`- **${entry.id}** ${entry.title}${entry.issue ? ` (${entry.issue})` : ''}: ${entry.matched ? 'reproduced' : stale.includes(entry.id) ? '**no longer reproduces; remove the entry**' : 'not checked in this run'}. Remove: ${entry.remove}`);
  }
  return `${lines.join('\n')}\n`;
}

export async function runGate(values) {
  const out = resolve(values.out);
  mkdirSync(out, { recursive: true });
  const work = values.work ? resolve(values.work) : mkdtempSync(join(process.env.RUNNER_TEMP ?? tmpdir(), 'runic-train-compat-'));
  const logs = join(work, 'logs');
  mkdirSync(logs, { recursive: true });
  const specs = Object.fromEntries(repoKeys.map(key => [key, values[key]]));
  const token = process.env.GH_TOKEN ?? process.env.GITHUB_TOKEN;
  const result = { schema: RESULT_SCHEMA, row: values.row, createdAt: new Date().toISOString(),
    gate: { repository: process.env.GITHUB_REPOSITORY ?? null, sha: spawnSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).stdout.trim() || null,
      run: process.env.GITHUB_RUN_ID ? { id: Number(process.env.GITHUB_RUN_ID), attempt: Number(process.env.GITHUB_RUN_ATTEMPT ?? 1) } : null },
    inputs: {}, scenarios: [], expected: { applied: [], stale: [], entries: [] }, artifactAccess: null, verdict: 'fail' };
  const finish = code => {
    writeFileSync(join(out, 'train-result.json'), `${JSON.stringify(result, null, 2)}\n`);
    const text = summary(result);
    writeFileSync(join(out, 'summary.md'), text);
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, text);
    if (existsSync(logs)) cpSync(logs, join(out, 'logs'), { recursive: true });
    if (!values.keep) rmSync(work, { recursive: true, force: true, maxRetries: 3 });
    else console.log(`Kept ${work}`);
    return code;
  };

  let sources;
  try {
    sources = await resolveSources(specs, join(work, 'candidates'), { token });
  } catch (error) {
    if (error.artifactAccess) {
      result.artifactAccess = { ...error.artifactAccess, message: error.message };
      console.log(`::error title=Cross-repository artifact refused::${error.message}. D061.12: the maintainer provides a read-only token or GitHub App (RUNIC_TRAIN_TOKEN).`);
      return finish(3);
    }
    throw error;
  }
  const crossRepository = Object.values(sources).filter(source => source.run && source.repository !== process.env.GITHUB_REPOSITORY);
  if (crossRepository.length) result.artifactAccess = { status: 200, repositories: crossRepository.map(source => source.repository), token: process.env.RUNIC_TRAIN_TOKEN_SOURCE ?? null };
  for (const [key, source] of Object.entries(sources))
    result.inputs[key] = { repository: source.repository, input: source.input, kind: source.kind, version: source.version, run: source.run,
      packages: source.packages.map(({ id, version, source: from, sha256 }) => ({ id, version, source: from, sha256 })),
      npm: source.npm.map(({ name, version, sha256 }) => ({ name, version, sha256 })) };
  console.log(Object.values(sources).map(source => `${repos[source.repo].title}: ${source.input} -> ${source.version}${source.run ? ` (run ${source.run.id})` : ''}`).join('\n'));

  // Candidates carry final release versions, so they must never reach a shared NuGet, npm or tool cache.
  const hasRun = Object.values(sources).some(source => source.kind !== 'published');
  const shared = !hasRun && process.env.RUNIC_TRAIN_NUGET_PACKAGES;
  const env = { ...process.env, DOTNET_CLI_HOME: join(work, 'dotnet-home'), NUGET_PACKAGES: shared || join(work, 'nuget-packages'),
    NUGET_HTTP_CACHE_PATH: join(work, 'nuget-http-cache'), npm_config_cache: join(work, 'npm-cache'), DOTNET_NOLOGO: '1',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1', MSBUILDDISABLENODEREUSE: '1', DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER: '1',
    UseSharedCompilation: 'false' };
  for (const key of ['GH_TOKEN', 'GITHUB_TOKEN', 'RUNIC_TRAIN_TOKEN']) delete env[key];
  const feeds = Object.fromEntries(repoKeys.map(key => [key, sources[key].kind !== 'published' ? join(work, 'candidates', key, 'nuget') : null]));
  const nugetConfig = join(work, 'nuget.config');
  writeNugetConfig(nugetConfig, feeds);
  // Every consumer below the work directory restores through nuget.config and builds with warnings as errors;
  // RunicTrainWaive adds the warnings an expected failure waives (I1's NU1608) to NoWarn.
  writeFileSync(join(work, 'Directory.Build.props'), '<Project>\n  <PropertyGroup>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <NuGetAudit>true</NuGetAudit>\n  </PropertyGroup>\n</Project>\n');
  writeFileSync(join(work, 'Directory.Build.targets'), '<Project>\n  <PropertyGroup Condition="\'$(RunicTrainWaive)\' != \'\'">\n    <NoWarn>$(NoWarn);$(RunicTrainWaive)</NoWarn>\n  </PropertyGroup>\n</Project>\n');

  const versionOf = id => Object.values(sources).flatMap(source => source.packages).find(item => item.id.toLowerCase() === id.toLowerCase())?.version ?? null;
  const entries = loadExpected();
  const active = activeEntries(entries, versionOf);
  const failures = [], notes = new Map();
  const versions = { cli: sources.cli.version, translations: sources.translations.version, sdk: sources.sdk.version };
  const context = {
    root, work, logs, env, sources, versions, nugetConfig, feeds, waived: {},
    fail: (scenario, check, message) => { failures.push({ scenario, check, message }); console.log(`  FAIL ${scenario} ${check}: ${message}`); },
    note: (scenario, message) => { notes.set(scenario, [...(notes.get(scenario) ?? []), message]); console.log(`  note ${scenario}: ${message}`); },
    expectedFor: failure => matchExpected(active, failure),
    packageFile: (repo, id) => packageFile(sources[repo], id, join(work, 'candidates'), {}),
  };
  context.run = createRunner(context);
  if (sources.sdk.kind !== 'published') context.npmRegistry = await startNpmRegistry(context, sources.sdk.npm.map(item => item.file));

  const selected = values.only ? new Set(values.only.split(',').map(item => item.trim())) : null;
  try {
    for (const scenario of scenarios) {
      if (selected && !selected.has(scenario.id)) continue;
      console.log(`\n### ${scenario.id} ${scenario.title}`);
      const started = Date.now();
      try { await scenario.run(context); } catch (error) { context.fail(scenario.id, 'error', error.message); }
      notes.set(`${scenario.id}:seconds`, Math.round((Date.now() - started) / 1000));
    }
  } finally {
    context.npmRegistry?.server.kill();
    spawnSync('dotnet', ['build-server', 'shutdown'], { env, stdio: 'ignore' });
  }

  const classified = classify(failures, active);
  for (const scenario of scenarios) {
    const ran = !selected || selected.has(scenario.id);
    const own = classified.failures.filter(item => item.scenario === scenario.id);
    const status = !ran ? 'skipped' : !own.length ? 'passed' : !scenario.required ? 'informational' : own.every(item => item.expected) ? 'expected' : 'failed';
    result.scenarios.push({ id: scenario.id, title: scenario.title, required: scenario.required, status, seconds: notes.get(`${scenario.id}:seconds`) ?? null,
      failures: own.map(({ check, message, expected }) => ({ check, message, expected })), notes: notes.get(scenario.id) ?? [] });
  }
  const applied = new Set(classified.failures.map(item => item.expected).filter(Boolean));
  // An entry that matches only skipped scenarios is not stale.
  const stale = selected ? [] : classified.stale;
  result.expected = { applied: [...applied], stale, entries: active.map(entry => ({ id: entry.id, title: entry.title, issue: entry.issue ?? null, remove: entry.remove, matched: applied.has(entry.id) })) };
  result.verdict = result.scenarios.some(scenario => scenario.status === 'failed') ? 'fail' : 'pass';
  for (const id of stale) console.log(`::warning title=Expected failure no longer reproduces::${id} did not occur in this run; remove it from eng/train/expected-failures.json.`);
  for (const scenario of result.scenarios.filter(item => item.status === 'failed'))
    for (const failure of scenario.failures.filter(item => !item.expected)) console.log(`::error title=${scenario.id} ${failure.check}::${failure.message}`);
  console.log(`\nVerdict: ${result.verdict}`);
  return finish(result.verdict === 'pass' ? 0 : 1);
}

if (import.meta.main) process.exit(await runGate(options()));
