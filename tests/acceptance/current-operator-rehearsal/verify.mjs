#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { execFile as execute } from 'node:child_process';
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { promisify } from 'node:util';

const root = resolve(import.meta.dirname, '../..');
const schema = 'runic.local-operator-rehearsal/1';
const repeatSchema = 'runic.local-operator-rehearsal-repeat/1';
const profiles = ['csharp-host', 'local-application-bridge', 'editor-desktop', 'd008-hosted-product'];
const privacy = ['source-content', 'translation-content', 'review-content', 'session-content', 'cookie-content', 'token-content'];
const supportRejections = ['workspace-root', 'relative-path', 'token', 'source-text', 'translation-text', 'review-text'];
const recovery = { modes: ['complete', 'rollback'], blockedMutations: 2, staleSessionReplays: 2, diagnostics: 'sanitized-counts' };
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const fail = (message) => { throw new Error(`local operator rehearsal: ${message}`); };
const zero = (value, keys) => same(value, Object.fromEntries(keys.map((key) => [key, 0])));
const execFile = promisify(execute);

async function receipt(path, name) {
  const bytes = await readFile(resolve(path));
  try { return { sha256: sha256(bytes), value: JSON.parse(bytes) }; }
  catch { fail(`${name} must be valid JSON`); }
}

function repeated(value, journeySchema, name) {
  const repeat = journeySchema.replace(/\/1$/, '-repeat/1');
  if (value?.schema !== repeat || !Array.isArray(value.journeys) || value.journeys.length !== 2 || !same(value.journeys[0], value.journeys[1]) || value.journeys[0]?.schema !== journeySchema) fail(`${name} is not a deterministic ${journeySchema} receipt`);
  return value.journeys[0];
}

function citation(audit, id) {
  const values = audit.evidence?.w70Freeze?.citations?.filter((item) => item.id === id);
  if (values?.length !== 1) fail(`W80 audit must cite exactly one ${id} receipt`);
  return values[0];
}

async function verifiers() {
  const load = (path) => import(pathToFileURL(join(root, path)).href);
  return {
    audit: await load('eng/current-readiness-audit/verify.mjs'),
    documentation: await load('eng/current-documentation-support-parity/verify.mjs'),
    tool: await load('eng/current-unsigned-tool-staging/verify.mjs'),
    manual: await load('eng/current-manual-replacement-preflight/verify.mjs'),
    support: await load('eng/current-support-envelope/verify.mjs'),
    recovery: await load('eng/current-recovery-capability/verify.mjs'),
  };
}

function check(report, name) {
  if (!report?.ok) fail(`${name} verifier rejected its receipt: ${(report.errors ?? []).join('; ')}`);
}

function link(audit, input, id, name) {
  const expected = citation(audit, id);
  if (input.sha256 !== expected.sha256 || input.value.schema !== expected.schema) fail(`${name} does not match the frozen ${id} citation`);
}

