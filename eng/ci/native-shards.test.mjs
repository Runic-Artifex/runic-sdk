import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { root } from '../run.mjs';
import { nativeJob, nativeLegs } from './platforms.mjs';

// C2 (D077.25): win-x64 runs as two shards. Every step runs in one of them, except the shared setup below, so a new step
// joins a shard on purpose and the two stay balanced.
test('every win-x64 step of the native job runs in one shard, except the shared setup', () => {
  const workflow = Bun.YAML.parse(readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8'));
  assert.equal(workflow.jobs.native.name, "Native / ${{ matrix.rid }}${{ matrix.shard && format(' ({0})', matrix.shard) || '' }}");
  const shards = nativeLegs.filter(leg => leg.rid === 'win-x64');
  assert.deepEqual(shards.map(leg => leg.shard), ['window', 'platform']);
  assert.deepEqual(shards.map(nativeJob), ['Native / win-x64 (window)', 'Native / win-x64 (platform)']);
  assert.ok(nativeLegs.filter(leg => leg.rid !== 'win-x64').every(leg => leg.shard === undefined && nativeJob(leg) === `Native / ${leg.rid}`));
  // The conditions the native job uses, evaluated on win-x64 (CS-WebUI, a hosted runner, any label).
  const runs = (step, shard) => {
    if (step.if === undefined) return true;
    const expression = String(step.if).replace(/^\$\{\{\s*|\s*\}\}$/g, '');
    const value = expression
      .replace(/matrix\.shard != '(\w+)'/g, (_, name) => String(shard !== name))
      .replace(/matrix\.cswebui/g, 'true')
      .replace(/matrix\.rid (==|!=) '([\w-]+)'/g, (_, op, rid) => String((rid === 'win-x64') === (op === '==')))
      .replace(/runner\.os (==|!=) '(\w+)'/g, (_, op, os) => String((os === 'Windows') === (op === '==')))
      .replace(/runner\.arch (==|!=) '(\w+)'/g, (_, op, arch) => String((arch === 'X64') === (op === '==')))
      .replace(/runner\.environment (==|!=) 'github-hosted'/g, (_, op) => String(op === '=='))
      .replace(/(always|failure)\(\)/g, 'true')
      .replace(/steps\.[\w-]+\.outcome (==|!=) '\w+'/g, 'true')
      .replace(/contains\(needs\.plan\.outputs\.why, '[^']*'\)/g, 'true');
    assert.match(value, /^[\strue&|!()fals]+$/, `unexpected condition: ${expression}`);
    return Function(`return ${value}`)();
  };
  const shared = ['actions/checkout', './.github/actions/flaky-tests', './.github/actions/setup-sdk',
    'Close the Microsoft account prompt on the Windows desktop', './.github/actions/install-browser',
    'Build the shared Views runtime and the Vite plugin', 'Build native window smoke',
    'Check PackageDownload packages against their pinned hashes', 'Report retried tests'];
  const steps = workflow.jobs.native.steps.map(step => ({ step, name: step.name ?? step.uses.split('@')[0] }));
  assert.deepEqual(steps.filter(({ step }) => runs(step, 'window') && runs(step, 'platform')).map(({ name }) => name), shared);
  const count = shard => steps.filter(({ step }) => runs(step, shard)).length;
  assert.ok(count('window') > 30 && count('platform') > 30, `${count('window')} and ${count('platform')} steps`);
  // The NativeAOT publishes run in the platform shard only.
  const publishes = steps.filter(({ step }) => /\bdotnet publish\b[^\n]*PublishAot=true/.test(step.run ?? ''));
  assert.ok(publishes.filter(({ step }) => runs(step, 'platform')).length >= 5);
  assert.deepEqual(publishes.filter(({ step }) => runs(step, 'window')).map(({ name }) => name), []);
  // Each shard records its retries under a leg name of its own: the artifact names cannot collide.
  const legs = steps.filter(({ step }) => step.uses?.startsWith('./.github/actions/flaky-tests')).map(({ step }) => step.with.leg);
  assert.deepEqual(legs, Array(2).fill("${{ matrix.rid }}${{ matrix.shard && format('-{0}', matrix.shard) || '' }}"));
});
