import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { root } from '../run.mjs';
import { collect, failingTests, format, report, sinceDate } from './flakes.mjs';

// Recorded, trimmed gh api responses of runic-sdk's ci.yml runs on 2026-11-16 and 17: the VoiceOver return (#1093) and
// PathWatcher (#1008) flakes twice each, a Windows test session that hit its --timeout, a merge queue entry with bun
// test failures, a passed first attempt and a cancelled run.
const fixtures = resolve(root, 'eng/ci/fixtures/flakes');
const read = name => readFileSync(resolve(fixtures, name), 'utf8');
const recorded = (overrides = {}) => {
  const requests = [];
  return {
    requests,
    json: async path => {
      requests.push(path);
      if (path.includes('/workflows/ci.yml/runs?')) return JSON.parse(read('runs.json')).workflow_runs;
      const run = /\/actions\/runs\/(\d+)\/jobs\?filter=all/.exec(path);
      if (run) return JSON.parse(read(`jobs-${run[1]}.json`)).jobs;
      if (/\/issues\?state=all/.test(path)) return overrides.issues ?? JSON.parse(read('issues.json'));
      throw new Error(`unrecorded request ${path}`);
    },
    log: async id => {
      requests.push(`log ${id}`);
      if (!existsSync(resolve(fixtures, `log-${id}.txt`))) throw new Error(`no log for ${id}`);
      return read(`log-${id}.txt`);
    },
  };
};
const since = new Date('2026-11-15T16:00:00Z');
const statusOf = (result, key) => result.groups.find(group => group.key === key)?.status;

test('failure lines name the failing tests of TUnit, bun, node:test and the probes', () => {
  const log = [
    '2026-11-17T12:43:33.7829665Z failed DirectoryReplacedWithoutAReadIsWatchedAgain(False) (793ms)',
    '2026-11-17T12:43:33.8Z failed DirectoryReplacedWithoutAReadIsWatchedAgain(True) (1s 2ms)',
    '\x1b[31m(fail) Runic Inspect on a React page in webkit [41181.91ms]\x1b[0m',
    '✖ writes a lock file (12.5ms)',
    'FAIL           voiceover-side-panel-return: VoiceOver did not say \'Finish\'',
    '[44.80s] FAIL window-restart: after the Window restarted: NOT stopped',
    'running... 3 completed, 0 failed | active: SomethingElse',
    '  failed: 0',
  ].join('\n');
  assert.deepEqual(failingTests(log), ['DirectoryReplacedWithoutAReadIsWatchedAgain', 'Runic Inspect on a React page in webkit',
    'writes a lock file', 'voiceover-side-panel-return', 'window-restart']);
  // A session stopped by its timeout names the test then active; a passing log names none.
  assert.deepEqual(failingTests(read('log-134074693218.txt')), ['DevelopmentOpensALoopbackEndpointInAnInstalledBrowser (session aborted while it ran)']);
  assert.deepEqual(failingTests('running... 9 completed, 0 failed | active: Waits\nTest run summary: Passed!'), []);
});

test('--since takes days, hours or a date', () => {
  const now = new Date('2026-11-17T12:00:00Z');
  assert.equal(sinceDate('2d', now).toISOString(), '2026-11-15T12:00:00.000Z');
  assert.equal(sinceDate('36h', now).toISOString(), '2026-11-16T00:00:00.000Z');
  assert.equal(sinceDate('2026-11-10', now).toISOString(), '2026-11-10T00:00:00.000Z');
  assert.throws(() => sinceDate('yesterday', now), /--since/);
});

