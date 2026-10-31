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
import { nativeLegs, testTimeoutScales, wpfLegs } from './platforms.mjs';
import { testTimeoutScale } from './test-timeout-scale.mjs';
import { npmReleaseAgeArguments } from '../dependencies/release-age.mjs';

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
    .flatMap(name => readFileSync(resolve(root, '.github/workflows', name), 'utf8').split('\n'))
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
  // vite-plugin-runic runs under real Node with Chromium; the browser installs only when it, views (menu bar) or a
  // framework adapter with text-field tests is planned.
  assert.equal(webTests().filter(item => item.node).length, 1);
  const setup = steps.find(step => step.uses === './.github/actions/setup-sdk');
  assert.equal(setup.with.node, 'true');
  const browser = steps.find(step => step.uses === './.github/actions/install-browser');
  assert.equal(browser?.if,
    "${{ contains(fromJSON(needs.plan.outputs.web), 'vite-plugin-runic') || contains(fromJSON(needs.plan.outputs.web), 'views') || "
    + "contains(fromJSON(needs.plan.outputs.web), 'angular') || contains(fromJSON(needs.plan.outputs.web), 'react') || "
    + "contains(fromJSON(needs.plan.outputs.web), 'vue') || contains(fromJSON(needs.plan.outputs.web), 'svelte') }}");
  // The Svelte checks and npm framework consumers follow the plan's frameworks output.
  const frameworks = steps.filter(step => step.if?.includes('needs.plan.outputs.frameworks'));
  assert.deepEqual(frameworks.map(step => step.run), ['bun run --bun check', 'bun run --bun check', 'bun tests/web/svelte-package-consumers/package-consumers.mjs']);
  for (const step of frameworks)
    assert.equal(step.if, "${{ !cancelled() && steps.restore.outcome == 'success' && fromJSON(needs.plan.outputs.frameworks) }}");
  // The framework adapters, views-effect, the Vite plugin and SvelteKit also run their tests at their lowest peers when one of them is planned.
  const lowest = steps.find(step => step.run === 'bun tests/web/lowest-peers/lowest-peers.mjs');
  for (const name of ['react', 'vue', 'svelte', 'views-effect', 'vite-plugin-runic', 'sveltekit', 'angular'])
    assert.ok(lowest?.if.includes(`contains(fromJSON(needs.plan.outputs.web), '${name}')`), name);
  assert.equal(lowest.env.RUNIC_LOWEST_PEER_PACKAGES, '${{ needs.plan.outputs.web }}');
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
    'examples/notes-view-first/browser-smoke.mjs', 'examples/notes-view-first/menu-bar-smoke.mjs', 'examples/notes-reactive-views/browser-smoke.mjs',
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

