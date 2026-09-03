#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { execFile as execute } from 'node:child_process';
import { promisify } from 'node:util';
import { cp, mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = resolve(import.meta.dirname, '../..');
const schema = 'runic.native-shell-capability-certification/1';
const repeatSchema = 'runic.native-shell-capability-certification-repeat/1';
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const hash = (value) => createHash('sha256').update(value).digest('hex');
const nonClaims = ['platform-success', 'browser-launch', 'file-read', 'browser-visual-e2e', 'public-listener', 'cross-platform-certification', 'os-upgrade-certification', 'c-webui-abi-change', 'signing', 'publication', 'updates'];
const fail = (message) => { throw new Error(`native-shell certification: ${message}`); };
const execFile = promisify(execute);
const abiOracle = { status: 'passing', verification: 'pinned-nix-abi-check', csWebUi: { revision: '90648d7783681b9cbc4460f43ca15f3d7283f5a2', tree: '83736bfd4b61ffe8d3abf7ebb07203897a183a1c', webuiRevision: '52f9e75b92faf9a23fd150b3c60051c4ec85fc69' }, requiredSymbol: 'webui_set_icon_file', action: 'none' };

async function receipt(path, name) {
  const bytes = await readFile(resolve(path));
  try { return { sha256: hash(bytes), value: JSON.parse(bytes) }; } catch { fail(`${name} must be valid JSON`); }
}

function repeated(value, journeySchema, name) {
  const repeat = journeySchema.replace(/\/1$/, '-repeat/1');
  if (value?.schema !== repeat || !Array.isArray(value.journeys) || value.journeys.length !== 2 || !same(value.journeys[0], value.journeys[1]) || value.journeys[0]?.schema !== journeySchema) fail(`${name} is not a deterministic ${journeySchema} receipt`);
  return value.journeys[0];
}

async function verifier() { return import(pathToFileURL(join(root, 'eng/current-native-shell/verify.mjs')).href); }

async function verifyPinnedAbi(csWebUiRoot) {
  const directory = resolve(csWebUiRoot);
  const [status, revision, tree, oracle, lock] = await Promise.all([
    execFile('git', ['status', '--porcelain'], { cwd: directory }),
    execFile('git', ['rev-parse', 'HEAD'], { cwd: directory }),
    execFile('git', ['rev-parse', 'HEAD^{tree}'], { cwd: directory }),
    readFile(join(directory, 'eng/abi/official.abi'), 'utf8'),
    readFile(join(directory, 'flake.lock'), 'utf8'),
  ]);
  if (status.stdout !== '' || revision.stdout.trim() !== abiOracle.csWebUi.revision || tree.stdout.trim() !== abiOracle.csWebUi.tree) fail('CS-WebUI source is not the pinned clean ABI-check revision');
  if (!oracle.includes('webui_set_icon_file(size_t, char*) -> void') || !lock.includes(abiOracle.csWebUi.webuiRevision)) fail('CS-WebUI ABI oracle or pinned upstream revision differs');
  try { await execFile('nix', ['build', '.#checks.x86_64-linux.abi', '-L'], { cwd: directory }); } catch { fail('pinned CS-WebUI ABI check failed'); }
  return abiOracle;
}

async function collect(paths) {
  const [profile, freeze, native] = await Promise.all([receipt(paths.profile, 'profile'), receipt(paths.freeze, 'freeze'), receipt(paths.native, 'native shell')]);
  if (profile.value?.schema !== 'runic.controlled-nonpublic-profile-input/1' || profile.value.publication !== 'forbidden') fail('profile is not the closed W70-001 input');
  const frozen = repeated(freeze.value, 'runic.controlled-nonpublic-profile-freeze/1', 'freeze');
  if (!same(frozen.profiles, profile.value.profiles) || frozen.publication !== 'forbidden' || !same(frozen.externalActions, { requests: 0, signatures: 0, metadata: 0, releases: 0, uploads: 0, tags: 0 })) fail('freeze receipt does not bind the supplied W70-001 profile');
  const nativeJourney = repeated(native.value, 'runic.native-shell-consumer/1', 'native shell');
  const checks = await verifier();
  const report = checks.verifyReceipt(native.value, { packages: nativeJourney.packages, editor: nativeJourney.editor });
  if (!report.ok) fail(`native consumer receipt rejected: ${report.errors.join('; ')}`);
  const checkedAbiOracle = await verifyPinnedAbi(paths['cs-webui']);

  const runtime = nativeJourney.managed?.runtime, capability = nativeJourney.managed?.capabilities, details = nativeJourney.nativeShell?.details;
  if (runtime?.framework !== '.NET 10.0.10' || runtime.os !== 'NixOS 26.11 (Zokor)' || runtime.architecture !== 'X64' || capability?.freePort !== true || capability.privateFileHandlerStreaming !== false || capability.webViewAvailable !== false) fail('runtime or managed prerequisite facts are not the observed Linux/X64 refusal profile');
  if (!same(nativeJourney.packages, [{ identity: 'CsWebUi', version: '2.5.0-beta.4.5', archive: 'CsWebUi.2.5.0-beta.4.5.nupkg', sha256: 'dd186e9cd1a950dc4173626bd5a58548bcd5c73689396dec8b1c11500da7519d' }, { identity: 'CsWebUi.Native', version: '2.5.0-beta.4.5', archive: 'CsWebUi.Native.2.5.0-beta.4.5.nupkg', sha256: 'ced47a6ec4d6a32f2be108b0ee101fa27845454b91466eece2a7429385ed2164' }])) fail('pinned CS-WebUI package inputs differ');
  if (nativeJourney.nativeShell?.faultCode !== 'REDIT0008' || nativeJourney.nativeShell?.capability !== 'private-file-handler-streaming-unavailable' || nativeJourney.nativeShell?.retryable !== false || !same(details, { allowedOrigin: 'exact-loopback-origin', bridge: 'generated-bridge-attached', cleanup: 'closed-disposed-cleaned', contractFingerprint: checks.bridgeContractFingerprint, highContrast: 'false', highContrastPropagated: 'true', listener: 'private-loopback', loopbackAssetRequests: '0', outboundTransportAttempts: '0', privateFileHandlerStreaming: 'false', protocolIdentity: 'runic.translations.editor', protocolVersion: '1', schema: 'runic.translations.editor-native-shell/1', webViewCapability: 'webview-prerequisite-missing' })) fail('managed refusal, loopback, bridge, contrast, cleanup, or fault facts differ');

  return {
    schema,
    isolation: { inputs: 'isolated-copy-plus-explicit-abi-root', nuget: '.nuget/packages', dotnetCliHome: '.dotnet' },
    freezeReceipt: { sha256: freeze.sha256, schema: freeze.value.schema },
    frozenProfile: { sha256: profile.sha256, profiles: profile.value.profiles },
    nativeEvidence: { sha256: native.sha256, schema: native.value.schema, packages: nativeJourney.packages, editor: nativeJourney.editor },
    observedProfile: { runtime, managedCapability: capability, certifiedResult: { status: 'unavailable', faultCode: 'REDIT0008', capability: 'private-file-handler-streaming-unavailable', actionablePrerequisite: 'webview-prerequisite-missing', deterministic: true }, managedBoundary: details },
    abiOracle: checkedAbiOracle,
    nonClaims: nonClaims,
    externalActions: { requests: 0, browserLaunches: 0, publicListeners: 0, releases: 0, uploads: 0, signatures: 0 },
  };
}

export function verifyReceipt(value, expected) {
  const errors = [];
  if (value?.schema !== repeatSchema || !Array.isArray(value.journeys) || value.journeys.length !== 2 || !same(value.journeys?.[0], value.journeys?.[1])) errors.push('two deterministic native-shell certification journeys are required');
  if (expected && !same(value?.journeys?.[0], expected)) errors.push('receipt differs from exact frozen inputs');
  for (const journey of value?.journeys ?? []) {
    if (journey?.schema !== schema || !/^[a-f0-9]{64}$/.test(journey?.freezeReceipt?.sha256 ?? '') || journey.freezeReceipt?.schema !== 'runic.controlled-nonpublic-profile-freeze-repeat/1' || !same(journey?.isolation, { inputs: 'isolated-copy-plus-explicit-abi-root', nuget: '.nuget/packages', dotnetCliHome: '.dotnet' }) || !same(journey?.observedProfile?.certifiedResult, { status: 'unavailable', faultCode: 'REDIT0008', capability: 'private-file-handler-streaming-unavailable', actionablePrerequisite: 'webview-prerequisite-missing', deterministic: true }) || !same(journey?.abiOracle, abiOracle) || !same(journey?.nonClaims, nonClaims) || !same(journey?.externalActions, { requests: 0, browserLaunches: 0, publicListeners: 0, releases: 0, uploads: 0, signatures: 0 })) errors.push('native-shell certification boundary mismatch');
  }
  return { ok: errors.length === 0, errors };
}

async function isolated(paths) {
  const directory = await mkdtemp(join(tmpdir(), 'runic-w70-native-certification-'));
  try { const copied = { 'cs-webui': paths['cs-webui'] }; for (const name of ['profile', 'freeze', 'native']) { copied[name] = join(directory, `${name}.json`); await cp(resolve(paths[name]), copied[name]); } return await collect(copied); } finally { await rm(directory, { recursive: true, force: true }); }
}

export async function runTwice(paths) {
  const value = { schema: repeatSchema, journeys: [await isolated(paths), await isolated(paths)] };
  const report = verifyReceipt(value); if (!report.ok) fail(report.errors.join('; ')); return value;
}

function args(values) { const result = {}; for (let index = 0; index < values.length; index += 2) { if (!values[index]?.startsWith('--') || !values[index + 1] || result[values[index]]) fail('usage'); result[values[index].slice(2)] = values[index + 1]; } if (!same(Object.keys(result).filter((name) => name !== 'receipt').sort(), ['cs-webui', 'freeze', 'native', 'profile'])) fail('profile, freeze, native evidence, and CS-WebUI inputs are required'); return result; }
async function main(argv) { const [command, ...rest] = argv, options = args(rest); if (command === 'run-twice' && !options.receipt) return JSON.stringify(await runTwice(options), null, 2); if (command === 'verify-twice' && options.receipt) { const expected = await runTwice(options), actual = JSON.parse(await readFile(options.receipt)), report = verifyReceipt(actual, expected.journeys[0]); if (!report.ok || !same(actual, expected)) fail(report.errors.concat('receipt differs from exact isolated inputs').join('; ')); return undefined; } fail('usage'); }
if (import.meta.main) main(process.argv.slice(2)).then((output) => { if (output) process.stdout.write(`${output}\n`); }).catch((error) => { process.stderr.write(`${error.message}\n`); process.exitCode = 1; });
