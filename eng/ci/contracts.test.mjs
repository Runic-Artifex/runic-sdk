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
import { managedGroups, managedTests, jobSuites, webTests, plan, applicationFixtures, applicationFixtureCommands } from './plan.mjs';
import { actArguments } from './local.mjs';
import { nativeLegs, wpfLegs } from './platforms.mjs';
import { expandJobScripts, expandWorkflow } from './jobs.mjs';

const yaml = path => Bun.YAML.parse(readFileSync(resolve(root, path), 'utf8'));
// Job-script calls (eng/ci/jobs) are read as the commands they run.
const workflow = expandWorkflow(yaml('.github/workflows/ci.yml'));
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
  // One job runs each planned group in its own step, also after another group failed, and uploads its reports.
  const { steps } = workflow.jobs.managed;
  assert.equal(workflow.jobs.managed.strategy, undefined);
  const restore = steps.find(step => step.uses === './.github/actions/restore-build');
  assert.equal(restore?.id, 'restore');
  const groups = steps.filter(step => step.run?.startsWith('bun eng/ci/test-managed.mjs '));
  assert.deepEqual(groups.map(step => step.id), managedGroups);
  for (const step of groups) {
    assert.equal(step.run, `bun eng/ci/test-managed.mjs ${step.id}`);
    assert.equal(step.if, `\${{ !cancelled() && steps.restore.outcome == 'success' && contains(fromJSON(needs.plan.outputs.managed), '${step.id}') }}`);
    const upload = steps[steps.indexOf(step) + 1];
    assert.ok(upload.uses?.startsWith('actions/upload-artifact@'), step.id);
    assert.equal(upload.if, `\${{ always() && steps.${step.id}.outcome != 'skipped' }}`);
    assert.equal(upload.with.name, `managed-test-results-${step.id}-\${{ github.run_id }}`);
    assert.equal(upload.with.path, `artifacts/test-results/${step.id}\n!artifacts/test-results/${step.id}/*/_*\n`);
  }
  assert.ok(readFileSync(resolve(root, 'eng/ci/test-managed.mjs'), 'utf8').includes('`artifacts/test-results/${group}`'));
  assert.deepEqual(plan(null).managed, managedGroups);
  const suites = managedTests(root, 'win32');
  assert.equal(new Set(suites.map(item => item.path)).size, suites.length);
  for (const group of managedGroups) assert.ok(suites.some(item => item.group === group), group);
  for (const path of ['tests/dotnet/Runic.Platform.Runtime.Tests/Runic.Platform.Runtime.Tests.csproj',
    'tests/dotnet/Runic.Platform.Linux.Tests/Runic.Platform.Linux.Tests.csproj',
    'tests/dotnet/Runic.Application.Tool.Tests/Runic.Application.Tool.Tests.csproj'])
    assert.ok(suites.some(item => item.path === path), path);
  // The WPF hybrid editor example's TUnit suite runs with the application group.
  assert.ok(suites.some(item => item.path === 'examples/wpf-hybrid-editor/Tests/HybridNotes.Tests.csproj' && item.group === 'application'));
  // The suites another job runs are not managed suites, and that job runs them.
  for (const [job, paths] of Object.entries(jobSuites))
    for (const path of paths) {
      assert.ok(!suites.some(item => item.path === path), path);
      assert.ok(workflow.jobs[job].steps.some(step => step.run?.includes(`dotnet run --project ${path.slice(0, path.lastIndexOf('/'))} `)), path);
    }
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
    .flatMap(name => expandJobScripts(readFileSync(resolve(root, '.github/workflows', name), 'utf8')).split('\n'))
    .filter(line => /\bdotnet (run|publish|test)\b/.test(line) || /\b(bun|node) \S*package-smoke\.mjs\b/.test(line));
  // A package-smoke.mjs in the fixture's root folder also covers its nested projects.
  const runByWorkflow = path => {
    const directory = path.slice(0, path.lastIndexOf('/'));
    const fixture = path.split('/').slice(0, 4).join('/');
    return commands.some(line => line.includes(path) || line.includes(`${directory}/package-smoke.mjs`)
      || line.includes(`${fixture}/package-smoke.mjs`) || new RegExp(`${directory.replaceAll('.', '\\.')}(?=[\\s"']|$)`).test(line));
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
  const windows = applicationFixtureCommands('Release', root, 'win32');
  assert.ok(windows.every(([, args]) => args[0] === 'run'));
  assert.equal(workflow.jobs.managed['runs-on'], 'ubuntu-24.04');
});

