import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, readdirSync, rmSync, chmodSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { root, workspace } from '../run.mjs';
import { sourceDigest } from './source-state.mjs';
import { snapshot } from './snapshot.mjs';
import { buildPaths, validateBuild } from './build-artifact.mjs';
import { managedGroups, managedTests, webTests, plan, applicationFixtures, applicationFixtureCommands } from './plan.mjs';
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
  assert.equal(workflow.jobs.managed.strategy.matrix.suite, '${{ fromJSON(needs.plan.outputs.managed) }}');
  assert.deepEqual(plan(null).managed, managedGroups);
  const suites = managedTests(root, 'win32');
  assert.equal(new Set(suites.map(item => item.path)).size, suites.length);
  for (const group of managedGroups) assert.ok(suites.some(item => item.group === group), group);
  for (const path of ['tests/dotnet/Runic.Platform.Runtime.Tests/Runic.Platform.Runtime.Tests.csproj',
    'tests/dotnet/Runic.Platform.Linux.Tests/Runic.Platform.Linux.Tests.csproj',
    'tests/dotnet/Runic.Application.Tool.Tests/Runic.Application.Tool.Tests.csproj'])
    assert.ok(suites.some(item => item.path === path), path);
  assert.ok(workflow.jobs.native.steps.some(step => step.run?.includes('dotnet test tests/dotnet/Runic.Desktop.Tests')));
  assert.ok(workflow.jobs.native.steps.some(step =>
    step.run?.includes('dotnet publish tests/dotnet/Runic.Platform.Runtime.Tests/') && step.run.includes('PublishAot=true')));
});

test('every executable application fixture runs in CI', () => {
  // Experiments are measured by hand; see tests/fixtures/application/experiments.
  // A project counts as executable unless it declares itself a library, so an
  // OutputType inherited from a Directory.Build.props file is covered too.
  const fixtures = readdirSync(resolve(root, 'tests/fixtures/application'), { recursive: true })
    .map(path => `tests/fixtures/application/${path.replaceAll('\\', '/')}`)
    .filter(path => path.endsWith('.csproj') && !/\/(bin|obj|experiments)\//.test(path)
      && !/<OutputType>Library<\/OutputType>/.test(readFileSync(resolve(root, path), 'utf8')));
  // Only command lines count, not paths filters or comments. A fixture that
  // restores packed packages runs through its own package-smoke.mjs.
  const commands = readdirSync(resolve(root, '.github/workflows'))
    .flatMap(name => readFileSync(resolve(root, '.github/workflows', name), 'utf8').split('\n'))
    .filter(line => /\bdotnet (run|publish|test)\b/.test(line) || /\b(bun|node) \S*package-smoke\.mjs\b/.test(line));
  const runByWorkflow = path => {
    const directory = path.slice(0, path.lastIndexOf('/'));
    return commands.some(line => line.includes(path) || line.includes(`${directory}/package-smoke.mjs`) || new RegExp(`${directory.replaceAll('.', '\\.')}(?=[\\s"']|$)`).test(line));
  };
  const suites = managedTests(root, 'linux').map(item => item.path);
  const listed = applicationFixtures.map(item => item.path);
  assert.ok(fixtures.length >= 7, fixtures.join(', '));
  for (const path of fixtures)
    assert.ok(suites.includes(path) || listed.includes(path) || runByWorkflow(path), path);
  assert.ok(!runByWorkflow('tests/fixtures/application/reactiveui-reactive-flavor/ReactiveUiReactiveFlavorProof.csproj'));
  for (const path of ['reactiveui-reactive-flavor/ReactiveUiReactiveFlavorProof.csproj',
    'reactiveui-reactive-flavor/ReactiveUiReactiveSourceGeneratorProof.csproj', 'reactiveui25-aot/ReactiveUi25AotProof.csproj'])
    assert.ok(listed.includes(`tests/fixtures/application/${path}`), path);
  // The managed job runs on Linux, so it publishes and runs the NativeAOT fixture.
  const linux = applicationFixtureCommands('Release', root, 'linux').map(([command, args]) => [command, ...args].join(' '));
  assert.ok(linux.some(line => line.startsWith('dotnet publish') && line.includes('ReactiveUi25AotProof.csproj')));
  assert.ok(linux.some(line => line.endsWith('/ReactiveUi25AotProof')));
  assert.ok(applicationFixtureCommands('Release', root, 'win32').every(([, args]) => args[0] === 'run'));
  assert.equal(workflow.jobs.managed['runs-on'], 'ubuntu-24.04');
});

