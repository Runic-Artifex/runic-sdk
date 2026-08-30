#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { cp, mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = resolve(import.meta.dirname, '../..');
const schema = 'runic.support-certification/1';
const repeatSchema = 'runic.support-certification-repeat/1';
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const sha256 = (value) => createHash('sha256').update(value).digest('hex');
const privacyExcluded = ['source', 'translation', 'review', 'session', 'cookie', 'token'];
const fail = (message) => { throw new Error(`support certification: ${message}`); };

async function receipt(path, name) {
  const bytes = await readFile(resolve(path));
  try { return { sha256: sha256(bytes), value: JSON.parse(bytes) }; } catch { fail(`${name} must be valid JSON`); }
}

function repeated(value, journeySchema, name) {
  const repeat = journeySchema.replace(/\/1$/, '-repeat/1');
  if (value?.schema !== repeat || !Array.isArray(value.journeys) || value.journeys.length !== 2 || !same(value.journeys[0], value.journeys[1]) || value.journeys[0]?.schema !== journeySchema) fail(`${name} is not a deterministic ${journeySchema} receipt`);
  return value.journeys[0];
}

function citation(freeze, id) {
  const values = freeze.citations?.filter((item) => item.id === id);
  if (!values || values.length !== 1) fail(`frozen profile lacks exactly one ${id} citation`);
  return values[0];
}

function bind(freeze, id, supplied) {
  const item = citation(freeze, id);
  if (item.sha256 !== supplied.sha256 || item.schema !== supplied.value.schema) fail(`${id} receipt does not match the frozen citation`);
}

async function verifiers() {
  return {
    support: await import(pathToFileURL(join(root, 'eng/current-support-envelope/verify.mjs')).href),
    recovery: await import(pathToFileURL(join(root, 'eng/current-recovery-capability/verify.mjs')).href),
    quality: await import(pathToFileURL(join(root, 'eng/current-editor-quality/verify.mjs')).href),
  };
}

function check(report, name) { if (!report?.ok) fail(`${name} verifier rejected the supplied receipt: ${(report?.errors ?? []).join('; ')}`); }

async function collect(paths) {
  const [profile, freezeReceipt, support, recovery, quality] = await Promise.all([
    receipt(paths.profile, 'profile'), receipt(paths.freeze, 'freeze'), receipt(paths.support, 'support'), receipt(paths.recovery, 'recovery'), receipt(paths.quality, 'quality'),
  ]);
  const input = profile.value;
  if (input?.schema !== 'runic.controlled-nonpublic-profile-input/1' || input.publication !== 'forbidden') fail('profile is not the W70-001 publication-forbidden input');
  const frozen = repeated(freezeReceipt.value, 'runic.controlled-nonpublic-profile-freeze/1', 'freeze');
  if (!same(frozen.profiles, input.profiles) || frozen.publication !== 'forbidden' || !same(frozen.externalActions, { requests: 0, signatures: 0, metadata: 0, releases: 0, uploads: 0, tags: 0 })) fail('freeze receipt does not bind the supplied closed profile');
  bind(frozen, 'w50-support', support); bind(frozen, 'w50-recovery', recovery); bind(frozen, 'w50-quality', quality);

  const checks = await verifiers();
  const supportJourney = repeated(support.value, 'runic.support-envelope-consumer/1', 'support');
  check(checks.support.verifyReceipt(support.value, { tool: supportJourney.tool, editor: supportJourney.editor }), 'support');
  const recoveryJourney = repeated(recovery.value, 'runic.recovery-capability-consumer/1', 'recovery');
  check(checks.recovery.verifyReceipt(recovery.value, { packages: recoveryJourney.packages, editor: recoveryJourney.editor }), 'recovery');
  const qualityJourney = repeated(quality.value, 'runic.editor-structural-quality/1', 'quality');
  check(checks.quality.verifyReceipt(quality.value, qualityJourney.localProfiles), 'quality');

  const bridge = input.profiles?.['local-application-bridge']?.source;
  if (!bridge || qualityJourney.localProfiles?.toolkit?.revision !== bridge.revision || qualityJourney.localProfiles?.toolkit?.tree !== bridge.tree) fail('structural Bridge evidence does not match the frozen local profile');
  if (!supportJourney.isolatedCaches || !supportJourney.noProductProjectReference || !supportJourney.previewListsSelectionAndOmissions || !supportJourney.collectByteIdentical || !supportJourney.removed || !supportJourney.hostileRejected || !same(supportJourney.hostileRejections, ['workspace-root', 'relative-path', 'token', 'source-text', 'translation-text', 'review-text']) || supportJourney.outboundTransportAttempts !== 0) fail('support envelope privacy or removal evidence was softened');
  if (!same(recoveryJourney.projectReferences, []) || !same(recoveryJourney.recovery, { modes: ['complete', 'rollback'], blockedMutations: 2, staleSessionReplays: 2, diagnostics: 'sanitized-counts' }) || recoveryJourney.diagnostics?.schema !== 'runic.translations.editor-diagnostics/1' || recoveryJourney.diagnostics?.outboundTransportAttempts !== 0) fail('recovery precedence, stale bridge, or diagnostics evidence was softened');
  if (!same(qualityJourney.model, { messages: 50_000, reviewLocales: 100, retainedHeapMiBMaximum: 256, timing: 'observation-only' }) || !same(qualityJourney.keyboardAccessibility, { commandSearch: 'focused', recoveryFocusOrder: ['rollback', 'complete'], labelsAndLandmarks: 'checked', forcedColors: 'checked' })) fail('quality bounds, keyboard evidence, or non-SLA timing claim was softened');

  return {
    schema,
    isolation: { inputs: 'isolated-copy', diagnosticsAuthority: 'runic.translations.editor-diagnostics/1' },
    freezeReceipt: { sha256: freezeReceipt.sha256, schema: freezeReceipt.value.schema },
    frozenProfile: { sha256: profile.sha256, profiles: input.profiles },
    evidence: { support: { sha256: support.sha256, schema: support.value.schema }, recovery: { sha256: recovery.sha256, schema: recovery.value.schema }, quality: { sha256: quality.sha256, schema: quality.value.schema } },
    privacy: { optIn: true, excluded: privacyExcluded, outboundTransportAttempts: 0, upload: 'forbidden', telemetry: 'forbidden' },
    recovery: { completeRollback: true, mutationBeforeRecovery: 'rejected', staleBridgeState: 'rejected', diagnostics: 'sanitized-counts' },
    quality: { bridge: qualityJourney.bridge, model: qualityJourney.model, keyboardAccessibility: qualityJourney.keyboardAccessibility, timing: 'observation-only-not-sla' },
    externalActions: { requests: 0, uploads: 0, telemetry: 0, dashboard: 0 },
  };
}

