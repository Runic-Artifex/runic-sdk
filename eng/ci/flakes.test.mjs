import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { root } from '../run.mjs';
import { collect, failingTests, firstError, format, messageHead, normalizeError, report, sinceDate, withSmallerPages } from './flakes.mjs';

// Recorded, trimmed gh api responses of runic-sdk's ci.yml runs on 2026-11-16 and 17:
// - the VoiceOver return (#1093, closed) and PathWatcher (#1008, reopened) flakes twice each, both passing on a rerun;
// - a Windows test session that hit its --timeout;
// - a merge queue entry whose two jobs failed the same bun test;
// - the Inspect React page failure that #1054 names only by its error;
// - three template jobs whose logs name no test;
// - a passed first attempt and a cancelled run.
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
const entry = (result, name) => {
  const found = result.groups.filter(group => group.name === name);
  assert.equal(found.length, 1, `${name}: ${found.length} entries`);
  return found[0];
};
const voiceOver = 'voiceover-side-panel-return';
const pathWatcher = 'DirectoryReplacedWithoutAReadIsWatchedAgain';
const pressed = 'Runic Inspect keeps a pressed card link through a refresh in chromium';
const reactPage = 'Runic Inspect on a React page in webkit';

test('failure lines name the failing tests of TUnit, bun, node:test and the probes, each with its first error line', () => {
  const log = [
    '2026-11-17T12:43:33.7829665Z failed DirectoryReplacedWithoutAReadIsWatchedAgain(False) (793ms)',
    '  TUnit.Engine.Exceptions.TestFailedException: [Test Failure] InvalidOperationException: The watcher polls. events=5',
    '    at Runic.Application.Testing.Tests.PathWatcherTests.Require(Boolean condition) in /_/tests/PathWatcherTests.cs:722',
    '2026-11-17T12:43:33.8Z failed DirectoryReplacedWithoutAReadIsWatchedAgain(True) (1s 2ms)',
    '  TUnit.Engine.Exceptions.TestFailedException: [Test Failure] InvalidOperationException: The watcher polls. events=7',
    '(pass) Runic Inspect on a React page in chromium [1521.99ms]',
    '37 |     if (Date.now() > deadline) throw new Error(`No editor was launched`);',
    'error: No editor was launched: /tmp/runic-inspect-browser-5jnA3t/editor-arguments.txt holds no arguments.',
    '      at readRecord (/home/runner/work/runic-sdk/runic-sdk/tests/support/recording-editor.mjs:37:42)',
    '##[error]',
    '\x1b[31m(fail) Runic Inspect on a React page in webkit [41181.91ms]\x1b[0m',
    '✖ writes a lock file (12.5ms)',
    '  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:',
    'FAIL           voiceover-side-panel-return: VoiceOver did not say \'Finish\'; it said: You are currently on a button.',
    '[44.80s] FAIL window-restart: after the Window restarted: NOT stopped',
    'running... 3 completed, 0 failed | active: SomethingElse',
    '  failed: 0',
  ].join('\n');
  assert.deepEqual(failingTests(log).map(({ name, error }) => [name, error]), [
    // Both cases of the parameterized test are one test, and their counts read the same.
    ['DirectoryReplacedWithoutAReadIsWatchedAgain', 'InvalidOperationException: The watcher polls. events=N'],
    ['Runic Inspect on a React page in webkit', 'error: No editor was launched: <path> holds no arguments.'],
    ['writes a lock file', 'AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:'],
    // A probe's message is its signature up to the first "; ": what VoiceOver said instead varies.
    ['voiceover-side-panel-return', 'VoiceOver did not say \'Finish\''],
    ['window-restart', 'after the Window restarted: NOT stopped'],
  ]);
  // A session stopped by its timeout names the test then active; a passing log names none.
  assert.deepEqual(failingTests(read('log-134074693218.txt')).map(item => item.name), ['DevelopmentOpensALoopbackEndpointInAnInstalledBrowser']);
  assert.deepEqual(failingTests('running... 9 completed, 0 failed | active: Waits\nTest run summary: Passed!'), []);
});