test('every web package with a test script is included in the dynamic matrix', () => {
  const expected = workspace.npm.filter(item => JSON.parse(readFileSync(resolve(root, item.path, 'package.json'))).scripts?.test).map(item => item.path.split('/').at(-1));
  assert.deepEqual(webTests().map(item => item.package), expected);
  assert.equal(workflow.jobs.web.strategy.matrix, '${{ fromJSON(needs.plan.outputs.web) }}');
  assert.deepEqual(plan(null).web.include, webTests());
  assert.equal(webTests().filter(item => item.node).length, 1);
  const browser = workflow.jobs.web.steps.find(step => step.uses === './.github/actions/install-browser');
  assert.equal(browser?.if, "matrix.package == 'vite-plugin-runic'");
  const inline = workflow.jobs.web.steps.find(step => step.name === 'Verify Svelte inline SSR and hydration');
  assert.equal(inline, undefined);
});

test('verification gate includes all jobs and candidates are independent of test failures', () => {
  assert.deepEqual([...workflow.jobs.verify.needs].sort(), Object.keys(workflow.jobs).filter(key => key !== 'verify').sort());
  assert.equal(workflow.jobs.verify.if, 'always()');
  assert.deepEqual(workflow.jobs.packages.needs, ['plan', 'build']);
  for (const id of ['templates', 'package-consumers']) assert.deepEqual(workflow.jobs[id].needs, ['plan', 'packages']);
  for (const job of Object.values(workflow.jobs))
    if (job.strategy) assert.equal(job.strategy['fail-fast'], false);
});

test('package consumers verify isolated NuGet and npm installations', () => {
  const step = workflow.jobs['package-consumers'].steps.find(item =>
    item.name === 'Verify isolated NuGet and npm consumers');
  assert.equal(step?.run, 'bun run verify-packages');
});

test('Views replace the Bridge application gates', () => {
  assert.ok(workflow.jobs.views);
  assert.equal(workflow.jobs.bridge, undefined);
  assert.equal(workflow.jobs.customers, undefined);
  const steps = workflow.jobs.views.steps.map(step => step.run ?? '').join('\n');
  for (const path of ['examples/first-window/browser-smoke.mjs', 'examples/notes-view-first/window-smoke.mjs',
    'examples/first-window-desktop/browser-smoke.mjs',
    'examples/notes-view-first/browser-smoke.mjs', 'examples/notes-reactive-views/browser-smoke.mjs',
    'examples/notes-reactive-views/hmr-smoke.mjs', 'examples/notes-reactive-views/ide-host-smoke.mjs'])
    assert.ok(steps.includes(path), path);
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run?.includes('examples/first-window/package-smoke.mjs')));
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.uses === './.github/actions/install-browser'));
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run?.includes('bun run --cwd packages/web/views --bun build')));
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run?.includes('examples/first-window-desktop/package-smoke.mjs')));
  // W230-002: the experimental navigator through a packaged Bridge window, with JIT and NativeAOT.
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run === 'bun tests/fixtures/application/navigation-consumer/package-smoke.mjs'));
  // W240-003: the packed Runic.Navigation alone, with no Runic build assets or Node, with JIT and NativeAOT.
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run === 'bun tests/fixtures/navigation/standalone-consumer/package-smoke.mjs'));
  assert.ok(workflow.jobs.native.steps.some(step =>
    step.run?.includes('dotnet publish examples/first-window/FirstWindow.csproj') && step.run.includes('PublishAot=true')));
});

test('local checks are focused and Linux workflow selection leaves native OS coverage to GitHub', () => {
  const scripts = JSON.parse(readFileSync(resolve(root, 'package.json'))).scripts;
  assert.equal(scripts.test, 'bun eng/test.mjs');
  assert.equal(scripts.verify, 'bun eng/test.mjs');
  const args = actArguments(['--job', 'templates'], 'runner', '/artifacts', 1234, '/snapshot');
  assert.equal(args[args.indexOf('--workflows') + 1], '/snapshot/.github/workflows/ci.yml');
  assert.equal(args[args.indexOf('--directory') + 1], '/snapshot');
  assert.equal(args[args.indexOf('--matrix') + 1], 'os:ubuntu-24.04');
  assert.deepEqual(args.slice(-2), ['--job', 'templates']);
  for (const id of ['native'])
    assert.deepEqual(workflow.jobs[id].strategy.matrix.include.map(item => item.rid), ['linux-x64', 'win-x64', 'osx-arm64']);
});

