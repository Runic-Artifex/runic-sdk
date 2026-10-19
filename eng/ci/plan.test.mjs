import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, relative, resolve } from 'node:path';
import { root, workspace, engineeringOnly, engineeringTestDirectories } from '../run.mjs';
import { plan, planFor, skippableJobs, managedGroups, managedTests, webTests, outputs, affected, parseAffected, testReportArguments,
  everyLinuxJob, allPlatforms } from './plan.mjs';
import { platforms, allPlatformsLabel, nativeLegs, wpfLegs, packagingLegs, platformRules, neutralPaths } from './platforms.mjs';
import { incomplete, platformSummary } from './gate.mjs';

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
  assert.deepEqual(everything.web, webTests().map(item => item.package));
  assert.equal(everything.frameworks, true);
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
    assert.deepEqual(result.web, []);
    assert.equal(result.frameworks, false);
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
    // No Windows or macOS job reads the flake, so it plans every Linux job and leg; WPF has no Linux leg.
    const neutral = file === 'flake.nix';
    assert.deepEqual(result.skip, neutral ? ['wpf'] : [], file);
    assert.deepEqual(result.platforms, neutral ? [] : platforms, file);
    assert.deepEqual(result.managed, managedGroups, file);
    assert.deepEqual(result.web, webTests().map(item => item.package), file);
  }
});

test('package READMEs and fixtures are build inputs, not documentation', () => {
  for (const file of ['packages/web/views/README.md', 'packages/dotnet/Runic.Desktop/README.md',
    'tools/Runic.Application.Templates/content/runic-app/README.md', 'tests/fixtures/application/experiments/notes.md'])
    assert.notDeepEqual(plan([file]).skip, skippableJobs, file);
});

test('a Views runtime change runs its consumers, packages, templates and native apps', () => {
  const result = plan(['packages/web/views/src/index.ts']);
  for (const job of ['web', 'views', 'package-consumers', 'templates', 'native'])
    assert.ok(runs(result, job), job);
  assert.ok(result.frameworks);
  assert.ok(result.managed.includes('application'));
  assert.ok(!result.managed.includes('assets'));
  assert.ok(result.web.includes('views'));
});

test('a desktop change runs native checks and every dependent component', () => {
  const result = plan(['packages/dotnet/Runic.Desktop/DesktopSurface.cs']);
  // The navigation engine stays independent, but the hybrid WPF host consumes Desktop.
  assert.deepEqual(result.skip, []);
  assert.deepEqual(result.managed, managedGroups.filter(group => group !== 'navigation'));
});

test('template and Svelte changes run their lanes without unrelated suites', () => {
  const templates = plan(['tests/templates/Test-Templates.sh']);
  for (const job of ['package-consumers', 'templates']) assert.ok(runs(templates, job), job);
  for (const job of ['web', 'views', 'native']) assert.ok(!runs(templates, job), job);
  assert.equal(templates.frameworks, false);
  assert.deepEqual(templates.managed, ['platform'], 'only the group holding Runic.Create.Tests');

  const svelte = plan(['packages/web/svelte/src/index.ts']);
  for (const job of ['web', 'views', 'templates', 'native']) assert.ok(runs(svelte, job), job);
  assert.ok(svelte.frameworks);
  assert.deepEqual(svelte.web.sort(), ['svelte', 'sveltekit']);
  assert.deepEqual(svelte.managed, ['platform'], 'templates holds Runic.Create.Tests');
});

test('the packaging job runs for the packaging recipes, their probe and the platform and Views packages only', () => {
  for (const file of ['tools/Runic.Application.Templates/content/runic-app/packaging/package.mjs',
    'tools/Runic.Application.Templates/content/runic-app/ApplicationIdentity.cs', 'tests/native/Runic.Packaging.Probe/Program.cs',
    'packages/dotnet/Runic.Platform.Windows/WindowsPlatformProvider.cs', 'packages/dotnet/Runic.Desktop/DesktopSurface.cs',
    'packages/dotnet/Runic.Application.Views/RunicWindowHost.cs']) {
    const result = plan([file]);
    assert.ok(runs(result, 'packaging'), file);
  }
  const packagingOnly = plan(['tools/Runic.Application.Templates/content/runic-app/packaging/linux/flatpak.yml']);
  assert.ok(runs(packagingOnly, 'templates'), 'the recipes ship in the template package');
  for (const file of ['tests/templates/Test-Templates.sh', 'tools/Runic.Application.Templates/content/runic-app/Program.cs',
    'packages/web/svelte/src/index.ts'])
    assert.ok(!runs(plan([file]), 'packaging'), file);
});

