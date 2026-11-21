import { test } from 'node:test';
import assert from 'node:assert/strict';
import { command, gate, listPath, load, outcome, quarantineJobs, selected, validate } from './quarantine.mjs';

// A real test in a project that sets RunicFlakyTests.
const entry = { issue: 1273, job: 'native', legs: ['linux-x64'], project: 'tests/dotnet/Runic.Desktop.Tests', class: 'BrowserHostTests',
  method: 'OwnsChromiumProcessProfileRestartAndNaturalExit' };

test('the committed quarantine list is valid', () => {
  // Every entry names an issue, a job that skips and reruns quarantined tests, and a test that exists.
  assert.deepEqual(validate(load()), []);
  assert.ok(listPath.endsWith('eng/ci/flakes.json'));
});

test('an entry without an issue, with a closed issue or naming no test is rejected', () => {
  assert.deepEqual(validate([entry]), []);
  assert.deepEqual(validate([entry], { states: { 1273: 'open' } }), []);
  assert.match(validate([{ ...entry, issue: undefined }])[0], /names no issue/);
  assert.match(validate([{ ...entry, issue: '1273' }])[0], /names no issue/);
  assert.match(validate([entry], { states: { 1273: 'closed' } })[0], /#1273 is closed; remove the entry/);
  assert.match(validate([{ ...entry, job: 'templates' }])[0], new RegExp(`job must be one of ${quarantineJobs.join(', ')}`));
  assert.match(validate([{ ...entry, method: 'NoSuchTest' }])[0], /has no BrowserHostTests\.NoSuchTest/);
  assert.match(validate([{ ...entry, project: 'tests/dotnet/Nothing' }])[0], /is not a test project/);
  assert.match(validate([{ ...entry, project: 'tests/dotnet/Runic.Assets.Tests', class: 'X' }])[0], /does not set RunicFlakyTests/);
  assert.match(validate([entry, entry])[0], /listed twice/);
  assert.match(validate([{ ...entry, legs: [] }])[0], /legs is a list/);
});

test('the release waits while any test is quarantined', () => {
  assert.deepEqual(gate([]), []);
  assert.deepEqual(gate([entry], { states: { 1273: 'open' } }), [
    'BrowserHostTests.OwnsChromiumProcessProfileRestartAndNaturalExit is quarantined under #1273 (open): fix it and remove the entry (eng/ci/flakes.json) before the release.']);
  assert.equal(gate([entry], { states: { 1273: 'closed' } }).length, 2, 'a stale entry is named twice: closed, and still listed');
});

test('a job and leg run their quarantined tests on their own, and the outcome is recorded', () => {
  const windows = { ...entry, legs: undefined, method: 'CanDisposeOwnedBrowserFromItsBindingCallback' };
  assert.deepEqual(selected([entry, windows], 'native', 'win-x64'), [windows]);
  assert.deepEqual(selected([entry, windows], 'native', 'linux-x64'), [entry, windows]);
  assert.deepEqual(selected([entry], 'managed', 'linux-x64'), []);
  assert.deepEqual(command({ ...entry, wrapper: ['xvfb-run', '-a'], arguments: ['--timeout', '3m'] }), ['xvfb-run', '-a', 'dotnet', 'run', '--project',
    'tests/dotnet/Runic.Desktop.Tests', '-c', 'Release', '--', '--treenode-filter', '/*/*/BrowserHostTests/OwnsChromiumProcessProfileRestartAndNaturalExit',
    '--output', 'detailed', '--timeout', '3m']);
  // The failure text of the linux-x64 sighting of 2026-11-19 (#1273).
  const failed = outcome(entry, { status: 2, output: 'failed OwnsChromiumProcessProfileRestartAndNaturalExit (8s 1ms)\n'
    + '  TUnit.Engine.Exceptions.TestFailedException: [Test Failure] FailException: Generated profile remains: /tmp/x Entries: Default (Directory).\n' }, 'linux-x64');
  assert.deepEqual([failed.quarantined, failed.outcome, failed.issue, failed.leg, failed.test], [true, 'failed', 1273, 'linux-x64', entry.method]);
  assert.match(failed.exception, /FailException: Generated profile remains/);
  assert.deepEqual([outcome(entry, { status: 0, output: '' }, 'linux-x64').outcome, outcome(entry, { status: 8, output: '' }, 'linux-x64').outcome], ['passed', 'not-run']);
  assert.equal(outcome(entry, { status: 134, output: 'Abort trap: 6' }, 'linux-x64').exception, 'exit code 134');
});
