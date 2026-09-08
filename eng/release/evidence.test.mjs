import { test, expect } from 'bun:test';
import { mkdtempSync, readFileSync, writeFileSync, existsSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { gzipSync } from 'node:zlib';
import { YAML } from 'bun';
import { authority, json, VERSION, REPOSITORY, sha256 } from './artifacts.mjs';
import { acceptancePolicy } from './policy.mjs';
import { verifyGates } from './gates.mjs';

const decoder = fileURLToPath(new URL('./decode-evidence.py', import.meta.url));
const encode = bytes => gzipSync(bytes).toString('base64');
const envelope = '{"schema":"runic.preview-evidence/1","receipts":[]}';
function withDecoded(encoded, inspect, existing) {
  const directory = mkdtempSync(join(tmpdir(), 'runic-evidence-test-'));
  const output = join(directory, 'receipts.json');
  try {
    if (existing !== undefined) writeFileSync(output, existing);
    const result = spawnSync(process.platform === 'win32' ? 'python' : 'python3', [decoder, output], {
      encoding: 'utf8', timeout: 10000,
      env: {...process.env, RECEIPTS_GZIP_BASE64: encoded},
    });
    inspect(result, output);
  } finally { rmSync(directory, {recursive: true, force: true}); }
}

test('compressed evidence preserves exact UTF-8 bytes beyond the dispatch plaintext limit', () => {
  const bytes = Buffer.from('{\n "schema":"runic.preview-evidence/1", "note":' + JSON.stringify('文😀'.repeat(18000)) + ', "receipts":[]\n}\n');
  expect(bytes.length).toBeGreaterThan(65535);
  const encoded = encode(bytes);
  expect(encoded.length).toBeLessThan(60000);
  withDecoded(encoded, (result, output) => {
    expect(result.status).toBe(0);
    expect(readFileSync(output)).toEqual(bytes);
  });
});

const invalid = [
  ['empty input', ''],
  ['invalid base64', '!!!'],
  ['non-ASCII base64', 'é'],
  ['base64 whitespace', encode(envelope) + '\n'],
  ['encoded limit', 'A'.repeat(60001)],
  ['invalid gzip', Buffer.from(envelope).toString('base64')],
  ['truncated gzip', gzipSync(envelope).subarray(0, -4).toString('base64')],
  ['expanded limit', encode(' '.repeat(1048577))],
  ['invalid UTF-8', encode(Buffer.from([0xff]))],
  ['invalid JSON', encode('{')],
  ['wrong schema', encode('{"schema":"other"}')],
  ['legacy array', encode('[]')],
  ['duplicate key', encode('{"schema":"wrong","schema":"runic.preview-evidence/1"}')],
  ['nested duplicate key', encode('{"schema":"runic.preview-evidence/1","x":{"gate":1,"gate":2}}')],
  ['NaN', encode('{"schema":"runic.preview-evidence/1","x":NaN}')],
  ['Infinity', encode('{"schema":"runic.preview-evidence/1","x":Infinity}')],
  ['overflowing JSON exponent', encode('{"schema":"runic.preview-evidence/1","x":1e9999}')],
  ['overflowing JavaScript integer', encode('{"schema":"runic.preview-evidence/1","x":' + '9'.repeat(400) + '}')],
];
for (const [name, encoded] of invalid) test(`decoder rejects ${name} without producing a receipt`, () => {
  withDecoded(encoded, (result, output) => {
    expect(result.status).not.toBe(0);
    expect(existsSync(output)).toBe(false);
  });
});

test('decoder refuses to replace existing evidence', () => {
  withDecoded(encode(envelope), (result, output) => {
    expect(result.status).not.toBe(0);
    expect(readFileSync(output, 'utf8')).toBe('retained');
  }, 'retained');
});

test('decoded receipts still require unchanged gates and exact source/digest/hash binding', () => {
  const body = {schema: 'runic.preview/1', version: VERSION, repository: REPOSITORY,
    source: 'a'.repeat(40), ciRunId: '123', acceptancePolicy: acceptancePolicy('demo-preview'),
    packages: [{file: 'nuget/test.nupkg', sha256: 'b'.repeat(64)}]};
  const candidate = {...body, digest: sha256(JSON.stringify(body))};
  const receipts = body.acceptancePolicy.required.map(gate => ({gate, outcome: 'pass',
    source: candidate.source, candidateDigest: candidate.digest,
    artifactHashes: {'nuget/test.nupkg': 'b'.repeat(64)},
    environment: 'synthetic test', scenario: 'unit fixture only', validator: 'unit fixture', evidence: 'synthetic',
    recordedAt: '2026-09-08T12:00:00Z', actualNativeInteraction: true, durationSeconds: 7200,
    baselineCommit: '5afb8b8d', matchedMachineAndWorkload: true, unresolvedRegressionsAbove20Percent: 0}));
  const evidence = {schema: 'runic.preview-evidence/1', policy: body.acceptancePolicy, receipts};
  withDecoded(encode(JSON.stringify(evidence)), (result, output) => {
    expect(result.status).toBe(0);
    verifyGates(candidate, json(output));
  });
  for (const mutate of [
    e => e.receipts.pop(),
    e => { e.receipts[0].source = 'c'.repeat(40); },
    e => { e.receipts[0].candidateDigest = 'd'.repeat(64); },
    e => { e.receipts[0].artifactHashes = {}; },
    e => { e.receipts[0].outcome = 'pending'; },
    e => { e.policy.deferred[0].outcome = 'pass'; },
  ]) {
    const changed = structuredClone(evidence); mutate(changed);
    withDecoded(encode(JSON.stringify(changed)), (result, output) => {
      expect(result.status).toBe(0);
      expect(() => verifyGates(candidate, json(output))).toThrow();
    });
  }
});

test('release inventory follows current identities, not only counts or a historical version', () => {
  const workspace = json(fileURLToPath(new URL('../workspace.json', import.meta.url)));
  expect(VERSION).toBe(workspace.version);
  expect(authority(workspace).length).toBe(workspace.nuget.length + workspace.npm.length);
  const changed = structuredClone(workspace);
  changed.nuget[0].name = 'Runic.Unexpected';
  expect(() => authority(changed)).toThrow('Inventory differs');
});

test('only maintained workflows use selected-source sealing and gated compressed evidence', () => {
  const workflow = YAML.parse(readFileSync(new URL('../../.github/workflows/preview-evidence.yml', import.meta.url), 'utf8'));
  expect(Object.keys(workflow.on.workflow_dispatch.inputs).sort()).toEqual(['ci_run_id', 'receipts_gzip_base64']);
  expect(workflow.permissions).toEqual({contents: 'read', actions: 'read'});
  const steps = workflow.jobs.evidence.steps;
  expect(steps.find(s => s.uses?.startsWith('actions/checkout')).with.ref).toBeUndefined();
  const validation = steps.find(s => s.env?.RECEIPTS_GZIP_BASE64);
  expect(validation.env.RECEIPTS_GZIP_BASE64).toBe('${{ inputs.receipts_gzip_base64 }}');
  expect(validation.run).toContain('seal artifacts/packages "$GITHUB_SHA" "$CI_RUN_ID"');
  expect(validation.run).not.toContain('${{');
  expect(validation.run).toContain('python3 eng/release/decode-evidence.py artifacts/preview/receipts.json');
  expect(validation.run.indexOf(' gates ')).toBeGreaterThan(validation.run.indexOf('decode-evidence.py'));
  const upload = steps.find(s => s.uses?.startsWith('actions/upload-artifact'));
  expect(steps.indexOf(upload)).toBeGreaterThan(steps.indexOf(validation));
  expect(upload.if).toBeUndefined();
  const publish = YAML.parse(readFileSync(new URL('../../.github/workflows/publish-preview.yml', import.meta.url), 'utf8'));
  expect(publish.concurrency).toEqual({group: 'publish-preview', 'cancel-in-progress': false});
  expect(publish.jobs.publish.environment).toBe('preview');
  for (const name of ['preview-evidence.yml', 'publish-preview.yml']) expect(existsSync(new URL(`./${name}`, import.meta.url))).toBe(false);
});