test('Windows administration changes run native checks', () => {
  const result = plan(['packages/dotnet/Runic.Platform.Administration.Windows/Inspection.cs']);
  assert.ok(runs(result, 'native'));
  assert.ok(runs(result, 'package-consumers'));
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
  assert.deepEqual(lines.map(line => line.split('=')[0]),
    ['full', 'components', 'managed', 'web', 'frameworks', 'native', 'wpf', 'packaging', 'platforms', 'why', 'skip']);
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
const unresolvable = /^(?:@\(|\$\((?:NuGetPackageRoot|DynamicDataForkRoot|CsWebUiRepository|RunicPolicyIcon|TemplatePackContentRoot|_?RunicBridge\w*|_?RunicAssets\w*|_?RunicPrivileged\w*)\))/;

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
  // The Nix builder's fixtures are independent consumers outside the solution, restored for linux-x64 with their own
  // lock files, which buildRunicApplication reads (nix/tests).
  const independent = path => path.startsWith('tests/fixtures/nix/');
  assert.deepEqual(tracked('*packages.lock.json', '*packages.*.lock.json').filter(path => !independent(path)).sort(), expected);
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
    'packages/dotnet/Runic.Navigation.ReactiveUI.Reactive/Runic.Navigation.ReactiveUI.Reactive.csproj',
    'tests/fixtures/navigation/reactiveui-adapter/NavigationAdapterTests.cs',
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
    'tests/dotnet/Runic.Navigation.Wpf.Tests/HostTests.cs',
    'tests/fixtures/navigation/wpf-consumer/package-smoke.mjs',
    'examples/wpf-navigation/NotesNavigation/Notes/NoteDetailViewModel.cs',
    'packages/dotnet/Runic.Navigation/RunicNavigator.cs',
  ]) {
    const result = plan([file]);
    assert.ok(runs(result, 'wpf'), file);
    assert.ok(runs(result, 'package-consumers'), file);
    assert.ok(result.managed.includes('navigation'), file);
  }
  assert.ok(!runs(plan(['tests/templates/Test-Templates.sh']), 'wpf'));
});

test('hybrid WPF consumers run when their host or shared runtime changes', () => {
  for (const file of [
    'packages/dotnet/Runic.Application.Views.Wpf/RunicWebView.cs',
    'tests/dotnet/Runic.Application.Views.Wpf.Tests/Program.cs',
    'examples/wpf-hybrid-editor/Model/EditorViewModel.cs',
    'packages/dotnet/Runic.Desktop/DesktopSurface.cs',
    'packages/dotnet/Runic.Application.Views/WindowContentSession.cs',
  ]) assert.ok(runs(plan([file]), 'wpf'), file);
  // The web runtime needs no Windows leg of its own; the label, a nightly or a manual run covers WPF.
  const views = affected(['packages/web/views/src/index.ts']);
  assert.ok(!runs(planFor(views), 'wpf'));
  assert.deepEqual(planFor(allPlatforms(views, 'label')).wpf.include, wpfLegs);
});

// A sample file for a rule path: the path itself, or a file in it.
const sample = path => {
  const files = tracked(path).filter(file => !engineeringOnly(file));
  return path.endsWith('*') ? files[0] : files.includes(path) ? path : `${path}/Example.cs`;
};
const legsFor = (list, planned) => list.filter(leg => !leg.platform || planned.includes(leg.platform));

