#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { cp, mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = resolve(import.meta.dirname, '../..');
const schema = 'runic.controlled-clean-room-conformance/1';
const repeatSchema = 'runic.controlled-clean-room-conformance-repeat/1';
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const hash = (value) => createHash('sha256').update(value).digest('hex');
const sourceKeys = ['repository', 'revision', 'tree'];
const rids = ['linux-x64', 'osx-arm64', 'win-x64'];
const fail = (message) => { throw new Error(`controlled clean room: ${message}`); };

async function receipt(path, name) {
  const bytes = await readFile(resolve(path));
  try { return { path: resolve(path), sha256: hash(bytes), value: JSON.parse(bytes) }; } catch { fail(`${name} must be valid JSON`); }
}

function repeated(value, expected, name) {
  const repeat = expected.replace(/\/1$/, '-repeat/1');
  if (value?.schema !== repeat || !Array.isArray(value.journeys) || value.journeys.length !== 2 || !same(value.journeys[0], value.journeys[1]) || value.journeys[0]?.schema !== expected) fail(`${name} must be a deterministic ${expected} receipt`);
  return value.journeys[0];
}

function sourceMatches(actual, expected) {
  return actual && expected && actual.revision === expected.revision && actual.tree === expected.tree && (actual.repository === undefined || actual.repository === expected.repository);
}

function profile(input, name) {
  if (input?.schema !== 'runic.controlled-nonpublic-profile-input/1' || input.publication !== 'forbidden' || !input.profiles?.['csharp-host']?.source || !input.profiles?.['local-application-bridge']?.source || !input.profiles?.['editor-desktop']?.source || !input.profiles?.['d008-hosted-product']?.authority) fail(`${name} is not the W70-001 closed profile input`);
  return input;
}

function citation(freeze, id) {
  const item = freeze.citations?.find((value) => value.id === id);
  if (!item || freeze.citations.filter((value) => value.id === id).length !== 1) fail(`frozen receipt is missing '${id}' citation`);
  return item;
}

function bind(citationValue, supplied, name) {
  if (citationValue.sha256 !== supplied.sha256 || citationValue.schema !== supplied.value.schema) fail(`${name} does not match the frozen citation`);
}

async function verifiers() {
  return {
    tool: await import(pathToFileURL(join(root, 'eng/current-unsigned-tool-staging/verify.mjs')).href),
    portable: await import(pathToFileURL(join(root, 'eng/current-mf2-subset-consumer/verify.mjs')).href),
    hosted: await import(pathToFileURL(join(root, 'eng/current-hosted-product/verify.mjs')).href),
    localization: await import(pathToFileURL(join(root, 'eng/current-localization-compatibility/verify.mjs')).href),
    desktop: await import(pathToFileURL(join(root, '../runic-translations-editor/eng/verify-localized-desktop-product.mjs')).href),
  };
}

function check(report, name) { if (!report?.ok) fail(`${name} failed verification: ${(report?.errors ?? []).join('; ')}`); }