async function directToolReplay(paths, tool) {
  const staging = resolve(paths['tool-staging']);
  const toolkit = resolve(paths['toolkit-repository']);
  const record = tool.toolStaging;
  const directory = await mkdtemp(join(tmpdir(), 'runic-w80-direct-tool-'));
  const source = join(directory, 'source');
  try {
    const archive = join(staging, record.package.archive);
    const stagedRecord = JSON.parse(await readFile(join(staging, 'dotnet-runic-unsigned-staging.json')));
    if (!same(stagedRecord, record) || sha256(await readFile(archive)) !== record.package.sha256) fail('staged direct tool archive does not match its record');
    await execFile('git', ['-C', toolkit, 'worktree', 'add', '--detach', source, record.source.revision]);
    const [status, revision, tree, script, project, sdks] = await Promise.all([
      execFile('git', ['status', '--porcelain'], { cwd: source }),
      execFile('git', ['rev-parse', 'HEAD'], { cwd: source }),
      execFile('git', ['rev-parse', 'HEAD^{tree}'], { cwd: source }),
      execFile('git', ['show', `${record.source.revision}:${record.producer.script}`], { cwd: source }),
      execFile('git', ['show', `${record.source.revision}:${record.producer.project}`], { cwd: source }),
      execFile('dotnet', ['--list-sdks']),
    ]);
    const sdk = sdks.stdout.split('\n').map((line) => line.trim()).find((line) => line.startsWith('10.0.302 '));
    if (status.stdout !== '' || revision.stdout.trim() !== record.source.revision || tree.stdout.trim() !== record.source.tree || sha256(script.stdout) !== record.producer.scriptSha256 || project.stdout.includes('ProjectReference') || !sdk) fail('direct tool source, raw producer script, or Nix SDK does not match the staged record');
    const feed = join(directory, 'feed'), installed = join(directory, 'tool'), config = join(directory, 'NuGet.config');
    await mkdir(feed); await cp(archive, join(feed, record.package.archive));
    await writeFile(config, `<configuration><packageSources><clear/><add key="candidate" value="${feed}"/></packageSources><packageSourceMapping><packageSource key="candidate"><package pattern="dotnet-runic"/></packageSource></packageSourceMapping></configuration>`);
    const environment = { ...process.env, DOTNET_CLI_HOME: join(directory, '.dotnet'), NUGET_PACKAGES: join(directory, '.nuget', 'packages'), NUGET_HTTP_CACHE_PATH: join(directory, '.nuget', 'http'), DOTNET_CLI_TELEMETRY_OPTOUT: '1' };
    await execFile('dotnet', ['tool', 'install', 'dotnet-runic', '--tool-path', installed, '--version', record.package.metadata.version, '--configfile', config, '--ignore-failed-sources'], { cwd: directory, env: environment });
    const command = await execFile(join(installed, 'dotnet-runic'), ['--version'], { cwd: directory, env: environment });
    if (command.stdout.trim() !== 'dotnet-runic') fail('installed direct tool identity drifted');
    return { source: record.source, package: { archive: record.package.archive, sha256: record.package.sha256 }, sdk: sdk.split(' ')[0], command: 'dotnet-runic', isolation: { packageSources: 'exact-local-only', dotnetCliHome: '.dotnet', nugetPackages: '.nuget/packages', nugetHttpCache: '.nuget/http' } };
  } catch (error) {
    fail(`direct tool replay failed: ${String(error.stderr ?? error.message).slice(-512)}`);
  } finally {
    await execFile('git', ['-C', toolkit, 'worktree', 'remove', '--force', source]).catch(() => undefined);
    await rm(directory, { recursive: true, force: true });
  }
}

async function replayWorkflow(path, environment, frozen, name) {
  try {
    const result = await execFile(process.execPath, [join(root, path), 'run-twice'], { cwd: root, env: { ...process.env, ...environment, DOTNET_CLI_TELEMETRY_OPTOUT: '1' } });
    const bytes = Buffer.from(result.stdout);
    if (sha256(bytes) !== frozen.sha256 || !same(JSON.parse(bytes), frozen.value)) fail(`${name} replay differs from its frozen receipt`);
    return { matchedFrozenReceipt: true, sdk: '10.0.302' };
  } catch (error) {
    fail(`${name} replay failed: ${String(error.stderr ?? error.message).slice(-512)}`);
  }
}