test('each platform rule plans its Windows and macOS legs, first match wins', () => {
  for (const rule of platformRules)
    for (const path of rule.paths) {
      // Every rule names tracked files, so a rename cannot leave a rule silently matching nothing.
      assert.ok(tracked(path.endsWith('*') ? path : `${path}`).length > 0, `${path} matches no tracked file`);
      const file = sample(path);
      const result = plan([file]);
      assert.deepEqual(result.platforms, rule.platforms, file);
      assert.deepEqual(affected([file]).platforms, rule.platforms, file);
      if (runs(result, 'native')) assert.deepEqual(result.native.include, legsFor(nativeLegs, rule.platforms), file);
      if (runs(result, 'wpf')) assert.deepEqual(result.wpf.include, legsFor(wpfLegs, rule.platforms), file);
      if (runs(result, 'packaging')) assert.deepEqual(result.packaging.include, legsFor(packagingLegs, rule.platforms), file);
      for (const platform of rule.platforms) assert.match(result.why, new RegExp(`${platform}: ${file.replaceAll('.', '\\.')}`), file);
    }
  // GTK 4 is listed before Runic.Desktop and Runic.Platform.Linux before nothing broader.
  assert.deepEqual(plan(['packages/dotnet/Runic.Desktop.Gtk4/GtkWindow.cs']).platforms, []);
  assert.deepEqual(plan(['packages/dotnet/Runic.Desktop/DesktopSurface.cs']).platforms, platforms);
  assert.deepEqual(plan(['packages/dotnet/Runic.Platform.Linux.Portal/Portal.cs']).platforms, []);
  assert.deepEqual(plan(['packages/dotnet/Runic.Platform/IPlatform.cs']).platforms, platforms);
  // The union of several files, with the first file that needed each platform.
  const both = plan(['packages/dotnet/Runic.Platform.MacOS/MacOSPlatformProvider.cs', 'packages/dotnet/Runic.Platform.Windows.Elevation/Helper.cs']);
  assert.deepEqual(both.platforms, platforms);
  assert.equal(both.why, 'win-x64: packages/dotnet/Runic.Platform.Windows.Elevation/Helper.cs; win-arm64: packages/dotnet/Runic.Platform.Windows.Elevation/Helper.cs; '
    + 'osx-arm64: packages/dotnet/Runic.Platform.MacOS/MacOSPlatformProvider.cs');
});

test('owned files without a rule, engineering-only and neutral files plan no Windows or macOS leg', () => {
  for (const file of ['packages/web/svelte/src/index.ts', 'packages/web/views/src/index.ts', 'tools/Runic.Application.Templates/content/runic-app/Program.cs',
    'packages/dotnet/Runic.Assets/AssetCatalog.cs', 'packages/dotnet/Runic.Navigation.ReactiveUI/ReactiveNavigation.cs', 'examples/notes-view-first/Program.cs',
    'docs/README.md', 'eng/ci/plan.test.mjs', 'tests/native/Runic.Desktop.Gtk4.Smoke/Program.cs', 'eng/portals/README.txt']) {
    const result = plan([file]);
    assert.deepEqual(result.platforms, [], file);
    assert.equal(result.why, 'no Windows or macOS leg: the changed files need none');
    assert.ok(!runs(result, 'wpf'), file);
    for (const job of ['native', 'packaging']) assert.ok(!result[job].include.some(leg => leg.platform), `${file}: ${job}`);
  }
  // Neutral files are unowned: they plan every Linux job, but no Windows or macOS leg.
  for (const path of neutralPaths) {
    const file = tracked(path).find(item => !engineeringOnly(item)) ?? path;
    const result = plan([file]);
    assert.equal(result.full, true, file);
    assert.deepEqual(result.platforms, [], file);
    assert.deepEqual(result.native.include.map(leg => leg.rid), ['linux-x64', 'linux-arm64'], file);
    assert.deepEqual(result.packaging.include.map(leg => leg.target), ['flatpak'], file);
    assert.deepEqual(result.skip, ['wpf'], file);
  }
  // Any other unowned file plans every leg.
  for (const file of ['global.json', 'eng/ci/plan.mjs', 'eng/support.json', '.github/actions/setup-sdk/action.yml', 'bun.lock', 'eng/workspace.json']) {
    assert.deepEqual(plan([file]).platforms, platforms, file);
    assert.match(plan([file]).why, new RegExp(`^win-x64: ${file.replaceAll('.', '\\.')};`), file);
  }
});