test('the report groups recorded failures across runs and attempts and reads only failed or rerun runs', async () => {
  const source = recorded();
  const data = await collect(source, { since });
  // Only reads: the run list, failed or rerun runs' jobs (every attempt), failed jobs' logs (not verify's) and issues.
  assert.ok(source.requests.every(request => /^(log \d+|repos\/Runic-Artifex\/runic-sdk\/(actions\/|issues\?))/.test(request)), source.requests.join('\n'));
  assert.equal(data.runs, 6, 'the passed first attempt and the cancelled run are not read');
  assert.ok(!data.failures.some(failure => failure.job === 'verify'));
  const result = report(data);
  // Both flakes were seen twice, and their issues closed after the last sighting: the fixes (#1100, #1101) hold.
  assert.equal(statusOf(result, 'Native / osx-arm64 › voiceover-side-panel-return'), 'fixed');
  assert.equal(statusOf(result, 'Native / win-arm64 › DirectoryReplacedWithoutAReadIsWatchedAgain'), 'fixed');
  assert.deepEqual(result.groups.find(group => group.name === 'DirectoryReplacedWithoutAReadIsWatchedAgain').issues.map(issue => issue.number), [1008],
    'pull requests are not issues');
  assert.equal(statusOf(result, 'Native / win-x64 › DevelopmentOpensALoopbackEndpointInAnInstalledBrowser (session aborted while it ran)'), 'first-sighting');
  assert.equal(statusOf(result, 'Web packages and framework consumers › Runic Inspect keeps a pressed card link through a refresh in webkit'), 'first-sighting');
  // The rerun PR run counts its failed first attempt.
  assert.deepEqual(result.groups.find(group => group.key === 'Native / win-arm64 › DirectoryReplacedWithoutAReadIsWatchedAgain').sightings
    .map(item => [item.event, item.attempt]), [['push', 1], ['pull_request', 1]]);
  // The merge queue entry failed in tests no open issue names.
  assert.deepEqual(result.queue.map(entry => [entry.run, entry.verdict]), [[43909958494, 'investigate'], [43878333721, 'investigate']]);
});

test('an open issue makes a known flake, and a queue entry failing only in known flakes is re-queued', async () => {
  const issues = JSON.parse(read('issues.json')).map(issue => issue.number === 1093 ? { ...issue, state: 'open', closed_at: null } : issue);
  const result = report(await collect(recorded({ issues }), { since }));
  assert.equal(statusOf(result, 'Native / osx-arm64 › voiceover-side-panel-return'), 'known');
  assert.deepEqual(result.queue.find(entry => entry.run === 43909958494).verdict, 're-queue');
  assert.match(format(result), /Known flakes[^\n]*:\n {2}Native \/ osx-arm64 › voiceover-side-panel-return: 2 runs — #1093 \(open\)/);
});

test('a name seen twice without an issue needs one; after its issue closed, it regressed; a step-only repeat is read', async () => {
  const none = report(await collect(recorded({ issues: [] }), { since }));
  assert.equal(statusOf(none, 'Native / osx-arm64 › voiceover-side-panel-return'), 'needs-issue');
  assert.match(format(none), /no issue mentions it[^\n]*:\n(?: {2,}.*\n)*? {2}Native \/ osx-arm64 › voiceover-side-panel-return: 2 runs\n/);
  const early = JSON.parse(read('issues.json')).map(issue => ({ ...issue, closed_at: '2026-11-16T12:00:00Z' }));
  assert.equal(statusOf(report(await collect(recorded({ issues: early }), { since })), 'Native / osx-arm64 › voiceover-side-panel-return'), 'regressed');
  // With --min 3 two sightings are not yet a pattern.
  assert.equal(statusOf(report(await collect(recorded({ issues: [] }), { since }), { min: 3 }), 'Native / osx-arm64 › voiceover-side-panel-return'), 'first-sighting');
  // A job whose log is unavailable or names no test is grouped by its failed step.
  const step = report({ repository: 'r', since: since.toISOString(), runs: 2, issues: [], failures: [1, 2].map(run => ({
    name: 'step: Verify generated react template applications', test: false, job: 'Templates / react+svelte', step: 'Verify generated react template applications',
    run, attempt: 1, event: 'pull_request', branch: 'b', pullRequests: [1], url: `https://example.invalid/runs/${run}/job/1`, at: '2026-11-16T00:00:00Z' })) });
  assert.equal(step.groups[0].status, 'repeated-step');
});
