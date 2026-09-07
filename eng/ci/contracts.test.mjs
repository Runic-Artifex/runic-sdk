import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, chmodSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { root, workspace } from '../run.mjs';
import { sourceDigest } from './source-state.mjs';
import { snapshot } from './snapshot.mjs';
import { buildPaths, validateBuild } from './build-artifact.mjs';
import { managedGroups, managedTests, webTests } from './plan.mjs';
import { actArguments } from './local.mjs';

const yaml = path => Bun.YAML.parse(readFileSync(resolve(root, path), 'utf8'));
const workflow = yaml('.github/workflows/ci.yml');
const fixture = action => {
  const directory = mkdtempSync(resolve(tmpdir(), 'runic-ci-test-'));
  const git = (...args) => execFileSync('git', args, { cwd: directory, stdio: 'pipe' });
  try {
    git('init', '--quiet');
    git('remote', 'add', 'origin', 'https://github.com/Runic-Artifex/runic-sdk.git');
    writeFileSync(resolve(directory, '.gitignore'), 'ignored/\n');
    writeFileSync(resolve(directory, 'tracked'), 'original');
    git('add', '.');
    git('-c', 'user.name=CI test', '-c', 'user.email=ci@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'fixture');
    action(directory, git);
  } finally { rmSync(directory, { recursive: true, force: true }); }
};

test('source identity detects edits, new files, deletion, executable modes and symlinks', () => fixture(directory => {
  const initial = sourceDigest(directory);
  const tracked = resolve(directory, 'tracked');
  writeFileSync(tracked, 'edited');
  assert.notEqual(sourceDigest(directory), initial);
  writeFileSync(tracked, 'original');
  chmodSync(tracked, 0o755);
  assert.notEqual(sourceDigest(directory), initial);
  chmodSync(tracked, 0o644);
  mkdirSync(resolve(directory, 'ignored'));
  writeFileSync(resolve(directory, 'ignored/output'), 'build');
  assert.equal(sourceDigest(directory), initial);
  symlinkSync('tracked', resolve(directory, 'link'));
  assert.notEqual(sourceDigest(directory), initial);
  rmSync(resolve(directory, 'link'));
  rmSync(tracked);
  assert.notEqual(sourceDigest(directory), initial);
}));

test('local runs freeze dirty and untracked source without carrying ignored build output', () => fixture((directory, git) => {
  // Include a tracked, ignored input and a staged addition, like a real dirty checkout.
  mkdirSync(resolve(directory, 'ignored'));
  writeFileSync(resolve(directory, 'ignored/input'), 'tracked input');
  git('add', '--force', 'ignored/input');
  writeFileSync(resolve(directory, 'ignored/output'), 'build output');
  writeFileSync(resolve(directory, 'new'), 'untracked input');
  symlinkSync('new', resolve(directory, 'link'));
  rmSync(resolve(directory, 'tracked'));
  const destination = mkdtempSync(resolve(tmpdir(), 'runic-ci-snapshot-'));
  try {
    snapshot(directory, destination);
    assert.equal(sourceDigest(destination), sourceDigest(directory));
    const frozen = sourceDigest(destination);
    writeFileSync(resolve(directory, 'new'), 'next edit');
    assert.equal(sourceDigest(destination), frozen);
    assert.notEqual(sourceDigest(directory), frozen);
    assert.equal(execFileSync('git', ['remote', 'get-url', 'origin'], { cwd: destination, encoding: 'utf8' }).trim(), 'https://github.com/Runic-Artifex/runic-sdk.git');
  } finally { rmSync(destination, { recursive: true, force: true }); }
}));

test('build outputs cannot be reused for another source, checkout, platform or toolchain', () => {
  const expected = { schema: 'runic.ci-build/1', revision: 'a', source: 'b', directory: '/work', platform: 'linux', architecture: 'x64', configuration: 'Release', sdk: '10.0.302' };
  validateBuild(expected, expected);
  for (const key of Object.keys(expected))
    assert.throws(() => validateBuild({ ...expected, [key]: 'different' }, expected), new RegExp(key));
});

test('archive paths include managed and web outputs and reject escaping paths', () => fixture(directory => {
  writeFileSync(resolve(directory, 'RunicSdk.Core.slnx'), '<Solution><Project Path="tests/Example.Tests.csproj" /></Solution>');
  for (const path of ['tests/bin', 'tests/obj', 'web/dist']) mkdirSync(resolve(directory, path), { recursive: true });
  assert.deepEqual(buildPaths(directory, [{ path: 'web' }]), ['tests/bin', 'tests/obj', 'web/dist']);
  assert.throws(() => buildPaths(directory, [{ path: '../outside' }]), /Unsafe build path/);
}));

test('all managed executable suites are assigned exactly once to workflow groups', () => {
  assert.deepEqual(workflow.jobs.managed.strategy.matrix.suite, managedGroups);
  const suites = managedTests();
  assert.equal(new Set(suites.map(item => item.path)).size, suites.length);
  for (const group of managedGroups) assert.ok(suites.some(item => item.group === group), group);
  for (const path of ['tests/dotnet/Runic.Platform.Prototype.Tests/Runic.Platform.Prototype.Tests.csproj', 'tests/dotnet/Runic.Application.Bridge.Tests/Runic.Application.Bridge.Tests.csproj'])
    assert.ok(suites.some(item => item.path === path), path);
  assert.ok(workflow.jobs.native.steps.some(step => step.run?.includes('dotnet test tests/dotnet/Runic.Desktop.Tests')));
});

test('every web package with a test script is included in the dynamic matrix', () => {
  const expected = workspace.npm.filter(item => JSON.parse(readFileSync(resolve(root, item.path, 'package.json'))).scripts?.test).map(item => item.path.split('/').at(-1));
  assert.deepEqual(webTests().map(item => item.package), expected);
  assert.equal(workflow.jobs.web.strategy.matrix, '${{ fromJSON(needs.build.outputs.web) }}');
  assert.equal(webTests().filter(item => item.node).length, 1);
});

test('verification gate includes all jobs and candidates are independent of test failures', () => {
  assert.deepEqual([...workflow.jobs.verify.needs].sort(), Object.keys(workflow.jobs).filter(key => key !== 'verify').sort());
  assert.equal(workflow.jobs.verify.if, 'always()');
  assert.equal(workflow.jobs.packages.needs, 'build');
  for (const id of ['templates', 'package-consumers', 'footprint']) assert.equal(workflow.jobs[id].needs, 'packages');
  for (const job of Object.values(workflow.jobs))
    if (job.strategy) assert.equal(job.strategy['fail-fast'], false);
});

test('local aliases use the workflow and Linux selection leaves native OS coverage to GitHub', () => {
  const scripts = JSON.parse(readFileSync(resolve(root, 'package.json'))).scripts;
  assert.equal(scripts.test, 'bun run ci');
  assert.equal(scripts.verify, 'bun run ci');
  const args = actArguments(['--job', 'templates'], 'runner', '/artifacts', 1234, '/snapshot');
  assert.equal(args[args.indexOf('--workflows') + 1], '/snapshot/.github/workflows/ci.yml');
  assert.equal(args[args.indexOf('--directory') + 1], '/snapshot');
  assert.equal(args[args.indexOf('--matrix') + 1], 'os:ubuntu-24.04');
  assert.deepEqual(args.slice(-2), ['--job', 'templates']);
  for (const id of ['native', 'footprint'])
    assert.deepEqual(workflow.jobs[id].strategy.matrix.include.map(item => item.rid), ['linux-x64', 'win-x64', 'osx-arm64']);
});