test('errors normalize to a signature, and their words are what an issue quotes', () => {
  assert.equal(normalizeError('error: No editor was launched within 40000 ms: C:\\a\\x\\y.txt and /tmp/a-1/b holds {"a":1}'),
    'error: No editor was launched within N ms: <path> and <path> holds {…}');
  assert.equal(messageHead('error: No editor was launched: /tmp/x/editor-arguments.txt holds no arguments.'), 'No editor was launched');
  assert.equal(messageHead('TUnit.Engine.Exceptions.TestFailedException: [Test Failure] InvalidOperationException: The watcher polls instead of watching. disposed=False'),
    'The watcher polls instead of watching. disposed');
  assert.equal(messageHead('error: bash exited with 1'), null, 'too short to tell failures apart');
});

test('a step whose log names no test takes its first meaningful ##[error] line, else the error printed before it', () => {
  assert.equal(firstError('##[error]Process completed with exit code 1.\n##[error]win-arm64 lacks its library.'), 'win-arm64 lacks its library.');
  assert.equal(firstError('TypeError: x is undefined\nerror: bash exited with 1\n##[error]\n##[error]Process completed with exit code 1.'), 'error: bash exited with 1');
  assert.equal(firstError('##[error]Process completed with exit code 2.'), 'Process completed with exit code 2.');
  assert.equal(firstError(read('log-133503092706.txt')), 'error: bash exited with 1');
});

test('a server error retries the page with half the page size, down to 25', async () => {
  const asked = [];
  const get = async path => {
    asked.push(path);
    if (!path.includes('per_page=25')) throw new Error('gh api repos/r/actions/workflows/ci.yml/runs?per_page=100: HTTP 502: Bad Gateway');
    return ['ok'];
  };
  assert.deepEqual(await withSmallerPages(get, 'repos/r/runs?created=x&per_page=100'), ['ok']);
  assert.deepEqual(asked, ['repos/r/runs?created=x&per_page=100', 'repos/r/runs?created=x&per_page=50', 'repos/r/runs?created=x&per_page=25']);
  await assert.rejects(withSmallerPages(async () => { throw new Error('HTTP 404: Not Found'); }, 'a?per_page=100'), /404/);
  await assert.rejects(withSmallerPages(async () => { throw new Error('HTTP 502'); }, 'a?per_page=25'), /502/);
});

test('--since takes days, hours or a date', () => {
  const now = new Date('2026-11-17T12:00:00Z');
  assert.equal(sinceDate('2d', now).toISOString(), '2026-11-15T12:00:00.000Z');
  assert.equal(sinceDate('36h', now).toISOString(), '2026-11-16T00:00:00.000Z');
  assert.equal(sinceDate('2026-11-10', now).toISOString(), '2026-11-10T00:00:00.000Z');
  assert.throws(() => sinceDate('yesterday', now), /--since/);
});

