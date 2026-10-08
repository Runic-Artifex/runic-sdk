import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, relative, resolve } from 'node:path';
import { root, workspace, engineeringOnly, engineeringTestDirectories } from '../run.mjs';
import { plan, skippableJobs, managedGroups, managedTests, webTests, outputs, affected, parseAffected } from './plan.mjs';
import { incomplete } from './gate.mjs';

const workflow = Bun.YAML.parse(readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8'));
const tracked = (...patterns) => execFileSync('git', ['ls-files', ...patterns], { cwd: root, encoding: 'utf8' }).trim().split('\n').filter(Boolean);
const runs = (result, job) => !result.skip.includes(job);
const everything = plan(null);
const cli = (args, input = '') => {
  const result = spawnSync(process.execPath, [resolve(root, 'eng/ci/plan.mjs'), ...args], { cwd: root, input, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  return result.stdout;
};

test('pushes and unknown diffs plan every job', () => {
  assert.equal(everything.full, true);
  assert.deepEqual(everything.skip, []);
  assert.deepEqual(everything.managed, managedGroups);
  assert.deepEqual(everything.web.include, webTests());
  assert.deepEqual(everything.components.sort(), Object.keys(workspace.components).sort());
});

test('documentation-only pull requests skip every skippable job', () => {
  for (const files of [['docs/README.md'], ['README.md', 'CONTRIBUTING.md'], ['specs/desktop/README.md'],
    ['eng/release/notes/0.6.0-preview.1.md'], ['examples/first-window/README.md'], ['examples/README.md'], ['tests/engineering/markdown-links.test.mjs'],
    ['eng/ci/plan.test.mjs'], ['.github/workflows/dynamicdata.yml']]) {
    const result = plan(files);
    assert.deepEqual(result.components, [], files.join());
    assert.deepEqual(result.skip, skippableJobs, files.join());
    assert.deepEqual(result.managed, []);
    assert.deepEqual(result.web, { include: [] });
  }
});

test('an empty or failed diff plans every job', () => {
  assert.equal(affected([]).full, true);
  assert.deepEqual(plan([]).skip, []);
  assert.equal(JSON.parse(cli(['--affected'], '')).full, true);
  const step = workflow.jobs.plan.steps.find(item => item.id === 'plan');
  // GitHub runs `shell: bash` as bash -eo pipefail; the default bash shell has no pipefail.
  assert.equal(step.shell, 'bash');
  assert.match(step.run, /affected=\$\(git diff [^|]+\| bun /);
});

test('shared or unowned inputs run everything', () => {
  for (const file of ['Directory.Build.props', 'Directory.Packages.props', 'global.json', 'bun.lock', 'eng/run.mjs', 'eng/ci/plan.mjs',
    'eng/build/nuget-lock.targets', 'eng/build/application.props', 'eng/build/application.targets', 'eng/dependencies/audit.test.mjs', 'eng/ci/fixtures/artifact-roundtrip.yml', '.github/workflows/ci.yml', '.github/actions/setup-sdk/action.yml', 'flake.nix', 'LICENSE']) {
    const result = plan([file, 'docs/README.md']);
    assert.deepEqual(result.skip, [], file);
    assert.deepEqual(result.managed, managedGroups, file);
    assert.deepEqual(result.web.include, webTests(), file);
  }
});

test('package READMEs and fixtures are build inputs, not documentation', () => {
  for (const file of ['packages/web/views/README.md', 'packages/dotnet/Runic.Desktop/README.md',
    'tools/Runic.Application.Templates/content/runic-app/README.md', 'tests/fixtures/application/experiments/notes.md'])
    assert.notDeepEqual(plan([file]).skip, skippableJobs, file);
});

test('a Views runtime change runs its consumers, packages, templates and native apps', () => {
  const result = plan(['packages/web/views/src/index.ts']);
  for (const job of ['web', 'framework-consumers', 'views', 'packages', 'package-consumers', 'templates', 'native'])
    assert.ok(runs(result, job), job);
  assert.ok(result.managed.includes('application'));
  assert.ok(!result.managed.includes('assets'));
  assert.ok(result.web.include.some(item => item.package === 'views'));
});

test('a desktop change runs native checks and every dependent component', () => {
  const result = plan(['packages/dotnet/Runic.Desktop/DesktopSurface.cs']);
  // The navigation engine stays independent, but the hybrid WPF host consumes Desktop.
  assert.deepEqual(result.skip, []);
  assert.deepEqual(result.managed, managedGroups.filter(group => group !== 'navigation'));
});

test('template and Svelte changes run their lanes without unrelated suites', () => {
  const templates = plan(['tests/templates/Test-Templates.sh']);
  for (const job of ['packages', 'package-consumers', 'templates']) assert.ok(runs(templates, job), job);
  for (const job of ['web', 'framework-consumers', 'views', 'native']) assert.ok(!runs(templates, job), job);
  assert.deepEqual(templates.managed, ['platform'], 'only the group holding Runic.Create.Tests');

  const svelte = plan(['packages/web/svelte/src/index.ts']);
  for (const job of ['web', 'framework-consumers', 'views', 'templates', 'native']) assert.ok(runs(svelte, job), job);
  assert.deepEqual(svelte.web.include.map(item => item.package).sort(), ['svelte', 'sveltekit']);
  assert.deepEqual(svelte.managed, ['platform'], 'templates holds Runic.Create.Tests');
});

test('Windows administration changes run native checks', () => {
  const result = plan(['packages/dotnet/Runic.Platform.Administration.Windows/Inspection.cs']);
  assert.ok(runs(result, 'native'));
  assert.ok(runs(result, 'packages'));
  assert.ok(!runs(result, 'views'));
});

test('the template definition the guided creator embeds runs the creator tests', () => {
  const result = plan(['tools/Runic.Application.Templates/content/runic-app/.template.config/template.json']);
  assert.ok(result.components.includes('templates'));
  assert.ok(result.managed.includes('platform'), 'Runic.Create.Tests runs in the platform managed group');
  assert.ok(runs(result, 'templates'));
});

test('the engineering-only rule matches the tests the engineering job runs', () => {
  const step = workflow.jobs.engineering.steps.find(item => item.name === 'Verify workspace, runtime and CI contracts');
  assert.equal(step.run, `bun test --timeout 180000 ${engineeringTestDirectories.map(directory => `./${directory}/*.test.mjs`).join(' ')}`);
  for (const directory of engineeringTestDirectories) assert.ok(engineeringOnly(`${directory}/example.test.mjs`), directory);
  for (const file of ['eng/dependencies/audit.test.mjs', 'tests/engineering/fixtures/input.json', 'eng/ci/plan.mjs', '.github/workflows/ci.yml'])
    assert.ok(!engineeringOnly(file), file);
});

test('the base checkout decides what is affected and this checkout enumerates suites', () => {
  const decided = JSON.parse(cli(['--affected'], 'docs/README.md\0packages/web/svelte/src/index.ts\0'));
  assert.deepEqual(decided, affected(['docs/README.md', 'packages/web/svelte/src/index.ts']));
  assert.deepEqual(decided.components.sort(), ['examples', 'svelte', 'templates']);
  assert.equal(JSON.parse(cli(['--affected'], 'eng/ci/plan.mjs\0')).full, true);
  const planned = Object.fromEntries(cli(['--plan', JSON.stringify(decided)]).trim().split('\n').map(line => [line.slice(0, line.indexOf('=')), JSON.parse(line.slice(line.indexOf('=') + 1))]));
  assert.deepEqual(planned.skip, plan(['packages/web/svelte/src/index.ts']).skip);
  // An older base planner prints GitHub outputs instead; plan everything.
  for (const text of ['web={"include":[]}', '{"full":false,"components":["renamed"]}', ''])
    assert.equal(parseAffected(text), null, text);
  assert.match(cli(['--plan', 'web={"include":[]}']), /^full=true$/m);
  assert.match(cli([]), /^skip=\[\]$/m);
});

test('plan outputs are single-line GitHub outputs', () => {
  const lines = outputs(plan(['docs/README.md'])).split('\n');
  assert.deepEqual(lines.map(line => line.split('=')[0]), ['full', 'components', 'managed', 'web', 'skip']);
  for (const line of lines) JSON.parse(line.slice(line.indexOf('=') + 1));
});

test('the workflow gates exactly the skippable jobs on the plan and verify aggregates all of them', () => {
  const jobs = Object.keys(workflow.jobs);
  for (const job of skippableJobs) {
    assert.ok(workflow.jobs[job], job);
    assert.equal(workflow.jobs[job].if, `\${{ !contains(fromJSON(needs.plan.outputs.skip), '${job}') }}`, job);
    assert.ok([workflow.jobs[job].needs].flat().includes('plan'), job);
  }
  for (const job of ['plan', 'build', 'engineering']) assert.equal(workflow.jobs[job].if, undefined, job);
  assert.deepEqual([...skippableJobs, 'plan', 'build', 'engineering', 'verify'].sort(), jobs.sort());
  assert.equal(workflow.jobs.verify.if, 'always()');
  assert.deepEqual([...workflow.jobs.verify.needs].sort(), jobs.filter(job => job !== 'verify').sort());
  const gate = workflow.jobs.verify.steps.at(-1);
  assert.equal(gate.run, 'bun eng/ci/gate.mjs');
  assert.equal(gate.env.SKIP, '${{ needs.plan.outputs.skip }}');
  assert.equal(gate.env.NEEDS, '${{ toJSON(needs) }}');
  assert.equal(workflow.on.push.branches[0], 'main');
});

test('verify accepts only planned skips and successes', () => {
  const needs = { plan: { result: 'success' }, build: { result: 'success' }, native: { result: 'skipped' }, views: { result: 'success' } };
  assert.deepEqual(incomplete(needs, ['native']), []);
  assert.deepEqual(incomplete(needs, []), ['native=skipped']);
  assert.deepEqual(incomplete({ ...needs, views: { result: 'failure' } }, ['native', 'views']), ['views=failure']);
  assert.deepEqual(incomplete({ ...needs, views: { result: 'cancelled' } }, ['native']), ['views=cancelled']);
  assert.deepEqual(incomplete({ plan: { result: 'failure' }, native: { result: 'skipped' } }), ['plan=failure', 'native=skipped']);
});

const owners = file => Object.entries(workspace.components)
  .filter(([, component]) => component.paths.some(path => file === path || file.startsWith(`${path}/`))).map(([name]) => name);
const closure = name => {
  const seen = new Set([name]);
  for (const item of seen) for (const dependency of workspace.components[item].dependsOn) seen.add(dependency);
  return seen;
};

test('component dependencies cover every project reference and workspace npm dependency', () => {
  const npm = new Map(workspace.npm.map(item => [item.name, item.path]));
  for (const file of tracked('*.csproj', '*/package.json')) {
    const owner = owners(file);
    assert.equal(owner.length, 1, `${file} is owned by ${owner.join(', ') || 'no component'}`);
    const text = readFileSync(resolve(root, file), 'utf8');
    const references = file.endsWith('.csproj')
      ? [...text.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)].map(([, path]) => relative(root, resolve(root, dirname(file), path.replaceAll('\\', '/'))))
      : Object.keys({ ...JSON.parse(text).dependencies, ...JSON.parse(text).devDependencies, ...JSON.parse(text).peerDependencies })
        .filter(name => npm.has(name)).map(name => `${npm.get(name)}/package.json`);
    const reachable = closure(owner[0]);
    for (const reference of references) {
      const [target] = owners(reference);
      assert.ok(target && reachable.has(target), `${file} (${owner[0]}) references ${reference} (${target}); add it to dependsOn`);
    }
  }
});

// Item transforms and paths built from these properties name restored packages,
// generated output, pack staging or an external checkout (DynamicData fork,
// CsWebUi), not another component's source.
const unresolvable = /^(?:@\(|\$\((?:NuGetPackageRoot|DynamicDataForkRoot|CsWebUiRepository|RunicPolicyIcon|TemplatePackContentRoot|_?RunicBridge\w*|_?RunicAssets\w*)\))/;

test('component dependencies cover MSBuild imports and cross-directory items', () => {
  for (const file of tracked('*.csproj', '*.props', '*.targets')) {
    const [owner] = owners(file);
    // A change to an unowned file already runs everything. Its imports of owned
    // files reach projects through their own references (eng/desktop-targets.test.mjs).
    if (!owner) continue;
    const text = readFileSync(resolve(root, file), 'utf8');
    const values = [...text.matchAll(/<(?:Import\s+Project|(?:EmbeddedResource|Compile|None|Content|AdditionalFiles)\s+Include)="([^"]+)"/g)]
      .flatMap(([, value]) => value.split(';')).map(value => value.trim()).filter(Boolean);
    for (const value of values) {
      const path = value.replaceAll('\\', '/')
        .replace(/\$\((?:MSBuildThisFileDirectory|MSBuildProjectDirectory)\)\/?/g, './')
        .replace(/\$\(RunicSdkRoot\)\/?/g, `${relative(dirname(resolve(root, file)), root) || '.'}/`)
        .replace(/\$\(Configuration\)/g, 'Release');
      if (/[$@]\(/.test(path)) {
        assert.match(path, unresolvable, `${file}: resolve ${value} or allow-list its property`);
        continue;
      }
      const target = relative(root, resolve(root, dirname(file), path.split('*')[0]));
      const [component] = owners(target);
      if (target.startsWith('..') || !component) continue;
      assert.ok(closure(owner).has(component), `${file} (${owner}) reads ${target} (${component}); add it to dependsOn or make it shared`);
    }
  }
});

test('every core solution project commits a NuGet lock file and no other lock files exist', () => {
  const projects = [...readFileSync(resolve(root, 'RunicSdk.Core.slnx'), 'utf8').matchAll(/<Project Path="([^"]+)"/g)].map(([, path]) => path);
  const shared = new Set(projects.map(dirname).filter((directory, index, all) => all.indexOf(directory) !== index));
  const expected = projects.map(path => shared.has(dirname(path))
    ? `${dirname(path)}/packages.${path.split('/').at(-1).replace(/\.csproj$/, '')}.lock.json`
    : `${dirname(path)}/packages.lock.json`).sort();
  for (const path of expected) assert.ok(existsSync(resolve(root, path)), `${path} is missing; run bun run lock:nuget`);
  assert.deepEqual(tracked('*packages.lock.json', '*packages.*.lock.json').sort(), expected);
});

test('a navigation engine change runs its own group and the application group that depends on it', () => {
  const result = plan(['packages/dotnet/Runic.Navigation/RunicNavigator.cs']);
  assert.ok(result.managed.includes('navigation'));
  assert.ok(result.managed.includes('application'));
  assert.ok(!result.managed.includes('assets'));
  assert.ok(runs(result, 'package-consumers'));
});

test('the standalone navigation consumer belongs to the navigation component', () => {
  const result = plan(['tests/fixtures/navigation/standalone-consumer/Program.cs']);
  assert.ok(result.managed.includes('navigation'));
  assert.ok(runs(result, 'package-consumers'));
});

test('the ReactiveUI navigation adapters belong to the navigation component', () => {
  for (const file of [
    'packages/dotnet/Runic.Navigation.ReactiveUI/ReactiveNavigation.cs',
    'packages/dotnet/Runic.Navigation.ReactiveUI.Reactive/ReactiveModelContextScheduler.cs',
    'tests/fixtures/navigation/reactiveui-adapter/Program.cs',
    'tests/fixtures/navigation/reactiveui-consumer/Program.cs',
  ]) {
    const result = plan([file]);
    assert.ok(result.managed.includes('navigation'), file);
    assert.ok(result.managed.includes('application'), file);
    assert.ok(!result.managed.includes('assets'), file);
    assert.ok(runs(result, 'package-consumers'), file);
  }
});

test('the navigation group runs the adapter tests of both ReactiveUI flavors', () => {
  const tests = managedTests().filter(test => test.group === 'navigation').map(test => test.path);
  assert.ok(tests.some(path => path.endsWith('Runic.Navigation.ReactiveUI.Tests.csproj')));
  assert.ok(tests.some(path => path.endsWith('Runic.Navigation.ReactiveUI.Reactive.Tests.csproj')));
});

test('navigation changes run the Windows WPF lane', () => {
  for (const file of [
    'packages/dotnet/Runic.Navigation.Wpf/NavigationHost.cs',
    'tests/dotnet/Runic.Navigation.Wpf.Tests/Program.cs',
    'tests/fixtures/navigation/wpf-consumer/package-smoke.mjs',
    'examples/wpf-navigation/NotesNavigation/Notes/NoteDetailViewModel.cs',
    'packages/dotnet/Runic.Navigation/RunicNavigator.cs',
  ]) {
    const result = plan([file]);
    assert.ok(runs(result, 'wpf'), file);
    assert.ok(runs(result, 'packages'), file);
    assert.ok(result.managed.includes('navigation'), file);
  }
  assert.ok(!runs(plan(['tests/templates/Test-Templates.sh']), 'wpf'));
});

test('hybrid WPF consumers run when their host or shared runtime changes', () => {
  for (const file of [
    'packages/dotnet/Runic.Application.Wpf/RunicWebView.cs',
    'tests/dotnet/Runic.Application.Wpf.Tests/Program.cs',
    'examples/wpf-hybrid-editor/Model/EditorViewModel.cs',
    'packages/dotnet/Runic.Desktop/DesktopSurface.cs',
    'packages/dotnet/Runic.Application.Views/WindowContentSession.cs',
    'packages/web/views/src/index.ts',
  ]) assert.ok(runs(plan([file]), 'wpf'), file);
});

test('the WPF test runner is Windows-only and outside the Linux managed groups', () => {
  const path = 'tests/dotnet/Runic.Navigation.Wpf.Tests/Runic.Navigation.Wpf.Tests.csproj';
  assert.ok(!managedTests(root, 'linux').some(test => test.path === path));
  assert.ok(managedTests(root, 'win32').some(test => test.path === path && test.group === 'navigation'));
});

test('the hybrid host has portable session checks and a Windows native runner', () => {
  const portable = 'tests/dotnet/Runic.Application.Wpf.Tests/Runic.Application.Wpf.Tests.csproj';
  const windows = 'tests/dotnet/Runic.Application.Wpf.Windows.Tests/Runic.Application.Wpf.Windows.Tests.csproj';
  assert.ok(managedTests(root, 'linux').some(test => test.path === portable && test.group === 'application'));
  assert.ok(!managedTests(root, 'linux').some(test => test.path === windows));
  assert.ok(managedTests(root, 'win32').some(test => test.path === windows && test.group === 'application'));
  assert.ok(workflow.jobs.wpf.steps.some(step => step.run === 'dotnet run --project tests/dotnet/Runic.Application.Wpf.Windows.Tests -c Release'));
});

test('the WPF navigation example builds from the packed packages in the WPF lane', () => {
  const path = 'examples/wpf-navigation/Tests/NotesNavigation.Tests.csproj';
  assert.ok(!managedTests(root, 'linux').some(test => test.path === path));
  assert.ok(managedTests(root, 'win32').some(test => test.path === path && test.group === 'navigation'));
  assert.ok(workflow.jobs.wpf.steps.some(step => step.run === 'bun examples/wpf-navigation/package-smoke.mjs'));
});