test('every test job scales its condition waits by RUNIC_TEST_TIMEOUT_SCALE for its runner', () => {
  // setup-sdk sets the scale first; the jobs that do not set up the SDK run no tests, except native-musl, which sets it
  // itself and passes it into its container.
  const setup = yaml('.github/actions/setup-sdk/action.yml');
  assert.equal(setup.runs.steps[0].uses, './.github/actions/test-timeout-scale');
  const action = yaml('.github/actions/test-timeout-scale/action.yml');
  assert.deepEqual(action.runs.steps.map(step => step.run), ['node eng/ci/test-timeout-scale.mjs']);
  // A job that calls a reusable workflow (native-libraries) runs no tests itself.
  const scaled = job => job.uses !== undefined || job.steps.some(step => ['./.github/actions/setup-sdk', './.github/actions/test-timeout-scale'].includes(step.uses));
  assert.deepEqual(Object.entries(workflow.jobs).filter(([, job]) => !scaled(job)).map(([name]) => name), ['verify']);
  const musl = workflow.jobs['native-musl'];
  assert.ok(musl.steps.findIndex(step => step.uses === './.github/actions/test-timeout-scale') <
    musl.steps.findIndex(step => step.run?.includes('eng/ci/native-musl.sh')));
  assert.match(musl.steps.find(step => step.run?.includes('eng/ci/native-musl.sh')).run, /-e RUNIC_TEST_TIMEOUT_SCALE /);
  assert.match(workflow.jobs.native.steps.find(step => step.run?.includes('bash eng/ci/real-portal.sh')).run, /-e RUNIC_TEST_TIMEOUT_SCALE /);
  // The variable replaced RUNIC_SMOKE_TIMEOUT_SCALE and the native legs' smoke-timeout-scale.
  assert.doesNotMatch(readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8'), /RUNIC_SMOKE_TIMEOUT_SCALE|smoke-timeout-scale/);
  assert.ok(nativeLegs.every(leg => !('smoke-timeout-scale' in leg)));
  assert.deepEqual(testTimeoutScales, { 'Linux-X64': '4', 'Linux-ARM64': '4', 'Windows-X64': '5', 'Windows-ARM64': '6', 'macOS-ARM64': '6' });
  // Every runner a matrix names has a scale (GitHub's RUNNER_OS and RUNNER_ARCH).
  const runner = os => `${os.startsWith('ubuntu') ? 'Linux' : os.startsWith('windows') ? 'Windows' : 'macOS'}-${/arm|macos/.test(os) ? 'ARM64' : 'X64'}`;
  for (const os of [...nativeLegs, ...wpfLegs].map(leg => leg.os)) assert.ok(testTimeoutScales[runner(os)], os);
});

test('test-timeout-scale picks the runner value unless the job sets a positive one', () => {
  assert.equal(testTimeoutScale('Windows', 'X64', undefined), '5');
  assert.equal(testTimeoutScale('Linux', 'ARM64', ''), '4');
  assert.equal(testTimeoutScale('macOS', 'ARM64', '1.5'), '1.5');
  assert.throws(() => testTimeoutScale('Linux', 'X64', '0'), /must be a positive number, got 0/);
  assert.throws(() => testTimeoutScale('Linux', 'X64', 'fast'), /must be a positive number/);
  assert.throws(() => testTimeoutScale('FreeBSD', 'X64', undefined), /no value for the FreeBSD X64 runner/);
  const directory = mkdtempSync(resolve(tmpdir(), 'runic-ci-scale-'));
  try {
    const environment = resolve(directory, 'env');
    writeFileSync(environment, '');
    execFileSync(process.execPath, [resolve(root, 'eng/ci/test-timeout-scale.mjs')], {
      env: { ...process.env, RUNNER_OS: 'Windows', RUNNER_ARCH: 'ARM64', GITHUB_ENV: environment, RUNIC_TEST_TIMEOUT_SCALE: '' }, stdio: 'pipe' });
    assert.equal(readFileSync(environment, 'utf8'), 'RUNIC_TEST_TIMEOUT_SCALE=6\n');
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

test('template lanes cover every framework once and run the creator check in one lane', () => {
  const { strategy, steps } = workflow.jobs.templates;
  assert.deepEqual(strategy.matrix.include, [
    { first: 'react', second: 'svelte', creator: '1' },
    { first: 'vue', second: 'angular', 'desktop-smoke': '1' },
    { first: 'sveltekit', node: '26.12.1' },
  ]);
  assert.deepEqual(strategy.matrix.include.flatMap(lane => [lane.first, lane.second]).filter(Boolean).sort(),
    ['angular', 'react', 'svelte', 'sveltekit', 'vue']);
  // The starters admit Node 24 and 26 (D031.3): one lane builds them on Node 26, with the pinned package managers.
  const node = steps.findIndex(item => item.uses?.startsWith('actions/setup-node@'));
  assert.equal(steps[node].if, 'matrix.node');
  assert.equal(steps[node].with['node-version'], '${{ matrix.node }}');
  assert.equal(steps[node + 1].if, 'matrix.node');
  assert.match(steps[node + 1].run, /npm install --global npm@\S+ pnpm@\S+/);
  const [step, second] = steps.filter(item => item.run === 'bun run verify:templates');
  assert.equal(step?.name, 'Verify generated ${{ matrix.first }} template applications');
  assert.equal(step?.env.RUNIC_TEMPLATE_FRAMEWORKS, '${{ matrix.first }}');
  assert.equal(step?.env.RUNIC_TEMPLATE_CREATOR, "${{ matrix.creator || '0' }}");
  // The vue step owns the --linux-gtk 4 variant; its GTK 4 smoke needs the runtime installed first.
  assert.equal(step?.env.RUNIC_TEMPLATE_DESKTOP_SMOKE, "${{ matrix.desktop-smoke || '0' }}");
  // The second framework also runs after the first failed, without the creator and desktop smoke.
  assert.equal(second?.name, 'Verify generated ${{ matrix.second }} template applications');
  assert.deepEqual(second?.env, { RUNIC_TEMPLATE_FRAMEWORKS: '${{ matrix.second }}', RUNIC_TEMPLATE_CREATOR: '0', RUNIC_TEMPLATE_DESKTOP_SMOKE: '0' });
  assert.equal(steps.find(item => item.uses?.startsWith('actions/download-artifact@'))?.id, 'candidates');
  assert.equal(second.if, "${{ !cancelled() && steps.candidates.outcome == 'success' && matrix.second }}");
  const install = steps.findIndex(item => item.uses === './.github/actions/apt-install' && item.with.packages.split(' ').includes('libwebkitgtk-6.0-4'));
  assert.ok(install >= 0 && install < steps.indexOf(step));
  assert.ok(node >= 0 && node < steps.indexOf(step));
  assert.equal(steps[install].if, "matrix.desktop-smoke == '1'");
  const sandbox = steps.findIndex(item => item.run === 'eng/ci/permit-webkit-sandbox.sh');
  assert.ok(sandbox > install && sandbox < steps.indexOf(step));
  assert.equal(steps[sandbox].if, "matrix.desktop-smoke == '1'");
  // I-195: every lane requires the Chromium Content-Security-Policy check, so its browser installs first.
  assert.equal(workflow.jobs.templates.env?.RUNIC_TEMPLATE_BROWSER_CHECK, '1');
  const browser = steps.findIndex(item => item.uses === './.github/actions/install-browser');
  assert.ok(browser >= 0 && browser < steps.indexOf(step));
  assert.equal(steps[browser].if, undefined);
  // Each lane verifies only the variants of its framework, so the --linux-gtk 4 (gtk4 flag) variants must belong to
  // the smoke lane's framework, or their smoke would be skipped silently.
  const smokeFrameworks = strategy.matrix.include.filter(item => item['desktop-smoke'] === '1').map(item => item.first);
  const variants = [...readFileSync(resolve(root, 'tests/templates/Test-Templates.sh'), 'utf8')
    .matchAll(/^\s*"(\w+) (\w+) desktop (\w+)( tests)? gtk4"$/gm)];
  assert.ok(variants.length > 0, 'Test-Templates.sh has no --linux-gtk 4 variant.');
  for (const [, framework] of variants) assert.deepEqual(smokeFrameworks, [framework]);
});

test('native and template jobs share the WebKit sandbox permission script', () => {
  const users = Object.entries(workflow.jobs)
    .filter(([, job]) => job.steps?.some(item => item.run === 'eng/ci/permit-webkit-sandbox.sh'))
    .map(([id]) => id);
  assert.deepEqual(users.sort(), ['native', 'templates']);
  assert.ok(!readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8').includes('apparmor_parser'));
});

// I-186/I-187: package managers on hosted runners go through the time-limited paths with mirror fallback and caches
// (eng/ci/README.md, "Ubuntu packages" and "Fedora packages"); an unbounded apt or dnf download has held jobs to their limit.
test('Linux packages install through the bounded, cached paths', () => {
  const files = execFileSync('git', ['ls-files', '-z', '.github'], { cwd: root, encoding: 'utf8' }).split('\0').filter(path => /\.ya?ml$/.test(path));
  for (const path of files) {
    const commands = readFileSync(resolve(root, path), 'utf8').split('\n').filter(line => !/^\s*#/.test(line)).join('\n');
    assert.doesNotMatch(commands, /\bapt-get\s+(update|install)|\bapt\s+install|--with-deps|\bdnf\s+(-\S+\s+)*install/, path);
  }
  const browser = yaml('.github/actions/install-browser/action.yml').runs.steps;
  const apt = browser.find(step => step.uses === './.github/actions/apt-install');
  assert.deepEqual(apt?.with, { packages: '${{ steps.deps.outputs.packages }}', 'install-recommends': 'false' });
  const flatpak = workflow.jobs.packaging.steps.find(step => step.name === 'Install flatpak-builder');
  assert.equal(flatpak?.uses, './.github/actions/apt-install');
  // The real GNOME portal check reuses the dnf cache, and the step keeps its limit rather than growing it.
  const native = workflow.jobs.native.steps;
  const portal = native.findIndex(step => step.run?.includes('bash eng/ci/real-portal.sh'));
  assert.ok(portal >= 0);
  assert.match(native[portal].run, /-v \/var\/cache\/runic-dnf:\/var\/cache\/libdnf5 /);
  assert.equal(native[portal]['timeout-minutes'], 10);
  assert.equal(native[portal - 1].with?.path, '/var/cache/runic-dnf');
  assert.match(readFileSync(resolve(root, 'eng/ci/real-portal.sh'), 'utf8'), /retry\.sh" 1 "\$limit" "\$\{dnf\[@\]\}" install --downloadonly/);
});

// The repository's cache quota is shared: pull request entries evicted main's caches. Only main saves.
test('dependency caches restore everywhere and save only on main', () => {
  const files = execFileSync('git', ['ls-files', '-z', '.github'], { cwd: root, encoding: 'utf8' }).split('\0').filter(path => /\.ya?ml$/.test(path));
  const users = files.filter(path => /uses:\s*actions\/cache(\/\w+)?@/.test(readFileSync(resolve(root, path), 'utf8')));
  assert.deepEqual(users, ['.github/actions/dependency-cache/action.yml']);
  const [save, restore] = yaml('.github/actions/dependency-cache/action.yml').runs.steps;
  assert.match(save.uses, /^actions\/cache@/);
  // A lookup (the native server's library jobs run only on a miss) never saves.
  assert.equal(save.if, "github.ref == 'refs/heads/main' && inputs.lookup-only != 'true'");
  assert.match(restore.uses, /^actions\/cache\/restore@/);
  assert.equal(restore.if, "github.ref != 'refs/heads/main' || inputs.lookup-only == 'true'");
  assert.equal(save.uses.split('@')[1], restore.uses.split('@')[1]);
  const shared = { path: '${{ inputs.path }}', key: '${{ inputs.key }}', 'restore-keys': '${{ inputs.restore-keys }}',
    enableCrossOsArchive: '${{ inputs.enable-cross-os-archive }}' };
  assert.deepEqual(save.with, shared);
  assert.deepEqual(restore.with, { ...shared, 'lookup-only': '${{ inputs.lookup-only }}' });
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

test('an upload that only keeps diagnostics cannot fail a job, one a later job downloads still does', () => {
  // sdk#779's queue run failed only because an upload of passing tests' diagnostics met ETIMEDOUT.
  const files = execFileSync('git', ['ls-files', '-z', '.github'], { cwd: root, encoding: 'utf8' }).split('\0')
    .filter(path => /\.ya?ml$/.test(path));
  const steps = files.flatMap(path => {
    const document = yaml(path);
    return [...Object.values(document.jobs ?? {}).flatMap(job => job.steps ?? []), ...(document.runs?.steps ?? [])]
      .map(step => ({ path, step }));
  });
  const downloaded = steps.filter(({ step }) => step.uses?.startsWith('actions/download-artifact@'))
    // The release downloads CI's packages by artifact id; CI's own jobs download them by name.
    .map(({ step }) => step.with?.name ?? step.with?.pattern).filter(Boolean);
  const consumed = name => downloaded.some(wanted =>
    new RegExp(`^${wanted.replace(/[.+?^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*')}$`).test(name)
    || wanted.replace(/\$\{\{[^}]*\}\}/g, '') === name.replace(/\$\{\{[^}]*\}\}/g, ''));
  const uploads = steps.filter(({ step }) => step.uses?.startsWith('actions/upload-artifact@'));
  assert.ok(uploads.length > 10);
  for (const { path, step } of uploads) {
    // The CI pass marker is read through the API (the plan job) and only lets a later run skip work.
    const kept = consumed(step.with.name) && !step.with.name.startsWith('ci-passed-');
    assert.equal(step['continue-on-error'], kept ? undefined : true, `${path}: ${step.with.name}`);
  }
  for (const name of ['sdk-build-${{ github.run_id }}', 'runic-sdk-${{ github.run_id }}', 'runic-server-${{ matrix.rid }}',
    'runic-desktop-native-server-package-${{ github.run_id }}', 'release-candidate-${{ github.run_id }}'])
    assert.ok(uploads.some(({ step }) => step.with.name === name && step['continue-on-error'] === undefined), name);
});

test('package checks without a lockfile resolve third-party npm packages published at least a day ago', () => {
  // D038: sdk#772's queue run met devframe@1.3.0, published 3 s after its install started.
  const scripts = execFileSync('git', ['ls-files', '-z', '*.mjs'], { cwd: root, encoding: 'utf8' }).split('\0')
    .filter(path => path && !path.startsWith('native/') && path !== 'eng/ci/contracts.test.mjs');
  const unlocked = scripts.filter(path => readFileSync(resolve(root, path), 'utf8').includes('"--package-lock=false"'));
  assert.deepEqual(unlocked.sort(), ['eng/verify-packages.mjs', 'packages/web/vite-plugin-runic/test/package-consumer.test.mjs',
    'tests/fixtures/application/operations-consumer/package-smoke.mjs', 'tests/web/angular-package-consumer/test-package-consumer.mjs',
    'tests/web/svelte-package-consumers/package-consumers.mjs']);
  for (const path of unlocked) {
    const text = readFileSync(resolve(root, path), 'utf8');
    const installs = text.split('"--package-lock=false"').length - 1;
    assert.equal(text.split('...npmReleaseAgeArguments()').length - 1, installs, path);
  }
  const now = Date.parse('2026-10-31T12:00:00Z');
  assert.deepEqual(npmReleaseAgeArguments(now), ['--before=2026-10-30T12:00:00.000Z', '--min-release-age-exclude=@runic-artifex/*']);
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

// The merge queue (eng/ci/README.md, "Merge queue"): `verify` is the ruleset's required check, so it
// must report on merge_group runs, and queue entries must not cancel each other or pull request runs.
test('merge queue entries run CI in their own concurrency groups and report verify', () => {
  assert.deepEqual(workflow.on.merge_group, { types: ['checks_requested'] });
  // Evaluate the concurrency expressions; GitHub's ==, && and || behave like JavaScript's here.
  const evaluate = (expression, github) => expression.replace(/\$\{\{(.*?)\}\}/g, (_, body) =>
    String(Function('github', `return (${body.replaceAll('==', '===')});`)(github)));
  const run = (event_name, ref, run_id) => ({ workflow: 'SDK CI', event_name, ref, run_id });
  const runs = [run('merge_group', 'refs/heads/gh-readonly-queue/main/pr-7-aaaa', 1),
    run('merge_group', 'refs/heads/gh-readonly-queue/main/pr-8-bbbb', 2), run('pull_request', 'refs/pull/7/merge', 3),
    run('push', 'refs/heads/main', 4), run('schedule', 'refs/heads/main', 5)];
  const groups = runs.map(github => evaluate(workflow.concurrency.group, github));
  assert.equal(new Set(groups).size, runs.length, groups.join('\n'));
  assert.equal(groups[0], 'sdk-ci-SDK CI-refs/heads/gh-readonly-queue/main/pr-7-aaaa');
  assert.deepEqual(runs.map(github => evaluate(workflow.concurrency['cancel-in-progress'], github)),
    ['false', 'false', 'true', 'false', 'false']);
  // No job or step is limited to other events, so a queue entry runs the same jobs and verify reports. Only
  // verify's marker of a passed pull request run, after its gate, is pull_request-only ("Merge queue", Reuse).
  const gate = workflow.jobs.verify.steps.findIndex(step => step.run === 'bun eng/ci/gate.mjs');
  const markers = workflow.jobs.verify.steps.slice(gate + 1);
  assert.deepEqual(markers.map(step => step.name), ['Mark the tested base and tree as passed', 'Upload the passed marker']);
  for (const [name, job] of Object.entries(workflow.jobs)) {
    assert.doesNotMatch(String(job.if ?? ''), /event_name|github\.ref/, name);
    for (const step of job.steps ?? []) if (!markers.includes(step))
      assert.doesNotMatch(String(step.if ?? ''), /event_name|github\.ref/, `${name}: ${step.name}`);
  }
  assert.equal(workflow.jobs.verify.name, 'verify');
  assert.equal(workflow.jobs.verify.if, 'always()');
});
