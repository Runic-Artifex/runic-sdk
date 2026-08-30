#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { cp, mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = resolve(import.meta.dirname, '../..');
const schema = 'runic.local-nonpublic-readiness-audit/1';
const repeatSchema = 'runic.local-nonpublic-readiness-audit-repeat/1';
const profiles = ['csharp-host', 'local-application-bridge', 'editor-desktop', 'd008-hosted-product'];
const citationIds = ['w30-rollout', 'w40-localization', 'w50-support', 'w50-recovery', 'w50-quality', 'w60-candidate', 'w60-tool', 'w60-preflight', 'w60-handoff'];
const canonicalPackages = ['Runic.Application', 'Runic.Application.Bridge', 'Runic.Application.Desktop', 'Runic.Application.Hosting', 'Runic.Application.Templates', 'Runic.Application.Testing', 'dotnet-runic'];
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const sha256 = (value) => createHash('sha256').update(value).digest('hex');
const fail = (message) => { throw new Error(`local readiness audit: ${message}`); };
const zero = (value, keys) => value && same(value, Object.fromEntries(keys.map((key) => [key, 0])));

async function receipt(path, name) { const bytes = await readFile(resolve(path)); try { return { sha256: sha256(bytes), value: JSON.parse(bytes) }; } catch { fail(`${name} must be valid JSON`); } }
function repeated(value, journeySchema, name) { const repeat = journeySchema.replace(/\/1$/, '-repeat/1'); if (value?.schema !== repeat || !Array.isArray(value.journeys) || value.journeys.length !== 2 || !same(value.journeys[0], value.journeys[1]) || value.journeys[0]?.schema !== journeySchema) fail(`${name} is not a deterministic ${journeySchema} receipt`); return value.journeys[0]; }
function source(value, name) { if (value?.repository !== 'https://github.com/Runic-Artifex/runic-toolkit' || !/^[a-f0-9]{40}$/.test(value.revision ?? '') || !/^[a-f0-9]{40}$/.test(value.tree ?? '')) fail(`${name} has malformed source facts`); return value; }
function profile(value) { if (value?.schema !== 'runic.controlled-nonpublic-profile-input/1' || value.publication !== 'forbidden' || !same(Object.keys(value.profiles ?? {}).sort(), [...profiles].sort())) fail('profile is not the closed W70-001 input'); return value; }
function frozen(value, input) {
  const journey = repeated(value, 'runic.controlled-nonpublic-profile-freeze/1', 'freeze');
  if (journey.publication !== 'forbidden' || !same(journey.profiles, input.profiles) || !same(journey.support?.profiles, profiles) || !journey.support?.nonSupport?.includes('signing') || !journey.support?.nonSupport?.includes('updates') || !zero(journey.externalActions, ['requests', 'signatures', 'metadata', 'releases', 'uploads', 'tags'])) fail('freeze does not retain the exact non-public profiles and nonclaims');
  const ids = journey.citations?.map((item) => item.id).sort();
  if (!same(ids, [...citationIds].sort()) || journey.citations.some((item) => !/^[a-f0-9]{64}$/.test(item.sha256 ?? '') || !item.schema || !profiles.includes(item.profile))) fail('freeze citations are missing, duplicate, stale, or cross-profile');
  return journey;
}
function frozenAggregate(value, journeySchema, name, freezeHash, input) {
  const journey = repeated(value, journeySchema, name);
  if (journey.freezeReceipt?.sha256 !== freezeHash || journey.freezeReceipt?.schema !== 'runic.controlled-nonpublic-profile-freeze-repeat/1' || !same(journey.frozenProfile?.profiles, input.profiles)) fail(`${name} does not bind the retained freeze`);
  return journey;
}
function citation(freeze, id) { const values = freeze.citations.filter((item) => item.id === id); if (values.length !== 1) fail(`freeze must contain exactly one '${id}' citation`); return values[0]; }
function bindCitation(freeze, id, item, name) { const expected = citation(freeze, id); if (item?.sha256 !== expected.sha256 || item?.schema !== expected.schema) fail(`${name} does not match the frozen '${id}' evidence`); }
function baseline(value) {
  const journey = repeated(value, 'runic.current-clean-install/1', 'W04 clean install');
  const authority = journey.releaseManifest;
  if (journey.feeds?.githubPackages !== 'prohibited' || !same(journey.isolation, { nugetGlobalPackagesFolder: '.nuget/packages', nugetHttpCachePath: '.nuget/http-cache', npmCache: '.npm-cache' }) || !/^[a-f0-9]{40}$/.test(authority?.revision ?? '') || !/^[a-f0-9]{40}$/.test(authority?.tree ?? '') || !/^[a-f0-9]{64}$/.test(authority?.digest ?? '') || authority.before?.status !== '' || authority.after?.status !== '') fail('W04 foundation is stale, non-isolated, or malformed');
  return authority;
}
function packageGate(value) {
  if (value?.schema !== 'runic.canonical-seven-package-gate/1' || value.postFreeze !== true || value.retainedProfile !== false || value.packageVersion !== '0.2.0-preview.w80001' || !same(value.source, { repository: 'https://github.com/Runic-Artifex/runic-toolkit', revision: 'ea17ae7162683b7a65accb76e3f15c30eb887210', tree: 'b3db3ce2bfdb89e8165014b2d658c22e5a7b2a7e' }) || value.artifactVerification !== 'passed' || value.packagedConsumer !== 'passed' || value.authority?.publication !== 'forbidden' || !same(value.authority?.candidateDistribution, { id: 'translations-editor-archive', version: { state: 'unassigned', value: null } }) || !zero(value.externalActions, ['requests', 'signatures', 'updates', 'uploads', 'releases', 'tags'])) fail('post-freeze package gate is assigned, published, stale, or has external actions');
  if (!Array.isArray(value.packages) || value.packages.length !== canonicalPackages.length || !same(value.packages.map((item) => item.identity).sort(), [...canonicalPackages].sort()) || value.packages.some((item) => !/^[a-f0-9]{64}$/.test(item.sha256 ?? '') || !Number.isInteger(item.size) || item.size <= 0 || item.archive !== `${item.identity}.${value.packageVersion}.nupkg`)) fail('post-freeze package gate does not bind the canonical seven artifacts');
  return value;
}
async function verifiers() { return { clean: await import(pathToFileURL(join(root, 'eng/current-controlled-clean-room/verify.mjs')).href), support: await import(pathToFileURL(join(root, 'eng/current-support-certification/verify.mjs')).href), native: await import(pathToFileURL(join(root, 'eng/current-native-shell-certification/verify.mjs')).href) }; }
function check(report, name) { if (!report?.ok) fail(`${name} verifier rejected the supplied receipt: ${(report.errors ?? []).join('; ')}`); }