test('the Windows native job scales the example smoke timeouts (#70)', () => {
  const { env, strategy } = workflow.jobs.native;
  assert.equal(env.RUNIC_SMOKE_TIMEOUT_SCALE, "${{ matrix.smoke-timeout-scale || '1' }}");
  const scales = Object.fromEntries(strategy.matrix.include.map(item => [item.rid, item['smoke-timeout-scale']]));
  assert.deepEqual(scales, { 'linux-x64': undefined, 'win-x64': '2', 'osx-arm64': undefined });
});

test('template lanes cover every framework once and run the creator check in one lane', () => {
  const { strategy, steps } = workflow.jobs.templates;
  assert.deepEqual(strategy.matrix.framework, ['react', 'vue', 'svelte', 'angular']);
  assert.deepEqual(strategy.matrix.include, [{ framework: 'react', creator: '1' }, { framework: 'vue', 'desktop-smoke': '1' }]);
  const step = steps.find(item => item.run === 'bun run verify:templates');
  assert.equal(step?.env.RUNIC_TEMPLATE_FRAMEWORKS, '${{ matrix.framework }}');
  assert.equal(step?.env.RUNIC_TEMPLATE_CREATOR, "${{ matrix.creator || '0' }}");
  // The vue lane owns the desktop-gtk4 variant; its GTK 4 smoke needs the runtime installed first.
  assert.equal(step?.env.RUNIC_TEMPLATE_DESKTOP_SMOKE, "${{ matrix.desktop-smoke || '0' }}");
  const install = steps.findIndex(item => item.run?.includes('libwebkitgtk-6.0-4'));
  assert.ok(install >= 0 && install < steps.indexOf(step));
  assert.equal(steps[install].if, "matrix.desktop-smoke == '1'");
  const sandbox = steps.findIndex(item => item.run === 'eng/ci/permit-webkit-sandbox.sh');
  assert.ok(sandbox > install && sandbox < steps.indexOf(step));
  assert.equal(steps[sandbox].if, "matrix.desktop-smoke == '1'");
  // Each lane verifies only the variants of its framework, so the desktop-gtk4 variants must belong to the
  // smoke lane's framework, or their smoke would be skipped silently.
  const smokeFrameworks = strategy.matrix.include.filter(item => item['desktop-smoke'] === '1').map(item => item.framework);
  const variants = [...readFileSync(resolve(root, 'tests/templates/Test-Templates.sh'), 'utf8')
    .matchAll(/^\s*"(\w+) (\w+) desktop-gtk4 (\w+)"$/gm)];
  assert.ok(variants.length > 0, 'Test-Templates.sh has no desktop-gtk4 variant.');
  for (const [, framework] of variants) assert.deepEqual(smokeFrameworks, [framework]);
});

test('native and template jobs share the WebKit sandbox permission script', () => {
  const users = Object.entries(workflow.jobs)
    .filter(([, job]) => job.steps?.some(item => item.run === 'eng/ci/permit-webkit-sandbox.sh'))
    .map(([id]) => id);
  assert.deepEqual(users.sort(), ['native', 'templates']);
  assert.ok(!readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8').includes('apparmor_parser'));
});

test('remote actions are pinned to a commit with their release tag', () => {
  const files = execFileSync('git', ['ls-files', '-z', '.github', 'eng/ci/fixtures'], { cwd: root, encoding: 'utf8' })
    .split('\0').filter(path => /\.ya?ml$/.test(path));
  const references = files.flatMap(path => [...readFileSync(resolve(root, path), 'utf8')
    .matchAll(/^\s*(?:-\s*)?uses:\s*(\S+)(.*)$/gm)].map(match => ({ path, action: match[1], comment: match[2].trim() })))
    .filter(item => !item.action.startsWith('./'));
  assert.ok(references.length > 0);
  for (const item of references)
    assert.match(`${item.action} ${item.comment}`, /^[\w.-]+\/[\w./-]+@[0-9a-f]{40} # v\d+\.\d+\.\d+$/, `${item.path}: ${item.action}`);
});