test('pushes keep their platforms, and the label, schedules and manual runs plan every leg', () => {
  const decided = affected(['packages/dotnet/Runic.Platform.MacOS/MacOSPlatformProvider.cs']);
  const push = planFor(everyLinuxJob(decided));
  assert.equal(push.full, true);
  assert.deepEqual(push.managed, managedGroups);
  assert.deepEqual(push.platforms, ['osx-arm64']);
  assert.deepEqual(push.native.include.map(leg => leg.rid), ['linux-x64', 'linux-arm64', 'osx-arm64']);
  assert.deepEqual(push.skip, ['wpf']);
  const labelled = planFor(allPlatforms(decided, `label ${allPlatformsLabel}`));
  assert.deepEqual(labelled.platforms, platforms);
  assert.deepEqual(labelled.native.include, nativeLegs);
  assert.equal(labelled.why, platforms.map(platform => `${platform}: label ci:all-platforms`).join('; '));
  assert.equal(labelled.full, false, 'the label adds legs, not Linux jobs');
  const outputsOf = text => Object.fromEntries(text.trim().split('\n').map(line => [line.slice(0, line.indexOf('=')), JSON.parse(line.slice(line.indexOf('=') + 1))]));
  const cliPush = outputsOf(cli(['--plan', JSON.stringify(decided), '--all-linux']));
  assert.deepEqual([cliPush.full, cliPush.platforms, cliPush.skip], [true, ['osx-arm64'], ['wpf']]);
  const cliLabel = outputsOf(cli(['--plan', JSON.stringify(decided), '--all-platforms', 'label ci:all-platforms']));
  assert.deepEqual([cliLabel.full, cliLabel.platforms, cliLabel.wpf], [false, platforms, { include: wpfLegs }]);
  const scheduled = outputsOf(cli(['--all', 'schedule run']));
  assert.deepEqual([scheduled.full, scheduled.platforms, scheduled.skip], [true, platforms, []]);
  assert.match(scheduled.why, /^win-x64: schedule run;/);
  assert.deepEqual(outputsOf(cli([])).native, { include: nativeLegs });
});

test('a base planner without platforms plans every leg, and invalid platforms are ignored', () => {
  const decided = affected(['packages/web/svelte/src/index.ts']);
  assert.deepEqual(parseAffected(JSON.stringify(decided)), decided);
  for (const older of [{ full: decided.full, components: decided.components },
    { ...decided, platforms: ['win-x64', 'linux-riscv64'] }, { ...decided, platforms: ['win-x64'], why: {} }]) {
    const parsed = parseAffected(JSON.stringify(older));
    assert.deepEqual(parsed, { full: decided.full, components: decided.components });
    const result = planFor(parsed);
    assert.deepEqual(result.platforms, platforms);
    assert.match(result.why, /the base planner plans no platforms/);
    assert.deepEqual(result.native.include, nativeLegs);
  }
});

test('the workflow plans platform legs, reads the label live and runs everything nightly', () => {
  const { plan: job } = workflow.jobs;
  for (const name of ['native', 'wpf', 'packaging']) {
    assert.equal(workflow.jobs[name].strategy.matrix, `\${{ fromJSON(needs.plan.outputs.${name}) }}`, name);
    assert.equal(job.outputs[name], `\${{ steps.plan.outputs.${name} }}`, name);
  }
  for (const name of ['platforms', 'why']) assert.equal(job.outputs[name], `\${{ steps.plan.outputs.${name} }}`, name);
  assert.deepEqual(job.permissions, { contents: 'read', 'pull-requests': 'read' });
  const step = job.steps.find(item => item.id === 'plan');
  assert.equal(step.env.LABEL, allPlatformsLabel);
  assert.equal(step.env.BEFORE, '${{ github.event.before }}');
  assert.match(step.run, /gh api "repos\/\$GITHUB_REPOSITORY\/issues\/\$PULL_REQUEST\/labels"/);
  assert.match(step.run, /options=\(--all-platforms "the pull request's labels could not be read"\)/);
  assert.match(step.run, /options=\(--all-linux\)/);
  assert.match(step.run, /bun eng\/ci\/plan\.mjs --plan "\$affected" "\$\{options\[@\]\}"/);
  assert.match(step.run, /bun eng\/ci\/plan\.mjs --all "\$reason"/);
  // A label is read when the plan runs; adding one does not start a run by itself.
  assert.equal(workflow.on.pull_request, null);
  assert.equal(workflow.on.schedule.length, 1);
  assert.ok(Object.hasOwn(workflow.on, 'workflow_dispatch'));
  assert.equal(workflow.concurrency['cancel-in-progress'], "${{ github.event_name == 'pull_request' }}");
  assert.match(workflow.concurrency.group, /github\.run_id/);
  const gate = workflow.jobs.verify.steps.at(-1);
  assert.equal(gate.env.PLATFORMS, '${{ needs.plan.outputs.platforms }}');
  assert.equal(gate.env.WHY, '${{ needs.plan.outputs.why }}');
  assert.equal(platformSummary(['win-x64'], 'win-x64: a.cs'), 'Windows and macOS legs: win-x64 (win-x64: a.cs)');
  assert.equal(platformSummary([], ''), 'Windows and macOS legs: none (the plan gave no reason)');
});

