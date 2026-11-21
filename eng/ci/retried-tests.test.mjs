import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseRetries, report, summary, warning } from './retried-tests.mjs';

const line = { test: 'DirectoryReplacedWithoutAReadIsWatchedAgain(True)', method: 'DirectoryReplacedWithoutAReadIsWatchedAgain',
  class: 'PathWatcherTests', assembly: 'Runic.Application.Testing.Tests', reason: 'RealClock', leg: 'win-arm64',
  exception: 'TimeoutException: No path change was reported within 10 s. 50% | done', attempt: 1, at: '2026-11-21T01:00:00Z' };

test('each recorded retry is a warning and a job summary row; a torn line is skipped', () => {
  assert.deepEqual(parseRetries(`${JSON.stringify(line)}\n{"test": 1}\n{not json\n\n`), [line]);
  assert.equal(warning(line), '::warning title=Flaky test retried::DirectoryReplacedWithoutAReadIsWatchedAgain(True) (Runic.Application.Testing.Tests, win-arm64)'
    + ' failed and was retried once (RealClock): TimeoutException: No path change was reported within 10 s. 50%25 | done. A retry counts as a flake sighting (eng/ci/flakes.mjs).');
  assert.match(summary([line]), /\| DirectoryReplacedWithoutAReadIsWatchedAgain\(True\) \| Runic\.Application\.Testing\.Tests \| win-arm64 \| RealClock \| TimeoutException: [^|]*50% \\\| done \|/);
});

test('the report sets the retried output the upload depends on', () => {
  const directory = mkdtempSync(join(tmpdir(), 'retried-tests-'));
  try {
    const env = { GITHUB_OUTPUT: join(directory, 'output'), GITHUB_STEP_SUMMARY: join(directory, 'summary') };
    const printed = [];
    assert.deepEqual(report(join(directory, 'missing.jsonl'), { env, log: text => printed.push(text) }), []);
    assert.equal(readFileSync(env.GITHUB_OUTPUT, 'utf8'), 'retried=false\n');
    assert.deepEqual(printed, ['No test was retried or quarantined.']);
    const file = join(directory, 'retried-tests.jsonl');
    writeFileSync(file, `${JSON.stringify(line)}\n${JSON.stringify({ ...line, test: 'NativeMessageBoxesAsync' })}\n`);
    printed.length = 0;
    assert.equal(report(file, { env, log: text => printed.push(text) }).length, 2);
    assert.equal(readFileSync(env.GITHUB_OUTPUT, 'utf8'), 'retried=false\nretried=true\n');
    assert.equal(printed.filter(text => text.startsWith('::warning title=Flaky test retried::')).length, 2);
    assert.match(readFileSync(env.GITHUB_STEP_SUMMARY, 'utf8'), /### Flaky tests retried[\s\S]*NativeMessageBoxesAsync/);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