test('every web package with a test script has a planned step in the web job', () => {
  const expected = workspace.npm.filter(item => JSON.parse(readFileSync(resolve(root, item.path, 'package.json'))).scripts?.test).map(item => item.path.split('/').at(-1));
  assert.deepEqual(webTests().map(item => item.package), expected);
  const { steps } = workflow.jobs.web;
  assert.equal(workflow.jobs.web.strategy, undefined);
  assert.equal(steps.find(step => step.uses === './.github/actions/restore-build')?.id, 'restore');
  const tests = steps.filter(step => step.run === 'bun run --bun test');
  assert.deepEqual(tests.map(step => step['working-directory']), expected.map(name => `packages/web/${name}`));
  for (const step of tests) {
    const name = step['working-directory'].split('/').at(-1);
    assert.equal(step.name, `Test ${name}`);
    assert.equal(step.if, `\${{ !cancelled() && steps.restore.outcome == 'success' && contains(fromJSON(needs.plan.outputs.web), '${name}') }}`);
  }
  assert.deepEqual(plan(null).web, expected);
  // vite-plugin-runic runs under real Node with Chromium; the browser installs only when it is planned.
  assert.equal(webTests().filter(item => item.node).length, 1);
  const setup = steps.find(step => step.uses === './.github/actions/setup-sdk');
  assert.equal(setup.with.node, 'true');
  const browser = steps.find(step => step.uses === './.github/actions/install-browser');
  assert.equal(browser?.if, "${{ contains(fromJSON(needs.plan.outputs.web), 'vite-plugin-runic') }}");
  // The Svelte checks and npm framework consumers follow the plan's frameworks output.
  const frameworks = steps.filter(step => step.if?.includes('needs.plan.outputs.frameworks'));
  assert.deepEqual(frameworks.map(step => step.run), ['bun run --bun check', 'bun run --bun check', 'bun tests/web/svelte-package-consumers/package-consumers.mjs']);
  for (const step of frameworks)
    assert.equal(step.if, "${{ !cancelled() && steps.restore.outcome == 'success' && fromJSON(needs.plan.outputs.frameworks) }}");
  const inline = workflow.jobs.web.steps.find(step => step.name === 'Verify Svelte inline SSR and hydration');
  assert.equal(inline, undefined);
});

test('verification gate includes all jobs and candidates are independent of test failures', () => {
  assert.deepEqual([...workflow.jobs.verify.needs].sort(), Object.keys(workflow.jobs).filter(key => key !== 'verify').sort());
  assert.equal(workflow.jobs.verify.if, 'always()');
  // The build job packs the candidates after archiving its outputs, before and independently of any test job.
  assert.equal(workflow.jobs.packages, undefined);
  const build = workflow.jobs.build.steps;
  const archive = build.findIndex(step => step.with?.name === 'sdk-build-${{ github.run_id }}');
  const pack = build.findIndex(step => step.run === 'bun eng/run.mjs pack-built');
  const upload = build.findIndex(step => step.with?.name === 'runic-sdk-${{ github.run_id }}');
  assert.ok(archive >= 0 && pack > archive && upload > pack, build.map(step => step.name ?? step.uses).join('\n'));
  assert.ok(build.slice(pack, upload).some(step => step.run === 'bun eng/release/verify-template-locks.mjs'));
  for (const id of ['templates', 'package-consumers', 'wpf']) assert.deepEqual(workflow.jobs[id].needs, ['plan', 'build']);
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
  for (const path of ['examples/first-window/browser-smoke.mjs', 'examples/first-window/navigation-smoke.mjs',
    'examples/notes-view-first/window-smoke.mjs',
    'examples/first-window-desktop/browser-smoke.mjs',
    'examples/notes-view-first/browser-smoke.mjs', 'examples/notes-reactive-views/browser-smoke.mjs',
    'examples/notes-reactive-views/hmr-smoke.mjs', 'examples/notes-reactive-views/ide-host-smoke.mjs',
    'examples/notes-reactive-views/dotnet-hot-reload-smoke.mjs'])
    assert.ok(steps.includes(path), path);
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run?.includes('examples/first-window/package-smoke.mjs')));
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.uses === './.github/actions/install-browser'));
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run?.includes('bun run --cwd packages/web/views --bun build')));
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run?.includes('examples/first-window-desktop/package-smoke.mjs')));
  // W230-002: the navigator through a packaged Bridge window, with JIT and NativeAOT.
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run === 'bun tests/fixtures/application/navigation-consumer/package-smoke.mjs'));
  // W240-003: the packed Runic.Navigation alone, with no Runic build assets or Node, with JIT and NativeAOT.
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run === 'bun tests/fixtures/navigation/standalone-consumer/package-smoke.mjs'));
  // W240-006: the packed ReactiveUI navigation adapters pull in no Runic.Application.Views.
  assert.ok(workflow.jobs['package-consumers'].steps.some(step =>
    step.run === 'bun tests/fixtures/navigation/reactiveui-consumer/package-smoke.mjs'));
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
  // The Arm entries run on runners act does not map, so local runs keep linux-x64.
  assert.deepEqual(nativeLegs.map(item => [item.rid, item.os]),
    [['linux-x64', 'ubuntu-24.04'], ['linux-arm64', 'ubuntu-24.04-arm'], ['win-x64', 'windows-latest'], ['win-arm64', 'windows-11-arm'], ['osx-arm64', 'macos-26']]);
  // act passes no container engine into its jobs, so the musl container step does not run there.
  const musl = workflow.jobs['native-musl'];
  assert.deepEqual(musl.strategy.matrix.include.map(item => [item.rid, item.os]),
    [['linux-musl-x64', 'ubuntu-24.04'], ['linux-musl-arm64', 'ubuntu-24.04-arm']]);
  const container = musl.steps.find(step => step.run?.includes('eng/ci/native-musl.sh'));
  assert.equal(container?.if, '${{ !env.ACT }}');
  assert.match(container.run, /docker run .*--security-opt seccomp=unconfined/s);
  assert.match(musl.env.ALPINE_IMAGE, /^docker\.io\/library\/alpine:3\.22@sha256:[0-9a-f]{64}$/);
});