export function verifyReceipt(value, expected) {
  const errors = [];
  if (value?.schema !== repeatSchema || !Array.isArray(value.journeys) || value.journeys.length !== 2 || !same(value.journeys?.[0], value.journeys?.[1])) errors.push('two deterministic support-certification journeys are required');
  if (expected && !same(value?.journeys?.[0], expected)) errors.push('receipt differs from exact frozen inputs');
  for (const journey of value?.journeys ?? []) {
    if (journey?.schema !== schema || !/^[a-f0-9]{64}$/.test(journey?.freezeReceipt?.sha256 ?? '') || journey.freezeReceipt?.schema !== 'runic.controlled-nonpublic-profile-freeze-repeat/1' || !same(journey?.isolation, { inputs: 'isolated-copy', diagnosticsAuthority: 'runic.translations.editor-diagnostics/1' }) || !same(journey?.privacy, { optIn: true, excluded: privacyExcluded, outboundTransportAttempts: 0, upload: 'forbidden', telemetry: 'forbidden' }) || !same(journey?.recovery, { completeRollback: true, mutationBeforeRecovery: 'rejected', staleBridgeState: 'rejected', diagnostics: 'sanitized-counts' }) || !same(journey?.quality, { bridge: { returnedFrames: 'exact', schemaValidatedDelivery: 'exact', fixedBatches: [1, 256, 1024] }, model: { messages: 50_000, reviewLocales: 100, retainedHeapMiBMaximum: 256, timing: 'observation-only' }, keyboardAccessibility: { commandSearch: 'focused', recoveryFocusOrder: ['rollback', 'complete'], labelsAndLandmarks: 'checked', forcedColors: 'checked' }, timing: 'observation-only-not-sla' }) || !same(journey?.externalActions, { requests: 0, uploads: 0, telemetry: 0, dashboard: 0 })) errors.push('support-certification boundary mismatch');
  }
  return { ok: errors.length === 0, errors };
}

async function isolated(paths) {
  const directory = await mkdtemp(join(tmpdir(), 'runic-w70-support-certification-'));
  try {
    const copied = {};
    for (const [name, path] of Object.entries(paths)) { copied[name] = join(directory, `${name}.json`); await cp(resolve(path), copied[name]); }
    return await collect(copied);
  } finally { await rm(directory, { recursive: true, force: true }); }
}

export async function runTwice(paths) {
  const value = { schema: repeatSchema, journeys: [await isolated(paths), await isolated(paths)] };
  const report = verifyReceipt(value);
  if (!report.ok) fail(report.errors.join('; '));
  return value;
}

function args(values) {
  const result = {};
  for (let index = 0; index < values.length; index += 2) { if (!values[index]?.startsWith('--') || !values[index + 1] || result[values[index]]) fail('usage'); result[values[index].slice(2)] = values[index + 1]; }
  const names = ['profile', 'freeze', 'support', 'recovery', 'quality'];
  if (!same(Object.keys(result).filter((name) => name !== 'receipt').sort(), names.sort())) fail('all frozen support inputs are required');
  return result;
}

async function main(argv) {
  const [command, ...rest] = argv, options = args(rest);
  if (command === 'run-twice' && !options.receipt) return JSON.stringify(await runTwice(options), null, 2);
  if (command === 'verify-twice' && options.receipt) { const expected = await runTwice(options), actual = JSON.parse(await readFile(options.receipt)); const report = verifyReceipt(actual, expected.journeys[0]); if (!report.ok || !same(actual, expected)) fail(report.errors.concat('receipt differs from exact isolated inputs').join('; ')); return undefined; }
  fail('usage');
}

if (import.meta.main) main(process.argv.slice(2)).then((output) => { if (output) process.stdout.write(`${output}\n`); }).catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