async function collect(paths) {
  const [profileInput, freezeReceipt, w04, cleanRoom, supportReceipt, nativeReceipt, gateReceipt] = await Promise.all([receipt(paths.profile, 'profile'), receipt(paths.freeze, 'freeze'), receipt(paths.w04, 'W04'), receipt(paths['clean-room'], 'clean room'), receipt(paths.support, 'support'), receipt(paths.native, 'native'), receipt(paths['package-gate'], 'package gate')]);
  const input = profile(profileInput.value), frozenReceipt = frozen(freezeReceipt.value, input), freezeHash = freezeReceipt.sha256;
  const [clean, support, native] = [frozenAggregate(cleanRoom.value, 'runic.controlled-clean-room-conformance/1', 'clean room', freezeHash, input), frozenAggregate(supportReceipt.value, 'runic.support-certification/1', 'support', freezeHash, input), frozenAggregate(nativeReceipt.value, 'runic.native-shell-capability-certification/1', 'native', freezeHash, input)];
  const checks = await verifiers(); check(checks.clean.verifyReceipt(cleanRoom.value, clean), 'clean room'); check(checks.support.verifyReceipt(supportReceipt.value, support), 'support'); check(checks.native.verifyReceipt(nativeReceipt.value, native), 'native');
  if (!zero(clean.externalActions, ['requests', 'bearerChanges', 'corsChanges', 'proxyChanges']) || !zero(support.externalActions, ['requests', 'uploads', 'telemetry', 'dashboard']) || !zero(native.externalActions, ['requests', 'browserLaunches', 'publicListeners', 'releases', 'uploads', 'signatures'])) fail('W70 evidence contains external actions');
  bindCitation(frozenReceipt, 'w60-tool', clean.consumers?.csharpHostTool, 'clean-room C# host/tool');
  bindCitation(frozenReceipt, 'w50-quality', { sha256: clean.consumers?.localBridge?.qualitySha256, schema: 'runic.editor-structural-quality-repeat/1' }, 'clean-room local Bridge quality');
  bindCitation(frozenReceipt, 'w60-candidate', clean.consumers?.editor, 'clean-room Editor candidate');
  bindCitation(frozenReceipt, 'w40-localization', { sha256: clean.consumers?.editor?.localizationSha256, schema: 'runic.w40-localization-compatibility-repeat/1' }, 'clean-room localization');
  bindCitation(frozenReceipt, 'w30-rollout', { sha256: clean.consumers?.d008Hosted?.rolloutSha256, schema: 'runic.current-hosted-rollout-repeat/1' }, 'clean-room D008 rollout');
  bindCitation(frozenReceipt, 'w50-support', support.evidence?.support, 'support certification');
  bindCitation(frozenReceipt, 'w50-recovery', support.evidence?.recovery, 'recovery certification');
  bindCitation(frozenReceipt, 'w50-quality', support.evidence?.quality, 'quality certification');
  if (native.abiOracle?.status !== 'passing' || native.observedProfile?.certifiedResult?.capability !== 'private-file-handler-streaming-unavailable' || native.observedProfile?.certifiedResult?.status !== 'unavailable') fail('native evidence does not retain the bounded managed refusal');
  const w04Authority = baseline(w04.value), gate = packageGate(gateReceipt.value);
  return {
    schema,
    publication: 'forbidden',
    isolation: { inputs: 'isolated-copy', transport: 'none' },
    retainedProfiles: input.profiles,
    evidence: {
      w04Foundation: { sha256: w04.sha256, schema: w04.value.schema, authority: { revision: w04Authority.revision, tree: w04Authority.tree, digest: w04Authority.digest } },
      w70Freeze: { sha256: freezeHash, schema: freezeReceipt.value.schema, citations: frozenReceipt.citations },
      w70CleanRoom: { sha256: cleanRoom.sha256, schema: cleanRoom.value.schema },
      w70Support: { sha256: supportReceipt.sha256, schema: supportReceipt.value.schema },
      w70Native: { sha256: nativeReceipt.sha256, schema: nativeReceipt.value.schema },
    },
    postFreezePackageGate: { sha256: gateReceipt.sha256, source: gate.source, packageVersion: gate.packageVersion, packages: gate.packages, status: 'passed-not-retained' },
    nonClaims: ['release-version-assignment', 'signing', 'notarization', 'attestation-issuance', 'upload', 'publication', 'updates', 'hosted-topology-change', 'c-webui-abi-change', 'native-platform-success'],
    nativeCapability: { status: 'unavailable', capability: 'private-file-handler-streaming-unavailable', abiOracle: 'passing' },
    externalActions: { requests: 0, signatures: 0, updates: 0, uploads: 0, releases: 0, tags: 0 },
  };
}

