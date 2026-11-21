import { test } from 'node:test';
import assert from 'node:assert/strict';
import { managedGroups, managedTests } from './plan.mjs';
import { parseArguments, runAll, slowFirst, tasks } from './test-managed.mjs';

const reports = (path, extra, base, directory) => ['--results-directory', `${directory}/x`, '--report-trx',
  '--coverage', '--coverage-output-format', 'cobertura', '--coverage-output', 'x.cobertura.xml'];

test('the arguments name the groups, the parallelism and whether to collect coverage', () => {
  assert.deepEqual(parseArguments(['application']), { parallel: 1, coverage: true, groups: ['application'] });
  assert.deepEqual(parseArguments(['--parallel', '3', '--no-coverage', 'platform', 'application']),
    { parallel: 3, coverage: false, groups: ['platform', 'application'] });
  for (const argv of [[], ['--parallel', '0', 'platform'], ['--parallel', 'x', 'platform'], ['web'], ['platform', 'platform']])
    assert.throws(() => parseArguments(argv), undefined, argv.join(' '));
});

test('every planned project is one task, the slow ones first, and the application fixtures one more', () => {
  const list = tasks(managedGroups, { platform: 'linux', reportArguments: reports });
  const projects = managedTests(undefined, 'linux');
  assert.equal(list.filter(task => task.name !== 'application fixtures').length, projects.length);
  assert.equal(new Set(list.map(task => task.name)).size, list.length);
  // The slow projects lead, in slowFirst's order; the others keep the groups' and the solution's order.
  assert.deepEqual(list.slice(0, slowFirst.length).map(task => task.name), slowFirst);
  const rest = list.slice(slowFirst.length);
  assert.deepEqual(rest.map(task => task.group), [...rest.map(task => task.group)].sort((a, b) => managedGroups.indexOf(a) - managedGroups.indexOf(b)));
  const fixtures = list.find(task => task.name === 'application fixtures');
  assert.equal(fixtures.group, 'application');
  assert.ok(fixtures.commands.length >= 2);
  // Each project runs from the build's outputs with its group's report directory, with coverage by default.
  const [first] = list;
  assert.deepEqual(first.commands[0][1].slice(-9, -7), ['--', '--results-directory']);
  assert.ok(first.commands[0][1].includes('--no-build') && first.commands[0][1].includes('--coverage'));
  assert.ok(first.commands[0][1].includes('artifacts/test-results/application/x'));
  // --no-coverage keeps the TRX report and drops everything from --coverage on.
  const [plain] = tasks(['application'], { platform: 'linux', coverage: false, reportArguments: reports });
  assert.deepEqual(plain.commands[0][1].slice(-4), ['--', '--results-directory', 'artifacts/test-results/application/x', '--report-trx']);
  assert.ok(tasks(['platform'], { platform: 'linux', reportArguments: reports }).every(task => task.name !== 'application fixtures'));
});

test('at most <parallel> tasks run at once, in order, and every task runs after another failed', async () => {
  const list = Array.from({ length: 7 }, (_, index) => ({ name: `t${index}` }));
  let running = 0, most = 0;
  const started = [];
  const run = async task => {
    started.push(task.name);
    most = Math.max(most, ++running);
    await new Promise(done => setTimeout(done, 5 + (task.name === 't0' ? 20 : 0)));
    running--;
    return { task, code: task.name === 't2' ? 1 : 0, output: '', seconds: 0 };
  };
  const reported = [];
  const results = await runAll(list, 3, run, result => reported.push(result.task.name));
  assert.equal(most, 3);
  assert.deepEqual(started, list.map(task => task.name));
  assert.equal(results.length, 7);
  assert.deepEqual(results.filter(result => result.code).map(result => result.task.name), ['t2']);
  assert.deepEqual(reported.toSorted(), list.map(task => task.name));
  // The long first task ends last: results come in the order the tasks ended.
  assert.equal(results.at(-1).task.name, 't0');
  most = 0;
  await runAll(list, 1, run);
  assert.equal(most, 1);
});