async function collect(paths) {
  const [auditInput, documentationInput, toolInput, manualInput, supportInput, recoveryInput] = await Promise.all([
    receipt(paths.audit, 'W80 audit'), receipt(paths.documentation, 'W80 documentation'), receipt(paths.tool, 'W60 direct tool'),
    receipt(paths['manual-replacement'], 'W60 manual replacement'), receipt(paths.support, 'W50 support'), receipt(paths.recovery, 'W50 recovery'),
  ]);
  const audit = repeated(auditInput.value, 'runic.local-nonpublic-readiness-audit/1', 'W80 audit');
  const documentation = repeated(documentationInput.value, 'runic.local-documentation-support-parity/1', 'W80 documentation');
  const tool = repeated(toolInput.value, 'runic.unsigned-tool-staging-consumer/1', 'W60 direct tool');
  const manual = repeated(manualInput.value, 'runic.manual-replacement-preflight-consumer/1', 'W60 manual replacement');
  const support = repeated(supportInput.value, 'runic.support-envelope-consumer/1', 'W50 support');
  const recovered = repeated(recoveryInput.value, 'runic.recovery-capability-consumer/1', 'W50 recovery');
  const checks = await verifiers();
  check(checks.audit.verifyReceipt(auditInput.value, audit), 'W80 audit');
  check(checks.documentation.verifyReceipt(documentationInput.value, documentation), 'W80 documentation');
  check(checks.tool.verifyReceipt(toolInput.value), 'W60 direct tool');
  check(checks.manual.verifyReceipt(manualInput.value), 'W60 manual replacement');
  check(checks.support.verifyReceipt(supportInput.value), 'W50 support');
  check(checks.recovery.verifyReceipt(recoveryInput.value), 'W50 recovery');
  if (audit.publication !== 'forbidden' || !same(Object.keys(audit.retainedProfiles ?? {}).sort(), [...profiles].sort()) || !same(audit.isolation, { inputs: 'isolated-copy', transport: 'none' })) fail('W80 audit does not retain the closed local train');
  link(audit, toolInput, 'w60-tool', 'direct tool');
  link(audit, manualInput, 'w60-preflight', 'manual replacement');
  link(audit, supportInput, 'w50-support', 'support envelope');
  link(audit, recoveryInput, 'w50-recovery', 'recovery');
  if (documentation.evidence?.audit?.sha256 !== auditInput.sha256 || documentation.evidence?.manualReplacement?.sha256 !== manualInput.sha256 || !privacy.every((item) => documentation.nonClaims?.includes(item))) fail('documentation parity is not linked to the exact local operator evidence');
  if (!same(tool.isolation, { dotnetCliHome: '.dotnet', nugetPackages: '.nuget/packages', nugetHttpCache: '.nuget/http' }) || !same(tool.projectReferences, []) || !same(tool.remoteSources, []) || tool.supportEnvelopeContent !== 'forbidden' || tool.canonicalReleaseApproval !== 'seven-package-release-gate-required' || tool.command?.output !== 'dotnet-runic' || tool.toolStaging?.publication !== 'forbidden' || tool.toolStaging?.producer?.operation !== 'direct-dotnet-pack' || tool.toolStaging?.producer?.fullPackInvoked !== false || !same(tool.toolStaging?.producer?.sourceProjectReferences, []) || !same(tool.toolStaging?.prerequisiteFeed?.remoteSources, []) || tool.toolStaging?.package?.metadata?.toolCommandName !== 'dotnet-runic') fail('direct dotnet-runic migration path is not isolated and package-only');
  const directReplay = await directToolReplay(paths, tool);
  if (manual.result !== 'manual-replacement-eligible' || manual.guidance !== 'user-performed-verified-manual-replacement' || manual.networkAttempts !== 0 || manual.processMutations !== 0 || !same(manual.hostileDiagnostics, ['RID-MISMATCH', 'ARCHIVE-RECEIPT-MISMATCH', 'CANDIDATE-MODEL-STALE', 'CANDIDATE-PLATFORM-MISMATCH'])) fail('manual replacement is not the sole bounded migration outcome');
  if (!support.isolatedCaches || !support.noProductProjectReference || !support.previewListsSelectionAndOmissions || !support.collectByteIdentical || !support.removed || !support.hostileRejected || !same(support.hostileRejections, supportRejections) || support.outboundTransportAttempts !== 0) fail('support preview, collection, removal, or privacy evidence was softened');
  if (!same(recovered.isolation, { dotnetCliHome: '.dotnet', nugetPackages: '.nuget/packages', nugetHttpCache: '.nuget/http' }) || !same(recovered.projectReferences, []) || !same(recovered.recovery, recovery) || recovered.diagnostics?.schema !== 'runic.translations.editor-diagnostics/1' || recovered.diagnostics?.outboundTransportAttempts !== 0) fail('recovery does not reject stale bridge state or mutation before recovery');
  const [supportReplay, recoveryReplay] = await Promise.all([
    replayWorkflow('eng/current-support-envelope/verify.mjs', { RUNIC_W50_TOOL_PACKAGE: resolve(paths['support-tool-package']), RUNIC_W50_EDITOR_DIRECTORY: resolve(paths['support-editor-directory']) }, supportInput, 'support envelope'),
    replayWorkflow('eng/current-recovery-capability/verify.mjs', { RUNIC_W50_RECOVERY_NUGET_FEED: resolve(paths['recovery-feed']), RUNIC_W50_RECOVERY_APPLICATION_VERSION: recovered.packages[0].version, RUNIC_W50_RECOVERY_EDITOR_DIRECTORY: resolve(paths['recovery-editor-directory']) }, recoveryInput, 'recovery'),
  ]);
  return {
    schema,
    publication: 'forbidden',
    isolation: { evidenceInputs: 'isolated-copy', packageCaches: 'fresh-per-workflow', transport: 'none' },
    retainedProfiles: audit.retainedProfiles,
    evidence: {
      audit: { sha256: auditInput.sha256, schema: auditInput.value.schema },
      documentation: { sha256: documentationInput.sha256, schema: documentationInput.value.schema },
      directTool: { sha256: toolInput.sha256, schema: toolInput.value.schema, package: tool.toolStaging.package, source: tool.toolStaging.source, replay: directReplay },
      manualReplacement: { sha256: manualInput.sha256, schema: manualInput.value.schema, candidateReceiptSha256: manual.provenance.candidateReceipt.sha256 },
      support: { sha256: supportInput.sha256, schema: supportInput.value.schema, artifactRole: 'support-envelope-only', tool: support.tool, editor: support.editor, replay: supportReplay },
      recovery: { sha256: recoveryInput.sha256, schema: recoveryInput.value.schema, packages: recovered.packages, editor: recovered.editor, replay: recoveryReplay },
    },
    migration: { outcome: 'manual-replacement-eligible', guidance: 'user-performed-verified-manual-replacement', directToolCommand: 'dotnet-runic' },
    support: { preview: 'lists-selection-and-omissions', collect: 'byte-identical', remove: 'verified', hostileRejections: supportRejections },
    recovery: { completeRollback: true, mutationBeforeRecovery: 'rejected', staleBridgeState: 'rejected', diagnostics: 'sanitized-counts' },
    privacy: { optIn: true, excluded: privacy, outboundTransportAttempts: 0, upload: 'forbidden', telemetry: 'forbidden' },
    externalActions: { requests: 0, updates: 0, installs: 0, deletes: 0, rollbacks: 0, repairs: 0, uploads: 0, signatures: 0, tags: 0, releases: 0 },
  };
}