export function verifyReceipt(value, expected) {
  const errors = [];
  if (value?.schema !== repeatSchema || !Array.isArray(value.journeys) || value.journeys.length !== 2 || !same(value.journeys?.[0], value.journeys?.[1])) errors.push('two deterministic readiness-audit journeys are required');
  if (expected && !same(value?.journeys?.[0], expected)) errors.push('receipt differs from exact isolated inputs');
  for (const journey of value?.journeys ?? []) if (journey?.schema !== schema || journey.publication !== 'forbidden' || !same(Object.keys(journey.retainedProfiles ?? {}).sort(), [...profiles].sort()) || !same(journey.isolation, { inputs: 'isolated-copy', transport: 'none' }) || journey.postFreezePackageGate?.status !== 'passed-not-retained' || !same(journey.nativeCapability, { status: 'unavailable', capability: 'private-file-handler-streaming-unavailable', abiOracle: 'passing' }) || !same(journey.externalActions, { requests: 0, signatures: 0, updates: 0, uploads: 0, releases: 0, tags: 0 })) errors.push('readiness-audit boundary mismatch');
  return { ok: errors.length === 0, errors };
}
async function isolated(paths) { const directory = await mkdtemp(join(tmpdir(), 'runic-w80-readiness-audit-')); try { const copied = {}; for (const [name, path] of Object.entries(paths)) { copied[name] = join(directory, `${name}.json`); await cp(resolve(path), copied[name]); } return await collect(copied); } finally { await rm(directory, { recursive: true, force: true }); } }
export async function runTwice(paths) { const value = { schema: repeatSchema, journeys: [await isolated(paths), await isolated(paths)] }; const report = verifyReceipt(value); if (!report.ok) fail(report.errors.join('; ')); return value; }
function args(argv) { const values = {}; for (let index = 0; index < argv.length; index += 2) { if (!argv[index]?.startsWith('--') || !argv[index + 1] || values[argv[index]]) fail('usage'); values[argv[index].slice(2)] = argv[index + 1]; } const names = ['profile', 'freeze', 'w04', 'clean-room', 'support', 'native', 'package-gate']; if (!same(Object.keys(values).filter((key) => key !== 'receipt').sort(), names.sort())) fail('all readiness audit inputs are required'); return values; }
async function main(argv) { const [command, ...rest] = argv, options = args(rest); if (command === 'run-twice' && !options.receipt) return JSON.stringify(await runTwice(options), null, 2); if (command === 'verify-twice' && options.receipt) { const actual = JSON.parse(await readFile(options.receipt)); const expected = await runTwice(options), report = verifyReceipt(actual, expected.journeys[0]); if (!report.ok || !same(actual, expected)) fail(report.errors.concat('receipt differs from exact isolated inputs').join('; ')); return; } fail('usage'); }
if (import.meta.main) main(process.argv.slice(2)).then((output) => { if (output) process.stdout.write(`${output}\n`); }).catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