test('the report groups recorded failures by signature across runs, jobs and legs, and reads only failed or rerun runs', async () => {
  const source = recorded();
  const data = await collect(source, { since });
  // Only reads: the run list, failed or rerun runs' jobs (every attempt), failed jobs' logs (not verify's) and issues.
  assert.ok(source.requests.every(request => /^(log \d+|repos\/Runic-Artifex\/runic-sdk\/(actions\/|issues\?))/.test(request)), source.requests.join('\n'));
  assert.equal(data.runs, 8, 'the passed first attempt and the cancelled run are not read');
  assert.equal(data.cancelled, 1);
  assert.ok(!data.failures.some(failure => failure.job === 'verify'));
  const result = report(data);
  assert.equal(result.cancelled, 1);
  // The same bun failure in the Node 24 and Node 26 jobs is one entry; VoiceOver's two different replies are one too.
  assert.deepEqual(entry(result, pressed).jobs.sort(), ['Web packages and framework consumers', 'npm packages / Node 26']);
  assert.equal(entry(result, pressed).runs, 1);
  assert.equal(entry(result, voiceOver).runs, 2);
  // #1093 closed after both sightings; #1008 was reopened, so the PathWatcher failure is a known flake.
  assert.equal(entry(result, voiceOver).status, 'fixed');
  assert.equal(entry(result, pathWatcher).status, 'known');
  assert.deepEqual(entry(result, pathWatcher).issues.map(issue => issue.number), [1008], 'pull requests are not issues');
  // #1054 names the test only as exerciseMessages; it quotes the error.
  assert.deepEqual(entry(result, reactPage).issues.map(issue => issue.number), [1054]);
  assert.equal(entry(result, reactPage).status, 'fixed');
  // A job that passed on a rerun of the same commit is a flake; one that failed without a rerun is not known to be.
  assert.equal(entry(result, pathWatcher).flake, true);
  assert.equal(entry(result, voiceOver).flake, true);
  assert.equal(entry(result, 'DevelopmentOpensALoopbackEndpointInAnInstalledBrowser').flake, true);
  assert.equal(entry(result, pressed).flake, false);
  assert.deepEqual(entry(result, pathWatcher).sightings.map(item => [item.event, item.attempt, item.passedOnRerun]),
    [['push', 1, false], ['pull_request', 1, true]]);
  // The template jobs' logs name no test: each step is an entry with its error, one run each.
  const react = entry(result, 'step: Verify generated react template applications');
  assert.deepEqual([react.test, react.error, react.runs, react.jobs.length], [false, 'error: bash exited with N', 1, 2]);
  // The merge queue entries failed in tests no open issue names.
  assert.deepEqual(result.queue.map(entry => [entry.run, entry.verdict]), [[43909958494, 'investigate'], [43878333721, 'investigate']]);
  const text = format(result);
  assert.match(text, /; 1 cancelled \(not classified\)/);
  assert.match(text, new RegExp(`  ${pathWatcher}: 2 runs, Native / win-arm64 — #1008 \\(open\\) — flake: passed on a rerun of the same commit\\n    InvalidOperationException: The watcher polls`));
  assert.match(text, /\(passed on rerun\)/);
});

test('an open issue makes a known flake, and a queue entry failing only in known flakes is re-queued', async () => {
  const issues = JSON.parse(read('issues.json')).map(issue => issue.number === 1093 ? { ...issue, state: 'open', closed_at: null } : issue);
  const result = report(await collect(recorded({ issues }), { since }));
  assert.equal(entry(result, voiceOver).status, 'known');
  assert.equal(result.queue.find(item => item.run === 43909958494).verdict, 're-queue');
  assert.match(format(result), /Known flakes[^\n]*:\n(?:.*\n)*? {2}voiceover-side-panel-return: 2 runs, Native \/ osx-arm64 — #1093 \(open\)/);
});

test('an entry seen twice without an issue needs one; after its issue closed, it regressed; a step-only repeat is read', async () => {
  const none = report(await collect(recorded({ issues: [] }), { since }));
  assert.equal(entry(none, voiceOver).status, 'needs-issue');
  assert.match(format(none), /no issue mentions it[^\n]*:\n(?:.*\n)*? {2}voiceover-side-panel-return: 2 runs/);
  const early = JSON.parse(read('issues.json')).map(issue => ({ ...issue, state: 'closed', closed_at: '2026-11-16T12:00:00Z' }));
  assert.equal(entry(report(await collect(recorded({ issues: early }), { since })), voiceOver).status, 'regressed');
  // With --min 3 two sightings are not yet a pattern.
  assert.equal(entry(report(await collect(recorded({ issues: [] }), { since }), { min: 3 }), voiceOver).status, 'first-sighting');
  // A step-only failure in two runs with the same error is one repeated entry; another error is another entry.
  const failure = (run, error) => ({ name: 'step: Verify generated react template applications', test: false, error, raw: error,
    job: 'Templates / react+svelte', step: 'Verify generated react template applications', run, attempt: 1, passedOnRerun: false,
    event: 'pull_request', branch: 'b', pullRequests: [1], url: `https://example.invalid/runs/${run}/job/1`, at: '2026-11-16T00:00:00Z' });
  const steps = report({ repository: 'r', since: since.toISOString(), runs: 3, issues: [],
    failures: [failure(1, 'error: bash exited with N'), failure(2, 'error: bash exited with N'), failure(3, 'TypeError: x')] });
  assert.deepEqual(steps.groups.map(group => [group.error, group.runs, group.status]),
    [['error: bash exited with N', 2, 'repeated-step'], ['TypeError: x', 1, 'first-sighting']]);
});
