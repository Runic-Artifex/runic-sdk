import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { root } from '../run.mjs';
import { nativeJob, nativeLegs } from './platforms.mjs';

// C2 (D077.25) and R1 (D077.26): every glibc, Windows and macOS leg runs as two shards. Every step of a sharded leg runs in
// exactly one of its shards, except the shared setup below, so a new step joins a shard on purpose and the two stay
// balanced.
const workflow = Bun.YAML.parse(readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8'));
const steps = workflow.jobs.native.steps.map(step => ({ step, name: step.name ?? step.uses.split('@')[0] }));
// The runner each sharded leg runs on, whether it runs the CS-WebUI probes, the least steps and NativeAOT publishes a
// shard has, the setup both of its shards run, and the publishes the window shard runs.
const linuxShared = ['actions/checkout', './.github/actions/flaky-tests', './.github/actions/setup-sdk',
  './.github/actions/install-browser', 'Build the shared Views runtime and the Vite plugin',
  // The CS-WebUI placement probe of the platform shard runs under Openbox too.
  'Install Linux native WebView prerequisites', 'Install Openbox for the X11 placement check', 'Permit the WebKit sandbox on Ubuntu',
  'Build native window smoke', 'Check PackageDownload packages against their pinned hashes', 'Report retried tests'];
// The GTK4 smoke's NativeAOT run follows its JIT build in the window shard.
const linuxWindowPublishes = ['Verify the GTK4 NativeAOT consumer'];
const sharded = {
  'linux-x64': {
    os: 'Linux', arch: 'X64', cswebui: true, steps: 25, publishes: 5, shared: linuxShared, windowPublishes: linuxWindowPublishes,
  },
  'linux-arm64': {
    os: 'Linux', arch: 'ARM64', cswebui: true, steps: 25, publishes: 5, shared: linuxShared, windowPublishes: linuxWindowPublishes,
  },
  'win-x64': {
    os: 'Windows', arch: 'X64', cswebui: true, steps: 30, publishes: 5,
    shared: ['actions/checkout', './.github/actions/flaky-tests', './.github/actions/setup-sdk',
      'Close the Microsoft account prompt on the Windows desktop', './.github/actions/install-browser',
      'Build the shared Views runtime and the Vite plugin', 'Build native window smoke',
      'Check PackageDownload packages against their pinned hashes', 'Report retried tests'],
  },
  // CS-WebUI ships no win-arm64 library, so this leg skips the CS-WebUI probes and their publish.
  'win-arm64': {
    os: 'Windows', arch: 'ARM64', cswebui: false, steps: 30, publishes: 4,
    shared: ['actions/checkout', './.github/actions/flaky-tests', './.github/actions/setup-sdk',
      'Close the Microsoft account prompt on the Windows desktop', './.github/actions/install-browser',
      'Build the shared Views runtime and the Vite plugin', 'Build native window smoke',
      'Check PackageDownload packages against their pinned hashes', 'Report retried tests'],
  },
  'osx-arm64': {
    os: 'macOS', arch: 'ARM64', cswebui: true, steps: 25, publishes: 3,
    shared: ['actions/checkout', './.github/actions/flaky-tests', './.github/actions/setup-sdk',
      './.github/actions/install-browser', 'Build the shared Views runtime and the Vite plugin', 'Build native window smoke',
      'Check PackageDownload packages against their pinned hashes', 'Report retried tests'],
  },
};

// The conditions the native job uses, evaluated for a leg (a hosted runner, any label) and one shard.
const runs = ({ rid, os, arch, cswebui }, step, shard) => {
  if (step.if === undefined) return true;
  const expression = String(step.if).replace(/^\$\{\{\s*|\s*\}\}$/g, '');
  const value = expression
    .replace(/matrix\.shard != '(\w+)'/g, (_, name) => String(shard !== name))
    .replace(/matrix\.cswebui/g, String(cswebui))
    .replace(/matrix\.rid (==|!=) '([\w-]+)'/g, (_, op, other) => String((other === rid) === (op === '==')))
    .replace(/runner\.os (==|!=) '(\w+)'/g, (_, op, other) => String((other === os) === (op === '==')))
    .replace(/runner\.arch (==|!=) '(\w+)'/g, (_, op, other) => String((other === arch) === (op === '==')))
    .replace(/runner\.environment (==|!=) 'github-hosted'/g, (_, op) => String(op === '=='))
    .replace(/(always|failure)\(\)/g, 'true')
    .replace(/steps\.[\w-]+\.outcome (==|!=) '\w+'/g, 'true')
    .replace(/contains\(needs\.plan\.outputs\.why, '[^']*'\)/g, 'true');
  assert.match(value, /^[\strue&|!()fals]+$/, `unexpected condition: ${expression}`);
  return Function(`return ${value}`)();
};

test('the sharded legs are linux-x64, linux-arm64, win-x64, win-arm64 and osx-arm64, each as window and platform', () => {
  assert.equal(workflow.jobs.native.name, "Native / ${{ matrix.rid }}${{ matrix.shard && format(' ({0})', matrix.shard) || '' }}");
  assert.deepEqual(nativeLegs.filter(leg => leg.shard).map(nativeJob), ['Native / linux-x64 (window)', 'Native / linux-x64 (platform)',
    'Native / linux-arm64 (window)', 'Native / linux-arm64 (platform)', 'Native / win-x64 (window)', 'Native / win-x64 (platform)',
    'Native / win-arm64 (window)', 'Native / win-arm64 (platform)', 'Native / osx-arm64 (window)', 'Native / osx-arm64 (platform)']);
  assert.ok(nativeLegs.filter(leg => !(leg.rid in sharded)).every(leg => leg.shard === undefined && nativeJob(leg) === `Native / ${leg.rid}`));
  // Each shard records its retries under a leg name of its own: the artifact names cannot collide.
  const legs = steps.filter(({ step }) => step.uses?.startsWith('./.github/actions/flaky-tests')).map(({ step }) => step.with.leg);
  assert.deepEqual(legs, Array(2).fill("${{ matrix.rid }}${{ matrix.shard && format('-{0}', matrix.shard) || '' }}"));
});

for (const [rid, { os, arch, cswebui, steps: least, publishes: aot, shared, windowPublishes = [] }] of Object.entries(sharded)) {
  test(`every ${rid} step of the native job runs in exactly one shard, except the shared setup`, () => {
    const leg = { rid, os, arch, cswebui };
    assert.deepEqual(nativeLegs.filter(entry => entry.rid === rid).map(entry => entry.shard), ['window', 'platform']);
    // The unsharded leg's steps: those that run with no shard set are exactly those that run in one shard or both.
    const unsharded = steps.filter(({ step }) => runs(leg, step, undefined)).map(({ name }) => name);
    const inAny = steps.filter(({ step }) => runs(leg, step, 'window') || runs(leg, step, 'platform')).map(({ name }) => name);
    assert.deepEqual(inAny, unsharded);
    assert.deepEqual(steps.filter(({ step }) => runs(leg, step, 'window') && runs(leg, step, 'platform')).map(({ name }) => name), shared);
    const count = shard => steps.filter(({ step }) => runs(leg, step, shard)).length;
    assert.ok(count('window') > least && count('platform') > least, `${count('window')} and ${count('platform')} steps`);
    // The NativeAOT publishes run in the platform shard, except those named for the window shard.
    const publishes = steps.filter(({ step }) => /\bdotnet publish\b[^\n]*PublishAot=true/.test(step.run ?? ''));
    assert.ok(publishes.filter(({ step }) => runs(leg, step, 'platform')).length >= aot);
    assert.deepEqual(publishes.filter(({ step }) => runs(leg, step, 'window')).map(({ name }) => name), windowPublishes);
  });
}