export function verifyReceipt(value, expected) {
  const errors = [];
  if (value?.schema !== repeatSchema || !Array.isArray(value.journeys) || value.journeys.length !== 2 || !same(value.journeys?.[0], value.journeys?.[1])) errors.push('two deterministic operator journeys are required');
  if (expected && !same(value?.journeys?.[0], expected)) errors.push('receipt differs from exact local inputs');
  for (const journey of value?.journeys ?? []) {
    if (journey?.schema !== schema || journey.publication !== 'forbidden' || !same(Object.keys(journey.retainedProfiles ?? {}).sort(), [...profiles].sort()) || !same(journey.isolation, { evidenceInputs: 'isolated-copy', packageCaches: 'fresh-per-workflow', transport: 'none' }) || journey.migration?.outcome !== 'manual-replacement-eligible' || journey.migration?.guidance !== 'user-performed-verified-manual-replacement' || journey.migration?.directToolCommand !== 'dotnet-runic' || !same(journey.support, { preview: 'lists-selection-and-omissions', collect: 'byte-identical', remove: 'verified', hostileRejections: supportRejections }) || !same(journey.recovery, { completeRollback: true, mutationBeforeRecovery: 'rejected', staleBridgeState: 'rejected', diagnostics: 'sanitized-counts' }) || !same(journey.privacy, { optIn: true, excluded: privacy, outboundTransportAttempts: 0, upload: 'forbidden', telemetry: 'forbidden' }) || !zero(journey.externalActions, ['requests', 'updates', 'installs', 'deletes', 'rollbacks', 'repairs', 'uploads', 'signatures', 'tags', 'releases'])) errors.push('operator boundary mismatch');
    if (!/^[a-f0-9]{64}$/.test(journey.evidence?.audit?.sha256 ?? '') || !/^[a-f0-9]{64}$/.test(journey.evidence?.documentation?.sha256 ?? '') || journey.evidence?.directTool?.package?.metadata?.toolCommandName !== 'dotnet-runic' || !same(journey.evidence?.directTool?.package?.metadata?.dependencies, []) || journey.evidence?.directTool?.replay?.sdk !== '10.0.302' || journey.evidence?.directTool?.replay?.command !== 'dotnet-runic' || !same(journey.evidence?.directTool?.replay?.isolation, { packageSources: 'exact-local-only', dotnetCliHome: '.dotnet', nugetPackages: '.nuget/packages', nugetHttpCache: '.nuget/http' }) || journey.evidence?.support?.artifactRole !== 'support-envelope-only' || !same(journey.evidence?.support?.replay, { matchedFrozenReceipt: true, sdk: '10.0.302' }) || !same(journey.evidence?.recovery?.replay, { matchedFrozenReceipt: true, sdk: '10.0.302' }) || !same(journey.evidence?.recovery?.packages?.map((item) => item.identity), ['Runic.Application', 'Runic.Application.Testing', 'Runic.Assets']) || !privacy.every((item) => journey.privacy?.excluded?.includes(item))) errors.push('operator provenance or privacy evidence mismatch');
  }
  return { ok: errors.length === 0, errors };
}