async function collect(paths) {
  const [profileInput, freeze, csharp, bridge, bridgeQuality, editor, localization, portable, hosted, desktop, rollout] = await Promise.all([
    receipt(paths.profile, 'profile'), receipt(paths.freeze, 'freeze'), receipt(paths.csharp, 'C# tool'), receipt(paths.bridge, 'Bridge'), receipt(paths['bridge-quality'], 'Bridge quality'), receipt(paths.editor, 'Editor candidate'), receipt(paths.localization, 'localization'), receipt(paths.portable, 'portable MF2'), receipt(paths.hosted, 'hosted product'), receipt(paths.desktop, 'Editor desktop'), receipt(paths.rollout, 'hosted rollout'),
  ]);
  const input = profile(profileInput.value, 'profile');
  const frozen = repeated(freeze.value, 'runic.controlled-nonpublic-profile-freeze/1', 'freeze');
  if (!same(frozen.profiles, input.profiles) || frozen.publication !== 'forbidden' || !same(frozen.externalActions, { requests: 0, signatures: 0, metadata: 0, releases: 0, uploads: 0, tags: 0 })) fail('freeze receipt differs from the publication-forbidden input');
  bind(citation(frozen, 'w60-tool'), csharp, 'C# tool receipt');
  bind(citation(frozen, 'w50-quality'), bridgeQuality, 'Bridge quality receipt');
  bind(citation(frozen, 'w60-candidate'), editor, 'Editor candidate receipt');
  bind(citation(frozen, 'w40-localization'), localization, 'localization receipt');
  bind(citation(frozen, 'w30-rollout'), rollout, 'hosted rollout receipt');

  const checks = await verifiers();
  const toolJourney = repeated(csharp.value, 'runic.unsigned-tool-staging-consumer/1', 'C# tool');
  check(checks.tool.verifyReceipt(csharp.value, { toolStaging: toolJourney.toolStaging, candidateSet: toolJourney.candidateSet }), 'C# tool receipt');
  if (!sourceMatches(toolJourney.toolStaging?.source, input.profiles['csharp-host'].source) || !same(toolJourney.projectReferences, []) || !same(toolJourney.remoteSources, []) || toolJourney.supportEnvelopeContent !== 'forbidden') fail('C# host/tool migration is not isolated or does not match the frozen host');

  const bridgeJourney = repeated(bridge.value, 'runic.current-host-transport/1', 'Bridge');
  if (!same(bridgeJourney.feeds, { nuget: 'w20-002-local-candidate-feed', npm: 'w20-002-local-candidate-npm-feed' }) || !same(bridgeJourney.isolation, { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet', npmCache: '.npm-cache' }) || !same(bridgeJourney.nugetCandidates?.map((item) => item.identity), ['Runic.Application', 'Runic.Application.Bridge', 'Runic.Application.Hosting']) || bridgeJourney.nugetCandidates?.some((item) => item.source !== bridgeJourney.feeds.nuget || !item.contentHash) || bridgeJourney.npmCandidate?.identity !== '@runic-artifex/application-bridge' || bridgeJourney.npmCandidate?.source !== bridgeJourney.feeds.npm || !/^[a-f0-9]{64}$/.test(bridgeJourney.npmCandidate?.archiveSha256 ?? '') || bridgeJourney.phases?.some((item) => item.status !== 'passed' || item.exitCode !== 0)) fail('Bridge package receipt is not the closed isolated W20 consumer proof');
  const qualityJourney = repeated(bridgeQuality.value, 'runic.editor-structural-quality/1', 'Bridge quality');
  if (!sourceMatches(qualityJourney.localProfiles?.toolkit, input.profiles['local-application-bridge'].source) || !same(qualityJourney.bridge, { returnedFrames: 'exact', schemaValidatedDelivery: 'exact', fixedBatches: [1, 256, 1024] }) || !same(bridgeJourney.isolation, { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', dotnetCliHome: '.dotnet', npmCache: '.npm-cache' })) fail('local Bridge package conformance does not match its frozen local boundary');

  const editorJourney = repeated(editor.value, 'runic.unsigned-candidate-set-consumer/1', 'Editor candidate');
  const editorProfile = input.profiles['editor-desktop'];
  if (!editorJourney.noProductProjectReference || editorJourney.candidateSet?.publication !== 'forbidden' || !sourceMatches(editorJourney.candidateSet?.source, editorProfile.source)) fail('Editor candidate is not an isolated frozen package consumer');
  for (const artifact of editorProfile.artifacts ?? []) {
    const platform = editorJourney.candidateSet?.platforms?.find((value) => value.runtimeIdentifier === artifact.runtimeIdentifier);
    if (!platform || !sourceMatches(platform.source, editorProfile.source) || platform.archive?.sha256 !== artifact.sha256) fail(`Editor ${artifact.runtimeIdentifier} archive differs from the frozen profile`);
  }
  if (!same((editorProfile.artifacts ?? []).map((item) => item.runtimeIdentifier).sort(), [...rids].sort())) fail('Editor frozen platform set is not closed');

  check(checks.localization.verifyReceipt(localization.value), 'localization receipt');
  check(checks.portable.verifyReceipt(portable.value), 'portable MF2 receipt');
  check(checks.hosted.verifyReceipt(hosted.value), 'hosted product receipt');
  check(checks.desktop.verifyReceipt(desktop.value), 'Editor desktop receipt');
  const localizationJourney = repeated(localization.value, 'runic.w40-localization-compatibility/1', 'localization');
  if (localizationJourney.inputs?.portable?.sha256 !== portable.sha256 || localizationJourney.inputs?.hosted?.sha256 !== hosted.sha256 || localizationJourney.inputs?.desktop?.sha256 !== desktop.sha256 || !same(localizationJourney.portableBoundary, checks.localization.PORTABLE_BOUNDARY)) fail('closed MF2, typed-reference, or localization receipt linkage drifted');

  const rolloutJourney = repeated(rollout.value, 'runic.current-hosted-rollout/1', 'hosted rollout');
  const authority = input.profiles['d008-hosted-product'].authority;
  if (rolloutJourney.releaseAuthority?.revision !== authority.revision || rolloutJourney.releaseAuthority?.tree !== authority.tree || rolloutJourney.releaseAuthority?.digest !== authority.sha256 || !same(localizationJourney.ownershipBoundary, checks.localization.OWNERSHIP_BOUNDARY) || !same(hosted.value.journeys?.[0]?.localeEvidence, ['en-url-over-cookie', 'de-url-over-cookie', 'unsupported-locale', 'hydration-mismatch'])) fail('D008 hosted locale or authority facts differ from the frozen profile');

  return {
    schema,
    isolation: { inputs: 'isolated-copy', nuget: 'receipt-verified-local-only', npm: 'receipt-verified-local-only' },
    freezeReceipt: { sha256: freeze.sha256, schema: freeze.value.schema },
    frozenProfile: { sha256: profileInput.sha256, profiles: input.profiles },
    consumers: {
      csharpHostTool: { sha256: csharp.sha256, schema: csharp.value.schema },
      localBridge: { sha256: bridge.sha256, qualitySha256: bridgeQuality.sha256, schema: bridge.value.schema },
      editor: { sha256: editor.sha256, localizationSha256: localization.sha256, schema: editor.value.schema },
      d008Hosted: { sha256: hosted.sha256, rolloutSha256: rollout.sha256, schema: hosted.value.schema },
    },
    rejections: ['source-project-references', 'ambient-package-cache', 'remote-endpoints', 'manifest-reference-fingerprint-locale-skew', 'structured-localization-flattening'],
    externalActions: { requests: 0, bearerChanges: 0, corsChanges: 0, proxyChanges: 0 },
  };
}

export function verifyReceipt(value, expected) {
  const errors = [];
  if (value?.schema !== repeatSchema || !Array.isArray(value.journeys) || value.journeys.length !== 2 || !same(value.journeys?.[0], value.journeys?.[1])) errors.push('two deterministic aggregate journeys are required');
  if (expected && !same(value?.journeys?.[0], expected)) errors.push('aggregate receipt differs from exact isolated inputs');
  for (const journey of value?.journeys ?? []) {
    if (journey?.schema !== schema || !/^[a-f0-9]{64}$/.test(journey?.freezeReceipt?.sha256 ?? '') || journey.freezeReceipt?.schema !== 'runic.controlled-nonpublic-profile-freeze-repeat/1' || journey?.frozenProfile?.profiles?.['csharp-host'] === undefined || !same(journey?.isolation, { inputs: 'isolated-copy', nuget: 'receipt-verified-local-only', npm: 'receipt-verified-local-only' }) || !same(journey?.rejections, ['source-project-references', 'ambient-package-cache', 'remote-endpoints', 'manifest-reference-fingerprint-locale-skew', 'structured-localization-flattening']) || !same(journey?.externalActions, { requests: 0, bearerChanges: 0, corsChanges: 0, proxyChanges: 0 })) errors.push('aggregate boundary mismatch');
  }
  return { ok: errors.length === 0, errors };
}

async function isolated(paths) {
  const directory = await mkdtemp(join(tmpdir(), 'runic-w70-clean-room-'));
  try {
    const copied = {};
    for (const [name, path] of Object.entries(paths)) { copied[name] = join(directory, `${name}.json`); await cp(resolve(path), copied[name]); }
    return await collect(copied);
  } finally { await rm(directory, { recursive: true, force: true }); }
}

export async function runTwice(paths) {
  const receipt = { schema: repeatSchema, journeys: [await isolated(paths), await isolated(paths)] };
  const report = verifyReceipt(receipt);
  if (!report.ok) fail(report.errors.join('; '));
  return receipt;
}

function argumentsFrom(argv) {
  const result = {};
  for (let index = 0; index < argv.length; index += 2) { if (!argv[index]?.startsWith('--') || !argv[index + 1] || result[argv[index]]) fail('usage'); result[argv[index].slice(2)] = argv[index + 1]; }
  const names = ['profile', 'freeze', 'csharp', 'bridge', 'bridge-quality', 'editor', 'localization', 'portable', 'hosted', 'desktop', 'rollout'];
  if (!same(Object.keys(result).filter((key) => key !== 'receipt').sort(), names.sort()) || !names.every((name) => result[name])) fail('all controlled profile inputs are required');
  return result;
}

async function main(argv) {
  const [command, ...rest] = argv; const options = argumentsFrom(rest);
  if (command === 'run-twice' && !options.receipt) return JSON.stringify(await runTwice(options), null, 2);
  if (command === 'verify-twice' && options.receipt) { const expected = await runTwice(options); const actual = JSON.parse(await readFile(options.receipt)); const report = verifyReceipt(actual, expected.journeys[0]); if (!report.ok || !same(actual, expected)) fail(report.errors.concat('receipt differs from exact isolated inputs').join('; ')); return undefined; }
  fail('usage');
}

if (import.meta.main) main(process.argv.slice(2)).then((output) => { if (output) process.stdout.write(`${output}\n`); }).catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