test('the Windows native job scales the example smoke timeouts (#70)', () => {
  const { env } = workflow.jobs.native;
  assert.equal(env.RUNIC_SMOKE_TIMEOUT_SCALE, "${{ matrix.smoke-timeout-scale || '1' }}");
  const scales = Object.fromEntries(nativeLegs.map(item => [item.rid, item['smoke-timeout-scale']]));
  assert.deepEqual(scales, { 'linux-x64': undefined, 'linux-arm64': undefined, 'win-x64': '2', 'win-arm64': '2', 'osx-arm64': undefined });
});

test('template lanes cover every framework once and run the creator check in one lane', () => {
  const { strategy, steps } = workflow.jobs.templates;
  assert.deepEqual(strategy.matrix.include, [{ first: 'react', second: 'svelte', creator: '1' }, { first: 'vue', second: 'angular', 'desktop-smoke': '1' }]);
  assert.deepEqual(strategy.matrix.include.flatMap(lane => [lane.first, lane.second]).sort(), ['angular', 'react', 'svelte', 'vue']);
  const [step, second] = steps.filter(item => item.run === 'bun run verify:templates');
  assert.equal(step?.name, 'Verify generated ${{ matrix.first }} template applications');
  assert.equal(step?.env.RUNIC_TEMPLATE_FRAMEWORKS, '${{ matrix.first }}');
  assert.equal(step?.env.RUNIC_TEMPLATE_CREATOR, "${{ matrix.creator || '0' }}");
  // The vue step owns the desktop-gtk4 variant; its GTK 4 smoke needs the runtime installed first.
  assert.equal(step?.env.RUNIC_TEMPLATE_DESKTOP_SMOKE, "${{ matrix.desktop-smoke || '0' }}");
  // The second framework also runs after the first failed, without the creator and desktop smoke.
  assert.equal(second?.name, 'Verify generated ${{ matrix.second }} template applications');
  assert.deepEqual(second?.env, { RUNIC_TEMPLATE_FRAMEWORKS: '${{ matrix.second }}', RUNIC_TEMPLATE_CREATOR: '0', RUNIC_TEMPLATE_DESKTOP_SMOKE: '0' });
  assert.equal(steps.find(item => item.uses?.startsWith('actions/download-artifact@'))?.id, 'candidates');
  assert.equal(second.if, "${{ !cancelled() && steps.candidates.outcome == 'success' }}");
  const install = steps.findIndex(item => item.run?.includes('libwebkitgtk-6.0-4'));
  assert.ok(install >= 0 && install < steps.indexOf(step));
  assert.equal(steps[install].if, "matrix.desktop-smoke == '1'");
  const sandbox = steps.findIndex(item => item.run === 'eng/ci/permit-webkit-sandbox.sh');
  assert.ok(sandbox > install && sandbox < steps.indexOf(step));
  assert.equal(steps[sandbox].if, "matrix.desktop-smoke == '1'");
  // Each lane verifies only the variants of its framework, so the desktop-gtk4 variants must belong to the
  // smoke lane's framework, or their smoke would be skipped silently.
  const smokeFrameworks = strategy.matrix.include.filter(item => item['desktop-smoke'] === '1').map(item => item.first);
  const variants = [...readFileSync(resolve(root, 'tests/templates/Test-Templates.sh'), 'utf8')
    .matchAll(/^\s*"(\w+) (\w+) desktop-gtk4 (\w+)( tests)?"$/gm)];
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

// The repository's cache quota is shared: pull request entries evicted main's caches. Only main saves.
test('dependency caches restore everywhere and save only on main', () => {
  const files = execFileSync('git', ['ls-files', '-z', '.github'], { cwd: root, encoding: 'utf8' }).split('\0').filter(path => /\.ya?ml$/.test(path));
  const users = files.filter(path => /uses:\s*actions\/cache(\/\w+)?@/.test(readFileSync(resolve(root, path), 'utf8')));
  assert.deepEqual(users, ['.github/actions/dependency-cache/action.yml']);
  const [save, restore] = yaml('.github/actions/dependency-cache/action.yml').runs.steps;
  assert.match(save.uses, /^actions\/cache@/);
  assert.equal(save.if, "github.ref == 'refs/heads/main'");
  assert.match(restore.uses, /^actions\/cache\/restore@/);
  assert.equal(restore.if, "github.ref != 'refs/heads/main'");
  assert.equal(save.uses.split('@')[1], restore.uses.split('@')[1]);
  for (const step of [save, restore])
    assert.deepEqual(step.with, { path: '${{ inputs.path }}', key: '${{ inputs.key }}', 'restore-keys': '${{ inputs.restore-keys }}' });
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

// W240-005: the WPF hosts run in a Windows desktop session, then their packed consumer.
test('the WPF lane runs the STA tests and the packed consumer on Windows', () => {
  const job = workflow.jobs.wpf;
  assert.equal(job['runs-on'], '${{ matrix.os }}');
  assert.deepEqual(wpfLegs.map(({ os, rid }) => ({ os, rid })), [{ os: 'windows-2025', rid: 'win-x64' }, { os: 'windows-11-arm', rid: 'win-arm64' }]);
  assert.equal(job['timeout-minutes'], 30);
  // win-arm64 runs the WPF hosts; the architecture-independent generator and packed-consumer steps run on win-x64.
  const hosts = ['dotnet run --project tests/dotnet/Runic.Navigation.Wpf.Tests -c Release',
    'dotnet run --project tests/dotnet/Runic.Application.Views.Wpf.Windows.Tests -c Release'];
  for (const step of job.steps.filter(step => step.run))
    assert.equal(step.if, hosts.includes(step.run) || step.run.includes('HybridNotes.WindowsSmoke') || step.run.includes('source-state.mjs check')
      ? undefined : "matrix.rid == 'win-x64'", step.name);
  const runs = job.steps.map(step => step.run).filter(Boolean);
  const tests = runs.indexOf('dotnet run --project tests/dotnet/Runic.Navigation.Wpf.Tests -c Release');
  const consumer = runs.indexOf('bun tests/fixtures/navigation/wpf-consumer/package-smoke.mjs');
  assert.ok(tests >= 0 && consumer > tests, runs.join('\n'));
  assert.ok(job.steps.some(step => step.uses?.startsWith('actions/download-artifact@') && step.with?.path === 'artifacts/packages'));
});

// #269 and #270 were reported on Windows: the generator's framework tests and the
// packed WPF Bridge consumer run there as well as on Linux.
test('the WPF lane runs the Bridge generator framework checks on Windows', () => {
  const runs = workflow.jobs.wpf.steps.map(step => step.run).filter(Boolean);
  assert.ok(runs.some(run => run.includes('--treenode-filter "/*/*/CodegenDiagnosticsTests/*Framework*"')), runs.join('\n'));
  assert.ok(runs.some(run => run.includes('--treenode-filter "/*/*/BridgeAttributeNamesTests/*"')), runs.join('\n'));
  assert.ok(runs.includes('bun tests/fixtures/application/wpf-bridge-consumer/package-smoke.mjs'), runs.join('\n'));
});