async function isolated(paths) {
  const directory = await mkdtemp(join(tmpdir(), 'runic-w80-operator-rehearsal-'));
  try {
    const copied = {
      'tool-staging': paths['tool-staging'], 'toolkit-repository': paths['toolkit-repository'],
      'support-tool-package': paths['support-tool-package'], 'support-editor-directory': paths['support-editor-directory'],
      'recovery-feed': paths['recovery-feed'], 'recovery-editor-directory': paths['recovery-editor-directory'],
    };
    for (const name of ['audit', 'documentation', 'tool', 'manual-replacement', 'support', 'recovery']) { copied[name] = join(directory, `${name}.json`); await cp(resolve(paths[name]), copied[name]); }
    return await collect(copied);
  } finally { await rm(directory, { recursive: true, force: true }); }
}

export async function runTwice(paths) {
  const value = { schema: repeatSchema, journeys: [await isolated(paths), await isolated(paths)] };
  const report = verifyReceipt(value);
  if (!report.ok) fail(report.errors.join('; '));
  return value;
}

function args(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 2) { if (!argv[index]?.startsWith('--') || !argv[index + 1] || values[argv[index]]) fail('usage'); values[argv[index].slice(2)] = argv[index + 1]; }
  const names = ['audit', 'documentation', 'tool', 'manual-replacement', 'support', 'recovery', 'tool-staging', 'toolkit-repository', 'support-tool-package', 'support-editor-directory', 'recovery-feed', 'recovery-editor-directory'];
  if (!same(Object.keys(values).filter((name) => name !== 'receipt').sort(), names.sort())) fail('all local operator evidence inputs are required');
  return values;
}

async function main(argv) {
  const [command, ...rest] = argv;
  const options = args(rest);
  if (command === 'run-twice' && !options.receipt) return JSON.stringify(await runTwice(options), null, 2);
  if (command === 'verify-twice' && options.receipt) {
    const actual = JSON.parse(await readFile(options.receipt));
    const expected = await runTwice(options);
    const report = verifyReceipt(actual, expected.journeys[0]);
    if (!report.ok || !same(actual, expected)) fail(report.errors.concat('receipt differs from exact local inputs').join('; '));
    return undefined;
  }
  fail('usage');
}

if (import.meta.main) main(process.argv.slice(2)).then((output) => { if (output) process.stdout.write(`${output}\n`); }).catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