test('the WPF test runner is Windows-only and outside the Linux managed groups', () => {
  const path = 'tests/dotnet/Runic.Navigation.Wpf.Tests/Runic.Navigation.Wpf.Tests.csproj';
  assert.ok(!managedTests(root, 'linux').some(test => test.path === path));
  assert.ok(managedTests(root, 'win32').some(test => test.path === path && test.group === 'navigation'));
});

test('the hybrid host has portable session checks and a Windows native runner', () => {
  const portable = 'tests/dotnet/Runic.Application.Views.Wpf.Tests/Runic.Application.Views.Wpf.Tests.csproj';
  const windows = 'tests/dotnet/Runic.Application.Views.Wpf.Windows.Tests/Runic.Application.Views.Wpf.Windows.Tests.csproj';
  assert.ok(managedTests(root, 'linux').some(test => test.path === portable && test.group === 'application'));
  assert.ok(!managedTests(root, 'linux').some(test => test.path === windows));
  assert.ok(managedTests(root, 'win32').some(test => test.path === windows && test.group === 'application'));
  assert.ok(workflow.jobs.wpf.steps.some(step => step.run === 'dotnet run --project tests/dotnet/Runic.Application.Views.Wpf.Windows.Tests -c Release'));
});

test('the WPF navigation example builds from the packed packages in the WPF lane', () => {
  const path = 'examples/wpf-navigation/Tests/NotesNavigation.Tests.csproj';
  assert.ok(!managedTests(root, 'linux').some(test => test.path === path));
  assert.ok(managedTests(root, 'win32').some(test => test.path === path && test.group === 'navigation'));
  assert.ok(workflow.jobs.wpf.steps.some(step => step.run === 'bun examples/wpf-navigation/package-smoke.mjs'));
});

test('TUnit suites write TRX and Cobertura reports; other executables and listings take no report options', () => {
  const base = mkdtempSync(resolve(tmpdir(), 'runic-test-reports-'));
  try {
    for (const [name, reference] of [['Tunit.Tests', '<PackageReference Include="TUnit.Engine" />'], ['Example.Tests', '<PackageReference Include="TUnit" />'],
      ['Probe.Tests', ''], ['Other.Tests', '<PackageReference Include="TUnit.Assertions" />']]) {
      mkdirSync(resolve(base, name));
      writeFileSync(resolve(base, name, `${name}.csproj`), `<Project><ItemGroup>${reference}</ItemGroup></Project>`);
    }
    const reports = testReportArguments('Tunit.Tests/Tunit.Tests.csproj', [], base);
    assert.deepEqual(reports.slice(0, 2), ['--results-directory', resolve(base, 'artifacts/test-results/Tunit.Tests')]);
    for (const option of ['--report-trx', 'Tunit.Tests.trx', '--coverage', 'cobertura', 'Tunit.Tests.cobertura.xml']) assert.ok(reports.includes(option), option);
    assert.deepEqual(testReportArguments('Tunit.Tests/Tunit.Tests.csproj', ['--list-tests'], base), []);
    assert.deepEqual(testReportArguments('Probe.Tests/Probe.Tests.csproj', [], base), []);
    // The examples reference the TUnit metapackage, which includes the report extensions.
    assert.ok(testReportArguments('Example.Tests/Example.Tests.csproj', [], base).includes('Example.Tests.trx'));
    assert.deepEqual(testReportArguments('Other.Tests/Other.Tests.csproj', [], base), []);
  } finally { rmSync(base, { recursive: true, force: true }); }
});
