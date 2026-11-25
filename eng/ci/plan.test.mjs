import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { brotliDecompressSync, gunzipSync } from 'node:zlib';
import { tmpdir } from 'node:os';
import { dirname, relative, resolve } from 'node:path';
import { root, workspace, engineeringOnly, engineeringTestDirectories } from '../run.mjs';
import { plan, planFor, ciComponent, skippableJobs, reusedSkip, managedGroups, managedTests, webTests, outputs, affected, affectedPlatforms, parseAffected,
  testReportArguments, everyLinuxJob, allPlatforms, nodeEngines, nodePackages, diffKind, parseDiffKind, versionOnly, coveredPlan, validPlan,
  parseCovered, reusedPlan, planRecord, versionFiles, coveredSkip, workflowParts, workflowPath, packScripts, packageArtifact, unscoped,
  checkoutContext, revisionContext, importsOf, scriptFile, legLists, definitionName, platformsPath, declarations, changedDefinitions,
  definitionJobs } from './plan.mjs';
import { platforms, allPlatformsLabel, nativeLegs, wpfLegs, packagingLegs, screenReaderLegs, screenReaderPaths, webKitCdpPaths, platformRules, neutralPaths,
  globalPaths, windowsProgramStartPaths, windowsFileReplacePaths, windowsPrivateFilePaths, windowsLoopbackPaths,
  windowsSpellingPrivateFilePaths, matchesPath } from './platforms.mjs';
import * as platformDefinitions from './platforms.mjs';
import { incomplete, platformSummary } from './gate.mjs';

const workflow = Bun.YAML.parse(readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8'));
const tracked = (...patterns) => execFileSync('git', ['ls-files', ...patterns], { cwd: root, encoding: 'utf8' }).trim().split('\n').filter(Boolean);
const runs = (result, job) => !result.skip.includes(job);
const everything = plan(null);
const cli = (args, input = '', env = {}) => {
  const result = spawnSync(process.execPath, [resolve(root, 'eng/ci/plan.mjs'), ...args], { cwd: root, input, encoding: 'utf8',
    env: { ...process.env, CI_PLAN_RECORD: '', GITHUB_STEP_SUMMARY: '', ...env } });
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
  for (const file of ['Directory.Build.props', 'Directory.Packages.props', 'global.json', 'bun.lock', 'eng/run.mjs', 'eng/ci/source-state.mjs',
    'eng/build/nuget-lock.targets', 'eng/build/application.props', 'eng/build/application.targets', 'eng/dependencies/package-downloads.json', 'eng/ci/retry.sh', '.github/workflows/ci.yml', '.github/actions/setup-sdk/action.yml', 'flake.nix', 'LICENSE']) {
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

test('Markdown needs no Windows or macOS leg, even in a component with platform rules', () => {
  for (const file of ['packages/dotnet/Runic.Desktop/README.md', 'packages/dotnet/Runic.Application.Views/README.md',
    'packages/dotnet/Runic.Platform.Windows/README.md', 'tests/native/Runic.Packaging.Probe/README.md', 'eng/ci/README.md'])
    assert.deepEqual(affectedPlatforms([file]).platforms, [], file);
  assert.deepEqual(affectedPlatforms(['packages/dotnet/Runic.Desktop/README.md', 'packages/dotnet/Runic.Desktop/DesktopMenu.cs']).platforms, platforms);
  // The analyzers' release tracking files are compiler inputs (AdditionalFiles), not documentation.
  assert.deepEqual(affectedPlatforms(['packages/dotnet/Runic.Platform.Analyzers/AnalyzerReleases.Unshipped.md']).platforms, ['win-x64']);
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
  for (const file of ['tools/Runic.Application.Templates/content/runic-app/packaging/windows/AppxManifest.xml',
    'tools/Runic.Application.Templates/content/runic-app/ApplicationIdentity.cs', 'tests/native/Runic.Packaging.Probe/Program.cs',
    'packages/dotnet/Runic.Platform.Windows/WindowsPlatformProvider.cs', 'packages/dotnet/Runic.Desktop/DesktopSurface.cs',
    'packages/dotnet/Runic.Application.Views/RunicWindowHost.cs', 'tools/dotnet-runic/Packaging/WindowsPackager.cs']) {
    const result = plan([file]);
    assert.ok(runs(result, 'packaging'), file);
  }
  // dotnet runic package builds every package on its own OS; the tool's other commands need no Windows or macOS leg,
  // except the files that start programs (#1081).
  assert.deepEqual(plan(['tools/dotnet-runic/Packaging/MacOSPackager.cs']).packaging.include.map(leg => leg.target), packagingLegs.map(leg => leg.target));
  assert.deepEqual(plan(['tools/dotnet-runic/DoctorChecks.cs']).platforms, []);
  const packagingOnly = plan(['tools/Runic.Application.Templates/content/runic-app/packaging/linux/flatpak.yml']);
  assert.ok(runs(packagingOnly, 'templates'), 'the recipes ship in the template package');
  for (const file of ['tests/templates/Test-Templates.sh', 'tools/Runic.Application.Templates/content/runic-app/Program.cs',
    'packages/web/svelte/src/index.ts'])
    assert.ok(!runs(plan([file]), 'packaging'), file);
});

test('the screen reader legs run for the embedded windows, the accessibility fixture and the probe only', () => {
  for (const path of screenReaderPaths) assert.ok(tracked(path).length > 0, `${path} matches no tracked file`);
  for (const file of ['packages/dotnet/Runic.Desktop/DesktopSurface.cs', 'tests/native/Runic.Desktop.AccessibilityProbe/Program.cs',
    'tests/native/Runic.Desktop.WebViewSmoke/AccessibilitySmoke.cs', 'packages/dotnet/Runic.Application.Views.Wpf/WpfPlatformServices.cs',
    'tests/dotnet/Runic.Application.Views.Wpf.Windows.Tests/ScreenReaderChecks.cs']) {
    const result = plan([file]);
    assert.ok(runs(result, 'screen-readers'), file);
    assert.deepEqual(result['screen-readers'].include, screenReaderLegs, file);
  }
  // GTK 4 plans no Windows leg, so only Orca reads it.
  for (const file of ['packages/dotnet/Runic.Desktop.Gtk4/GtkWindow.cs', 'tests/native/Runic.Desktop.Gtk4.Smoke/Program.cs'])
    assert.deepEqual(plan([file])['screen-readers'].include, screenReaderLegs.filter(leg => !leg.platform), file);
  for (const file of ['packages/dotnet/Runic.Platform.Linux.Portal/Portal.cs', 'packages/dotnet/Runic.Platform.Windows/WindowsPlatformProvider.cs',
    'packages/web/views/src/index.ts', 'docs/README.md'])
    assert.ok(!runs(plan([file]), 'screen-readers'), file);
  // Pushes to main run Orca; a base planner without screenReaders runs every planned leg.
  assert.deepEqual(planFor(everyLinuxJob(affected(['docs/README.md'])))['screen-readers'].include, screenReaderLegs.filter(leg => !leg.platform));
  const { screenReaders, ...older } = affected(['packages/web/views/src/index.ts']);
  assert.equal(screenReaders, false);
  assert.deepEqual(planFor(parseAffected(JSON.stringify(older)))['screen-readers'].include, legsFor(screenReaderLegs, older.platforms));
});

test('the webkit-cdp job runs for dotnet runic dev, the Linux hosts, the template and its check only', () => {
  for (const path of webKitCdpPaths) assert.ok(tracked(path).length > 0, `${path} matches no tracked file`);
  for (const file of ['tools/dotnet-runic/WebKit/CdpPageSession.cs', 'tools/dotnet-runic/DevApplication.cs',
    'packages/dotnet/Runic.Desktop/Internal/LinuxWebKitGtkHost.cs', 'packages/dotnet/Runic.Desktop.Gtk4/Gtk4WindowHostFactory.cs',
    'tools/Runic.Application.Templates/content/runic-app/.vscode/launch.json', 'tests/webkit-cdp/dap-scenarios.mjs',
    'tests/templates/check-webkit-cdp.mjs'])
    assert.ok(runs(plan([file]), 'webkit-cdp'), file);
  for (const file of ['packages/dotnet/Runic.Platform.Windows/WindowsPlatformProvider.cs', 'packages/web/views/src/index.ts', 'docs/README.md'])
    assert.ok(!runs(plan([file]), 'webkit-cdp'), file);
  // Pushes to main run it; a base planner without webKitCdp runs it.
  assert.ok(runs(planFor(everyLinuxJob(affected(['docs/README.md']))), 'webkit-cdp'));
  const { webKitCdp, ...older } = affected(['packages/web/views/src/index.ts']);
  assert.equal(webKitCdp, false);
  assert.ok(runs(planFor(parseAffected(JSON.stringify(older))), 'webkit-cdp'));
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
  assert.equal(JSON.parse(cli(['--affected'], 'eng/run.mjs\0')).full, true);
  assert.deepEqual(JSON.parse(cli(['--affected'], 'eng/ci/plan.mjs\0')).components, ['ci']);
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
    ['full', 'components', 'managed', 'web', 'frameworks', 'native', 'wpf', 'packaging', 'screen-readers', 'platforms', 'why', 'skip']);
  for (const line of lines) JSON.parse(line.slice(line.indexOf('=') + 1));
});

test('the npm packages that run in Node admit Node 24 and 26, and the node-26 job tests them on Node 26', () => {
  // D031.3; browser-only packages declare no engines.
  assert.deepEqual(nodePackages(), ['vite-plugin-runic', 'sveltekit']);
  assert.equal(nodeEngines, '^24.18.0 || ^26.0.0');
  for (const entry of workspace.npm) {
    const manifest = JSON.parse(readFileSync(resolve(root, entry.path, 'package.json'), 'utf8'));
    assert.equal(manifest.engines?.node, nodePackages().includes(entry.path.split('/').at(-1)) ? nodeEngines : undefined, entry.name);
  }
  const job = workflow.jobs['node-26'];
  assert.match(job.steps.find(step => step.uses?.startsWith('actions/setup-node@'))?.with['node-version'], /^26\.\d+\.\d+$/);
  const tests = job.steps.filter(step => step.run?.includes('bun run test'));
  assert.deepEqual(tests.map(step => step['working-directory']), nodePackages().map(name => `packages/web/${name}`));
  for (const step of tests) {
    const name = step['working-directory'].split('/').at(-1);
    assert.equal(step.if, `\${{ !cancelled() && steps.restore.outcome == 'success' && contains(fromJSON(needs.plan.outputs.web), '${name}') }}`);
    // Without --bun, the package scripts' tools run on the job's Node.
    assert.doesNotMatch(step.run, /--bun/);
    assert.match(step.run, /node --version \| grep '\^v26\\\.'/);
  }
  assert.ok(runs(plan(['packages/web/sveltekit/src/index.ts']), 'node-26'));
  assert.ok(runs(plan(['packages/web/vite-plugin-runic/src/index.ts']), 'node-26'));
  assert.equal(runs(plan(['packages/web/views-effect/src/index.ts']), 'node-26'), false);
  assert.equal(runs(plan(['README.md']), 'node-26'), false);
  assert.ok(runs(everything, 'node-26'));
});

test('the workflow gates exactly the skippable jobs on the plan and verify aggregates all of them', () => {
  const jobs = Object.keys(workflow.jobs);
  for (const job of skippableJobs) {
    assert.ok(workflow.jobs[job], job);
    assert.equal(workflow.jobs[job].if, `\${{ !contains(fromJSON(needs.plan.outputs.skip), '${job}') }}`, job);
    assert.ok([workflow.jobs[job].needs].flat().includes('plan'), job);
  }
  // native-libraries, build and engineering run in every plan but a merge queue entry's reuse of a passed pull request run.
  assert.equal(workflow.jobs.plan.if, undefined);
  for (const job of ['native-libraries', 'build', 'engineering']) {
    assert.ok(!skippableJobs.includes(job), job);
    assert.ok(reusedSkip.includes(job), job);
    assert.equal(workflow.jobs[job].if, `\${{ !contains(fromJSON(needs.plan.outputs.skip), '${job}') }}`, job);
    assert.ok([workflow.jobs[job].needs].flat().includes('plan'), job);
  }
  assert.deepEqual(workflow.jobs.engineering.needs, ['plan', 'build']);
  assert.deepEqual([...skippableJobs, 'plan', 'native-libraries', 'build', 'engineering', 'verify'].sort(), jobs.sort());
  assert.equal(workflow.jobs.verify.if, 'always()');
  assert.deepEqual([...workflow.jobs.verify.needs].sort(), jobs.filter(job => job !== 'verify').sort());
  const gate = workflow.jobs.verify.steps.find(step => step.run === 'bun eng/ci/gate.mjs');
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
  // A failed plan skips build and engineering too, and has no skip list to excuse them.
  assert.deepEqual(incomplete({ plan: { result: 'failure' }, build: { result: 'skipped' }, engineering: { result: 'skipped' } }),
    ['plan=failure', 'build=skipped', 'engineering=skipped']);
  const reused = Object.fromEntries(['plan', ...reusedSkip].map(job => [job, { result: job === 'plan' ? 'success' : 'skipped' }]));
  assert.deepEqual(incomplete(reused, reusedSkip), []);
  assert.deepEqual(incomplete(reused, skippableJobs), ['native-libraries=skipped', 'build=skipped', 'engineering=skipped']);
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
// CsWebUi), not another component's source. AotSizeModelsDirectory is the scaling suite's generated NativeAOT
// fixture models under artifacts/ (tests/scaling/aot-size). _RunicDevTools* is dotnet-runic's stamped DevTools extension
// manifest under obj/ (Runic.Application.Tool.csproj). _RunicDesktopApplicationIcon is the consuming application's own
// package icon (Runic.Desktop.targets, sdk#1207).
const unresolvable = /^(?:@\(|\$\((?:NuGetPackageRoot|_RunicDesktopApplicationIcon|AotSizeModelsDirectory|DynamicDataForkRoot|CsWebUiRepository|RunicPolicyIcon|RunicPackagingProbeExtraFiles\w*|TemplatePackContentRoot|_?RunicBridge\w*|_?RunicAssets\w*|_?RunicDevTools\w*|_?RunicPrivileged\w*|_?RunicDesktopNativeServer\w*|_?RunicNativeServer\w*)\))/;

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
  // The Flathub fixture is one too: dotnet runic package flathub generates its sources from its NativeAOT lock file.
  const independent = path => path.startsWith('tests/fixtures/nix/') || path.startsWith('tests/fixtures/flathub/');
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
    'packages/dotnet/Runic.Navigation.ReactiveUI/ReactiveNavigationRegionExtensions.cs',
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

// D066.25 3: the native job's Windows x64 leg checks the template's WebView2 script-debugging files against WebView2
// (tests/fixtures/application/dev-live-assets/webview2-debug-smoke.mjs), so they plan that job and leg.
test('the template WebView2 debugging files plan the native Windows x64 leg', () => {
  for (const file of [
    'tools/Runic.Application.Templates/content/runic-app/Properties/launchSettings.json',
    'tools/Runic.Application.Templates/content/runic-app/.vscode/launch.json',
    'tests/fixtures/application/dev-live-assets/webview2-debug-smoke.mjs',
    'tools/dotnet-runic/DevSession.cs',
    'tools/dotnet-runic/DevApplication.cs',
  ]) {
    const result = plan([file]);
    assert.deepEqual(result.native.include, legsFor(nativeLegs, ['win-x64']), file);
    assert.ok(runs(result, 'templates'), file);
  }
  // The template's other files still need no Windows or macOS leg.
  assert.deepEqual(plan(['tools/Runic.Application.Templates/content/runic-app/.vscode/tasks.json']).platforms, []);
});

// #1081: dotnet runic starts npm, which is npm.cmd on Windows. The files that start programs or choose how, and the checks
// of those starts, plan the native Windows x64 leg, which tests them and runs dev with npm
// (tests/fixtures/application/dev-live-assets/dev-npm-smoke.mjs). The tool's other files plan none.
test('dotnet runic\'s program starts plan the native Windows x64 leg, its other files none', () => {
  for (const file of windowsProgramStartPaths)
    assert.deepEqual(plan([file]).native.include, legsFor(nativeLegs, ['win-x64']), file);
  for (const file of ['tools/dotnet-runic/DoctorChecks.cs', 'tools/dotnet-runic/DevOptions.cs', 'tools/dotnet-runic/SupportForwarding.cs',
    'tools/dotnet-runic/README.md', 'tests/dotnet/Runic.Application.Tool.Tests/ToolTests.cs'])
    assert.deepEqual(plan([file]).platforms, [], file);
});

// #1102: the retry of a rename that Windows refuses, and its check against a held file, plan the native Windows x64
// leg, which runs that check. The frontend compiler's ready snapshot only calls the retry and plans no leg itself.
test('dotnet runic\'s Windows file replace plans the native Windows x64 leg', () => {
  assert.deepEqual(windowsFileReplacePaths,
    ['tools/dotnet-runic/FileReplace.cs', 'tests/dotnet/Runic.Application.Tool.Tests/DevSessionFileRetryTests.cs']);
  for (const file of windowsFileReplacePaths) {
    assert.ok(existsSync(resolve(root, file)), file);
    assert.deepEqual(plan([file]).native.include, legsFor(nativeLegs, ['win-x64']), file);
  }
  assert.deepEqual(plan(['tools/dotnet-runic/FrontendCompilerHotReloadCoordinator.cs']).platforms, []);
});

// #1187: dev --browser's code file and its check of the Windows access list plan the native Windows x64 leg, which runs
// that check.
test('dotnet runic dev --browser\'s private code file plans the native Windows x64 leg', () => {
  assert.deepEqual(windowsPrivateFilePaths,
    ['tools/dotnet-runic/BrowserEntry.cs', 'tests/dotnet/Runic.Application.Tool.Tests/DevBrowserCodeFileTests.cs']);
  for (const file of windowsPrivateFilePaths) {
    assert.ok(existsSync(resolve(root, file)), file);
    assert.deepEqual(plan([file]).native.include, legsFor(nativeLegs, ['win-x64']), file);
  }
  assert.deepEqual(plan(['tests/dotnet/Runic.Application.Tool.Tests/DevBrowserTests.cs']).platforms, []);
});

// DEV-SEC-80: dev's listeners on 127.0.0.1 and [::1] and their checks plan the native Windows x64 leg, which runs them.
test('dotnet runic dev\'s loopback listeners plan the native Windows x64 leg', () => {
  assert.deepEqual(windowsLoopbackPaths,
    ['tools/dotnet-runic/LoopbackListeners.cs', 'tests/dotnet/Runic.Application.Tool.Tests/LoopbackFamilyTests.cs']);
  for (const file of windowsLoopbackPaths) {
    assert.ok(existsSync(resolve(root, file)), file);
    assert.deepEqual(plan([file]).native.include, legsFor(nativeLegs, ['win-x64']), file);
  }
});

// DEV-SEC-88: Runic.Spelling's private word list, its owner-only helper and their check of the Windows access list plan the
// native Windows x64 leg, which runs that check (Runic.Spelling.Tests' WindowsPrivateFiles category). The package's other
// files plan no Windows leg.
test('Runic.Spelling\'s private word list plans the native Windows x64 leg', () => {
  assert.deepEqual(windowsSpellingPrivateFilePaths, ['packages/dotnet/Runic.Spelling/PrivateFiles.cs',
    'packages/dotnet/Runic.Spelling/SpellingUserWords.cs', 'tests/dotnet/Runic.Spelling.Tests/PrivateFileTests.cs']);
  for (const file of windowsSpellingPrivateFilePaths) {
    assert.ok(existsSync(resolve(root, file)), file);
    assert.deepEqual(plan([file]).native.include, legsFor(nativeLegs, ['win-x64']), file);
  }
  assert.deepEqual(plan(['packages/dotnet/Runic.Spelling/SpellChecker.cs']).platforms, []);
  // The step that runs the check is in the native job, on the leg that the rule plans.
  const workflowText = readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8');
  assert.match(workflowText, /--project tests\/dotnet\/Runic\.Spelling\.Tests[^\n]*\n?[^\n]*Category=WindowsPrivateFiles/);
});

// The narrow rule above holds only while every process start of the tool goes through CommandRunner, whose callers pass
// it the program and arguments. A new start elsewhere must use CommandRunner or join windowsProgramStartPaths here.
test('dotnet runic starts processes through CommandRunner only', () => {
  const allowed = new Set(['tools/dotnet-runic/CommandRunner.cs']);
  const starts = /\bnew\s+(?:System\.Diagnostics\.)?ProcessStartInfo\b|\bProcessStartInfo\s+\w+\s*=\s*new\s*\(|\bProcess\.Start\s*\(|\bStartInfo\s*=\s*new\b|\bUseShellExecute\s*=/;
  const files = tracked('tools/dotnet-runic/*.cs', 'tools/dotnet-runic/**/*.cs');
  assert.ok(files.includes('tools/dotnet-runic/CommandRunner.cs'));
  const offenders = [...new Set(files)].filter(file => !allowed.has(file) && !windowsProgramStartPaths.includes(file))
    .filter(file => starts.test(readFileSync(resolve(root, file), 'utf8')));
  assert.deepEqual(offenders, [], 'start these processes through CommandRunner, or add the files to windowsProgramStartPaths');
  // The check still sees a direct start.
  assert.ok(starts.test('var start = new ProcessStartInfo(tool) { UseShellExecute = false };'));
  assert.ok(starts.test('using Process process = Process.Start(start);'));
});

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

// B10: the development-directory source pins its root per OS and watches with FSEvents and ReadDirectoryChangesW,
// so its native-job step runs on x64 Windows and macOS.
test('the development asset source plans the x64 Windows and macOS legs', () => {
  for (const file of ['packages/dotnet/Runic.Assets/DevelopmentDirectoryAssetSource.cs', 'tests/dotnet/Runic.Assets.Tests/DevelopmentDirectoryTests.cs'])
    assert.deepEqual(plan([file]).platforms, ['win-x64', 'osx-arm64'], file);
});

test('owned files without a rule, engineering-only and neutral files plan no Windows or macOS leg', () => {
  for (const file of ['packages/web/svelte/src/index.ts', 'packages/web/views/src/index.ts', 'tools/Runic.Application.Templates/content/runic-app/Program.cs',
    'packages/dotnet/Runic.Assets.AspNetCore/RunicAssetEndpointExtensions.cs', 'packages/dotnet/Runic.Navigation.ReactiveUI/ReactiveNavigationRegionExtensions.cs', 'examples/notes-view-first/Program.cs',
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
    assert.deepEqual(result.native.include.map(leg => leg.rid), ['linux-x64', 'linux-x64', 'linux-arm64', 'linux-arm64'], file);
    assert.deepEqual(result.packaging.include.map(leg => leg.target), ['flatpak', 'appimage', 'deb', 'rpm', 'flathub'], file);
    assert.deepEqual(result.skip, ['wpf'], file);
  }
  // Any other unowned file plans every leg.
  for (const file of ['global.json', 'eng/ci/build-artifact.mjs', 'eng/support.json', '.github/actions/setup-sdk/action.yml', 'bun.lock', 'eng/workspace.json']) {
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
  assert.deepEqual(push.native.include.map(leg => leg.rid), ['linux-x64', 'linux-x64', 'linux-arm64', 'linux-arm64', 'osx-arm64', 'osx-arm64']);
  assert.deepEqual(push.skip, ['wpf']);
  const labelled = planFor(allPlatforms(decided, `label ${allPlatformsLabel}`));
  assert.deepEqual(labelled.platforms, platforms);
  assert.deepEqual(labelled.native.include, nativeLegs);
  assert.equal(labelled.why, platforms.map(platform => `${platform}: label ci:all-platforms`).join('; '));
  assert.equal(labelled.full, false, 'the label adds legs, not Linux jobs');
  const cliPush = outputsOf(cli(['--plan', JSON.stringify(decided), '--all-linux']));
  assert.deepEqual([cliPush.full, cliPush.platforms, cliPush.skip], [true, ['osx-arm64'], ['wpf']]);
  const cliLabel = outputsOf(cli(['--plan', JSON.stringify(decided), '--all-platforms', 'label ci:all-platforms']));
  assert.deepEqual([cliLabel.full, cliLabel.platforms, cliLabel.wpf], [false, platforms, { include: wpfLegs }]);
  const scheduled = outputsOf(cli(['--all', 'schedule run']));
  assert.deepEqual([scheduled.full, scheduled.platforms, scheduled.skip], [true, platforms, []]);
  assert.match(scheduled.why, /^win-x64: schedule run;/);
  assert.deepEqual(outputsOf(cli([])).native, { include: nativeLegs });
});

const repository = 'Runic-Artifex/runic-sdk';
// Runs the plan step's script in a scratch repository whose `bun` stub records the planner calls. The
// `gh` stub answers `gh api <endpoint> --jq <filter>` with jq on responses({ shas, tree })[endpoint] and
// fails for any other endpoint. `merge` checks out a merge commit of the first commit and the last one,
// like a pull_request checkout.
const planStep = (event, env, commits, responses = () => ({}), { merge = false } = {}) => {
  const directory = mkdtempSync(resolve(tmpdir(), 'runic-ci-plan-step-'));
  try {
    const git = (...args) => execFileSync('git', args, { cwd: directory, encoding: 'utf8',
      env: { ...process.env, GIT_AUTHOR_NAME: 'ci', GIT_AUTHOR_EMAIL: 'ci@example.com', GIT_COMMITTER_NAME: 'ci', GIT_COMMITTER_EMAIL: 'ci@example.com' } }).trim();
    git('init', '-q', '-b', 'main');
    const shas = commits.map(file => {
      writeFileSync(resolve(directory, file), file);
      git('add', file);
      git('commit', '-q', '-m', file);
      return git('rev-parse', 'HEAD');
    });
    if (merge) {
      git('checkout', '-q', '--detach', shas[0]);
      git('merge', '-q', '--no-ff', '-m', 'merge', shas.at(-1));
    }
    const tree = git('rev-parse', 'HEAD^{tree}');
    git('remote', 'add', 'origin', directory);
    mkdirSync(resolve(directory, 'stub'));
    // --diff-kind answers STUB_DIFF_KIND (the diff it read goes to diff-kind-input), eng/ci/covered.mjs STUB_COVERED.
    writeFileSync(resolve(directory, 'stub/bun'), `#!/usr/bin/env bash
if [[ "$2" == --affected ]]; then xargs -0 echo
elif [[ "$2" == --diff-kind ]]; then cat > "${resolve(directory, 'diff-kind-input')}"; printf '%s\\n' "$STUB_DIFF_KIND"
elif [[ "$1" == eng/ci/covered.mjs ]]; then printf '%s' "$STUB_COVERED"; [[ "$2" =~ ^[0-9a-f]{40}$ ]]
else echo "bun $*"; fi
`, { mode: 0o755 });
    mkdirSync(resolve(directory, 'responses'));
    for (const [endpoint, body] of Object.entries(responses({ shas, tree })))
      writeFileSync(resolve(directory, 'responses', endpoint.replace(/[^A-Za-z0-9]/g, '_')), JSON.stringify(body));
    writeFileSync(resolve(directory, 'stub/gh'), `#!/usr/bin/env bash
endpoint= filter=
while (($#)); do
  case "$1" in api|--paginate) ;; --jq) filter=$2; shift ;; *) endpoint=$1 ;; esac
  shift
done
file="${resolve(directory, 'responses')}/\${endpoint//[^A-Za-z0-9]/_}"
[[ -f "$file" ]] && jq -r "$filter" "$file"
`, { mode: 0o755 });
    mkdirSync(resolve(directory, 'temp'));
    const output = resolve(directory, 'output');
    const script = workflow.jobs.plan.steps.find(item => item.id === 'plan').run;
    const result = spawnSync('bash', ['-eo', 'pipefail', '-c', script], { cwd: directory, encoding: 'utf8', env: { ...process.env,
      PATH: `${resolve(directory, 'stub')}:${process.env.PATH}`, GITHUB_EVENT_NAME: event, GITHUB_OUTPUT: output,
      RUNNER_TEMP: resolve(directory, 'temp'), GITHUB_REPOSITORY: repository, STUB_DIFF_KIND: '{"versionOnly":false,"release":null}',
      STUB_COVERED: '', ...env(shas) } });
    assert.equal(result.status, 0, result.stderr);
    return readFileSync(output, 'utf8').trim();
  } finally { rmSync(directory, { recursive: true, force: true }); }
};

test('a merge queue entry plans its merge group diff like an unlabelled pull request', () => {
  const queued = shas => ({ QUEUE_BASE: shas[0], QUEUE_HEAD: shas.at(-1) });
  // The base planner reads base_sha..head_sha, without --all-linux or --all-platforms.
  assert.equal(planStep('merge_group', queued, ['a.txt', 'b.txt']), 'bun eng/ci/plan.mjs --plan b.txt');
  // An entry behind another one in the queue: its base is not HEAD^1, so it is fetched.
  assert.equal(planStep('merge_group', queued, ['a.txt', 'b.txt', 'c.txt']), 'bun eng/ci/plan.mjs --plan b.txt c.txt');
  // A checkout that is not the queued head, or no base, plans everything.
  for (const env of [shas => ({ QUEUE_BASE: shas[0], QUEUE_HEAD: shas[0] }), shas => ({ QUEUE_BASE: '', QUEUE_HEAD: shas[1] }),
    shas => ({ QUEUE_BASE: 'f'.repeat(40), QUEUE_HEAD: shas[1] })])
    assert.equal(planStep('merge_group', env, ['a.txt', 'b.txt']), 'bun eng/ci/plan.mjs --all merge_group run without a usable diff');
  // Pushes keep planning every Linux job from before..after.
  assert.equal(planStep('push', shas => ({ BEFORE: shas[0] }), ['a.txt', 'b.txt']), 'bun eng/ci/plan.mjs --plan b.txt --all-linux');
});

const outputsOf = text => Object.fromEntries(text.trim().split('\n').map(line => [line.slice(0, line.indexOf('=')), JSON.parse(line.slice(line.indexOf('=') + 1))]));

test('a reused plan runs nothing, build and engineering included', () => {
  const url = 'https://github.com/Runic-Artifex/runic-sdk/actions/runs/11';
  const reused = outputsOf(cli(['--reused', url]));
  assert.deepEqual(reused.skip, reusedSkip);
  assert.deepEqual(reusedSkip, [...skippableJobs, 'native-libraries', 'build', 'engineering']);
  assert.deepEqual([reused.full, reused.components, reused.managed, reused.web, reused.frameworks, reused.platforms],
    [false, [], [], [], false, []]);
  for (const name of ['native', 'wpf', 'packaging', 'screen-readers']) assert.deepEqual(reused[name], { include: [] }, name);
  assert.equal(reused.why, `reuses ${url}, which tested the same tree on the same base`);
  assert.deepEqual(Object.keys(reused), Object.keys(outputsOf(cli([]))));
  // Without a run to reuse, everything runs.
  assert.deepEqual(outputsOf(cli(['--reused'])).skip, []);
});

test('a merge queue entry reuses only a passed same-repository ci.yml pull request run of its base and tree', () => {
  const queued = shas => ({ QUEUE_BASE: shas[0], QUEUE_HEAD: shas.at(-1) });
  const url = `https://github.com/${repository}/actions/runs/11`;
  const responses = ({ run = {}, jobs = [{ name: 'build', conclusion: 'success' }, { name: 'verify', conclusion: 'success' }], expired = false,
    marker = ({ shas, tree }) => `${shas[0]}-${tree}` } = {}) =>
    ({ shas, tree }) => ({
      [`repos/${repository}/actions/artifacts?name=ci-passed-${marker({ shas, tree })}&per_page=100`]:
        { artifacts: [{ name: `ci-passed-${marker({ shas, tree })}`, expired, workflow_run: { id: 11 } }] },
      [`repos/${repository}/actions/runs/11`]: { event: 'pull_request', path: '.github/workflows/ci.yml',
        head_repository: { full_name: repository }, html_url: url, ...run },
      [`repos/${repository}/actions/runs/11/jobs?per_page=100`]: { jobs },
    });
  const normal = 'bun eng/ci/plan.mjs --plan b.txt';
  assert.equal(planStep('merge_group', queued, ['a.txt', 'b.txt'], responses()), `bun eng/ci/plan.mjs --reused ${url}`);
  assert.equal(planStep('merge_group', queued, ['a.txt', 'b.txt'], responses({ run: { path: '.github/workflows/ci.yml@refs/pull/7/merge' } })),
    `bun eng/ci/plan.mjs --reused ${url}`);
  for (const [why, response] of [
    ['verify failed', responses({ jobs: [{ name: 'verify', conclusion: 'failure' }] })],
    ['verify skipped', responses({ jobs: [{ name: 'verify', conclusion: 'skipped' }] })],
    ['no verify job', responses({ jobs: [{ name: 'build', conclusion: 'success' }] })],
    ['a push run', responses({ run: { event: 'push' } })],
    ['a merge queue run', responses({ run: { event: 'merge_group' } })],
    ['a fork', responses({ run: { head_repository: { full_name: 'someone/runic-sdk' } } })],
    ['a deleted fork', responses({ run: { head_repository: null } })],
    ['another workflow', responses({ run: { path: '.github/workflows/publish-preview.yml' } })],
    ['an expired marker', responses({ expired: true })],
    // A marker for another tree (the pull request changed since) or another base (main moved) is not looked up.
    ['another tree', responses({ marker: ({ shas }) => `${shas[0]}-${'e'.repeat(40)}` })],
    ['another base', responses({ marker: ({ shas, tree }) => `${shas[1]}-${tree}` })],
    ['gh fails', () => ({})],
  ]) assert.equal(planStep('merge_group', queued, ['a.txt', 'b.txt'], response), normal, why);
  // Only merge queue entries look for a run to reuse.
  assert.equal(planStep('push', shas => ({ BEFORE: shas[0] }), ['a.txt', 'b.txt'], responses()), 'bun eng/ci/plan.mjs --plan b.txt --all-linux');
});

test('a passed same-repository pull request run marks its merge commit base and tree', () => {
  const labels = () => ({ [`repos/${repository}/issues/7/labels`]: [] });
  const pull = head => () => ({ PULL_REQUEST: '7', HEAD_REPOSITORY: head, LABEL: allPlatformsLabel });
  const tree = (directory => {
    try {
      execFileSync('git', ['init', '-q'], { cwd: directory });
      for (const file of ['a.txt', 'b.txt']) writeFileSync(resolve(directory, file), file);
      execFileSync('git', ['add', '.'], { cwd: directory });
      return execFileSync('git', ['write-tree'], { cwd: directory, encoding: 'utf8' }).trim();
    } finally { rmSync(directory, { recursive: true, force: true }); }
  })(mkdtempSync(resolve(tmpdir(), 'runic-ci-tree-')));
  const marked = planStep('pull_request', pull(repository), ['a.txt', 'b.txt'], labels, { merge: true }).split('\n');
  assert.match(marked[0], /^tested-base=[0-9a-f]{40}$/);
  assert.deepEqual(marked.slice(1), [`tested-tree=${tree}`, 'bun eng/ci/plan.mjs --plan b.txt']);
  // Fork pull requests leave no marker.
  assert.equal(planStep('pull_request', pull('someone/runic-sdk'), ['a.txt', 'b.txt'], labels, { merge: true }), 'bun eng/ci/plan.mjs --plan b.txt');
  const { plan: job, verify } = workflow.jobs;
  for (const name of ['tested-base', 'tested-tree']) assert.equal(job.outputs[name], `\${{ steps.plan.outputs.${name} }}`, name);
  assert.equal(job.steps.find(item => item.id === 'plan').env.HEAD_REPOSITORY, '${{ github.event.pull_request.head.repo.full_name }}');
  // verify uploads the marker only after its gate passed, for same-repository pull requests.
  const gate = verify.steps.findIndex(step => step.run === 'bun eng/ci/gate.mjs');
  const [write, upload] = verify.steps.slice(gate + 1);
  const condition = "${{ github.event_name == 'pull_request' && github.event.pull_request.head.repo.full_name == github.repository && needs.plan.outputs.tested-tree != '' }}";
  for (const step of [write, upload]) assert.equal(step.if, condition, step.name);
  assert.equal(write.run, 'echo "$RUN_ID" > "$RUNNER_TEMP/ci-passed"');
  const build = workflow.jobs.build.steps.find(step => step.uses?.startsWith('actions/upload-artifact@'));
  assert.equal(upload.uses, build.uses);
  // A missing marker only costs the queue entry a full run.
  assert.equal(upload['continue-on-error'], true);
  assert.deepEqual(upload.with, { name: 'ci-passed-${{ needs.plan.outputs.tested-base }}-${{ needs.plan.outputs.tested-tree }}',
    path: '${{ runner.temp }}/ci-passed', 'if-no-files-found': 'error', overwrite: true, 'retention-days': 7, 'compression-level': 0 });
  // The plan step looks the marker up by the same name.
  assert.match(job.steps.find(item => item.id === 'plan').run, /actions\/artifacts\?name=ci-passed-\$base-\$tree&/);
});

test('a base planner without platforms plans every leg, and invalid platforms are ignored', () => {
  const decided = affected(['packages/web/svelte/src/index.ts']);
  assert.deepEqual(parseAffected(JSON.stringify(decided)), decided);
  for (const older of [{ full: decided.full, components: decided.components },
    { ...decided, platforms: ['win-x64', 'linux-riscv64'] }, { ...decided, platforms: ['win-x64'], why: {} }]) {
    const parsed = parseAffected(JSON.stringify(older));
    const readers = {
      ...older.screenReaders === undefined ? {} : { screenReaders: older.screenReaders },
      ...older.webKitCdp === undefined ? {} : { webKitCdp: older.webKitCdp },
    };
    assert.deepEqual(parsed, { full: decided.full, components: decided.components, ...readers });
    const result = planFor(parsed);
    assert.deepEqual(result.platforms, platforms);
    assert.match(result.why, /the base planner plans no platforms/);
    assert.deepEqual(result.native.include, nativeLegs);
  }
});

test('the workflow plans platform legs, reads the label live and runs everything nightly', () => {
  const { plan: job } = workflow.jobs;
  for (const name of ['native', 'wpf', 'packaging', 'screen-readers']) {
    assert.equal(workflow.jobs[name].strategy.matrix, `\${{ fromJSON(needs.plan.outputs.${name}) }}`, name);
    assert.equal(job.outputs[name], `\${{ steps.plan.outputs.${name} }}`, name);
  }
  for (const name of ['platforms', 'why']) assert.equal(job.outputs[name], `\${{ steps.plan.outputs.${name} }}`, name);
  assert.deepEqual(job.permissions, { contents: 'read', 'pull-requests': 'read', actions: 'read' });
  const step = job.steps.find(item => item.id === 'plan');
  assert.equal(step.env.LABEL, allPlatformsLabel);
  assert.equal(step.env.BEFORE, '${{ github.event.before }}');
  assert.match(step.run, /gh api "repos\/\$GITHUB_REPOSITORY\/issues\/\$PULL_REQUEST\/labels"/);
  assert.match(step.run, /options=\(--all-platforms "the pull request's labels could not be read"\)/);
  assert.match(step.run, /options=\(--all-linux\)/);
  assert.match(step.run, /bun eng\/ci\/plan\.mjs --plan "\$affected" "\$\{options\[@\]\}"/);
  assert.match(step.run, /bun eng\/ci\/plan\.mjs --all "\$reason"/);
  assert.equal(step.env.QUEUE_BASE, '${{ github.event.merge_group.base_sha }}');
  assert.equal(step.env.QUEUE_HEAD, '${{ github.event.merge_group.head_sha }}');
  // A label is read when the plan runs; adding one does not start a run by itself.
  assert.equal(workflow.on.pull_request, null);
  assert.deepEqual(workflow.on.merge_group, { types: ['checks_requested'] });
  assert.equal(workflow.on.schedule.length, 1);
  assert.ok(Object.hasOwn(workflow.on, 'workflow_dispatch'));
  assert.equal(workflow.concurrency['cancel-in-progress'], "${{ github.event_name == 'pull_request' }}");
  assert.match(workflow.concurrency.group, /github\.run_id/);
  const gate = workflow.jobs.verify.steps.find(step => step.run === 'bun eng/ci/gate.mjs');
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

// #473 (B11): Runic.Application.Views.Wpf.Windows.Tests checks the ReactiveUI adapters' failure bookkeeping under
// WPF's dispatcher, and Runic.Navigation carries their model-turn flow. A change to any project the suite references,
// directly or transitively, schedules the x64 WPF leg that runs it, except the Linux and macOS platform providers,
// whose rules keep them off Windows.
test('the projects the Windows WPF suite references schedule the x64 WPF leg', () => {
  const suite = resolve(root, 'tests/dotnet/Runic.Application.Views.Wpf.Windows.Tests/Runic.Application.Views.Wpf.Windows.Tests.csproj');
  const projects = new Set();
  const walk = project => {
    if (projects.has(project)) return;
    projects.add(project);
    for (const [, reference] of readFileSync(project, 'utf8').matchAll(/<ProjectReference Include="([^"]+)"/g))
      walk(resolve(dirname(project), reference.replaceAll('\\', '/')));
  };
  walk(suite);
  const directories = [...projects].map(project => relative(root, dirname(project)));
  for (const required of ['packages/dotnet/Runic.Application.Views.ReactiveUI', 'packages/dotnet/Runic.Navigation', 'packages/dotnet/Runic.Application.Testing'])
    assert.ok(directories.includes(required), `the Windows WPF suite no longer references ${required}`);
  const offWindows = directory => /^packages\/dotnet\/Runic\.(Platform\.Linux|Platform\.MacOS|Desktop\.Gtk4)/.test(directory);
  const files = [...directories.filter(directory => !offWindows(directory)).map(directory => `${directory}/Example.cs`),
    'packages/dotnet/Runic.Application.Views.ReactiveUI.Reactive/ReactiveModelContextScheduler.cs',
    'packages/dotnet/Runic.Navigation/RunicModelContext.cs'];
  for (const file of files) {
    const result = plan([file]);
    assert.ok(runs(result, 'wpf'), `${file} does not plan the wpf job`);
    assert.ok(result.wpf.include.some(leg => leg.platform === 'win-x64'), `${file} does not plan the win-x64 WPF leg`);
  }
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

// I11 (0.21: a CI-only change ran every leg). Every tracked file has an owner, is engineering-only, neutral or global
// on purpose; a new file that is none of these fails here instead of silently planning every job and leg.
const ownedBy = file => Object.entries(workspace.components)
  .filter(([, component]) => component.paths.some(path => file === path || file.startsWith(`${path}/`))).map(([name]) => name);
export const unownedFiles = files => files.filter(file => !engineeringOnly(file) && ownedBy(file).length === 0
  && !neutralPaths.some(path => matchesPath(file, path)) && !globalPaths.some(path => matchesPath(file, path)));

test('every tracked file is owned, engineering-only, neutral or global on purpose', () => {
  const unowned = unownedFiles(tracked());
  assert.deepEqual(unowned, [], `give these files a component in eng/workspace.json, or list them in neutralPaths or globalPaths (eng/ci/platforms.mjs):\n${unowned.join('\n')}`);
  // A new CI script under eng/ci is caught too.
  assert.deepEqual(unownedFiles(['eng/ci/new-helper.sh', 'eng/ci/plan.mjs', 'eng/ci/new.test.mjs', 'nix/new.nix', 'eng/build/new.props']), ['eng/ci/new-helper.sh']);
  // Every neutral and global entry names a tracked file, so a rename cannot leave one matching nothing.
  for (const path of [...neutralPaths, ...globalPaths]) assert.ok(tracked(path).length > 0, `${path} matches no tracked file`);
});

test('the CI component plans only the always-run jobs, without Windows or macOS legs', () => {
  const ci = workspace.components[ciComponent];
  assert.deepEqual(ci.dependsOn, []);
  assert.ok(!Object.values(workspace.components).some(component => component.dependsOn.includes(ciComponent)), 'no component depends on the CI component');
  for (const path of ci.paths) {
    assert.ok(tracked(path).length > 0, `${path} matches no tracked file`);
    assert.ok(!platformRules.some(rule => rule.paths.some(item => matchesPath(path, item))), `${path} has a platform rule`);
  }
  // platforms.mjs plans by its changed definitions, which need a context (see the leg definition tests).
  for (const files of [['eng/ci/plan.mjs'], ['eng/ci/plan.test.mjs'], ['eng/ci/gate.mjs', 'eng/ci/contracts.test.mjs'],
    ['eng/ci/flakes.mjs', 'eng/ci/flakes.test.mjs', 'eng/ci/README.md'], ['eng/ci/local.mjs', 'eng/ci/runner.Containerfile'],
    ['eng/ci/fixtures/artifact-roundtrip.yml'], ['eng/reliability/run.mjs'], ['eng/json-schema.mjs'], ['.github/CODEOWNERS']]) {
    const result = plan(files);
    assert.equal(result.full, false, files.join());
    assert.deepEqual(result.components, files.some(file => ownedBy(file).length) ? [ciComponent] : [], files.join());
    assert.deepEqual(result.skip, skippableJobs, files.join());
    assert.deepEqual(result.platforms, [], files.join());
  }
  // With a shipped component, package consumers and templates run as before.
  assert.ok(runs(plan(['eng/ci/plan.mjs', 'packages/web/svelte/src/index.ts']), 'templates'));
});

// D080, R7 G1: the components later work needs, declared before their paths exist, so no new path under them plans
// every job and leg. Rust crates plan Linux only, the C++ layer and the conformance driver no skippable job, runic-server
// as before (the desktop component and its dependents, Linux legs only), the NativeAOT tool builds the package jobs.
test('the Rust, C++, conformance and NativeAOT tool components plan Linux only, and runic-server as before', () => {
  const linuxOnly = result => { assert.equal(result.full, false); assert.deepEqual(result.platforms, []); };
  // runic-server: desktop and every component that depends on it, with the native job's Linux legs, as when desktop
  // owned all of native/.
  const desktop = plan(['eng/native/build-runic-server.sh']);
  linuxOnly(desktop);
  assert.ok(desktop.components.includes('desktop') && !desktop.components.includes('rust'));
  assert.deepEqual(desktop.skip, ['wpf', 'screen-readers', 'webkit-cdp']);
  assert.deepEqual(desktop.native.include, nativeLegs.filter(leg => !leg.platform));
  for (const file of ['native/runic-server/src/lib.rs', 'native/runic-server/Cargo.toml', 'native/runic-server/THIRD-PARTY-NOTICES.md',
    'native/runic-server/include/runic_server.h', 'nix/runic-server.nix']) {
    const result = plan([file]);
    // The notices file is Markdown, which ci.yml's jobs do not read (native-server.yml's filters run its check).
    if (file.endsWith('.md')) assert.deepEqual(result.components, [], file);
    else assert.deepEqual(result, desktop, file);
  }
  // The workspace files that change runic-server's bytes plan desktop and rust.
  for (const file of ['native/Cargo.toml', 'native/rust-toolchain.toml', 'native/rustfmt.toml']) {
    const result = plan([file]);
    assert.deepEqual(result.components.sort(), [...desktop.components, 'rust'].sort(), file);
    assert.deepEqual({ ...result, components: [] }, { ...desktop, components: [] }, file);
  }
  for (const [component, files, jobs] of [
    ['rust', ['native/crates/views/src/lib.rs', 'native/crates/desktop/Cargo.toml', 'native/crates/capi/src/lib.rs', 'native/Cargo.lock',
      'eng/native/crates.mjs', 'native/.gitignore'], []],
    ['cpp', ['native/cpp/CMakeLists.txt', 'native/cpp/server/server.cpp', 'native/cpp/conanfile.py', 'native/cpp/samples/hello/main.cpp'], []],
    ['conformance', ['eng/conformance/core/driver.ts', 'eng/conformance/views/cases.json'], []],
    ['tool-aot', ['eng/aot/publish.sh', 'eng/aot/compare-rax.sh'], ['package-consumers', 'templates']],
  ]) {
    assert.deepEqual(workspace.components[component].dependsOn, [], component);
    assert.ok(!Object.values(workspace.components).some(item => item.dependsOn.includes(component)), `no component depends on ${component}`);
    for (const path of workspace.components[component].paths)
      assert.ok(!platformRules.some(rule => rule.paths.some(item => matchesPath(path, item) || matchesPath(item, path))), `${path} has a platform rule`);
    for (const file of [...files, files]) {
      const result = plan([file].flat());
      linuxOnly(result);
      assert.deepEqual(result.components, [component], String(file));
      assert.deepEqual(skippableJobs.filter(job => runs(result, job)), jobs, String(file));
    }
  }
  // native/cpp plans no desktop job, and a new crate none of runic-server's.
  for (const file of ['native/cpp/CMakeLists.txt', 'native/crates/views/src/lib.rs'])
    for (const job of ['native', 'native-musl', 'managed', 'views', 'package-consumers', 'templates']) assert.ok(!runs(plan([file]), job), `${file}: ${job}`);
  // The existing conformance corpora keep their owners' plans and add the conformance component.
  const corpus = plan(['specs/application/conformance/scenario.schema.json']);
  assert.ok(corpus.components.includes('application') && corpus.components.includes('conformance'));
  assert.deepEqual({ ...corpus, components: [] }, { ...plan(['specs/application/fixtures/example.json']), components: [] });
  // With a shipped component, the package jobs run as before.
  assert.ok(runs(plan(['native/crates/views/src/lib.rs', 'packages/web/svelte/src/index.ts']), 'templates'));
});

// R1 SDK-1: native-server.yml runs, and the native libraries rebuild, only for runic-server's own paths, so a crate or a
// Cargo.lock change runs neither (a lock change in runic-server's graph changes its notices file, which both include).
test('native-server.yml and the library cache key follow runic-server, not all of native/', () => {
  const serverPaths = ['native/runic-server/**', 'native/Cargo.toml', 'native/rust-toolchain.toml', 'native/rustfmt.toml'];
  const server = Bun.YAML.parse(readFileSync(resolve(root, '.github/workflows/native-server.yml'), 'utf8'));
  for (const event of ['push', 'pull_request']) {
    const paths = server.on[event].paths;
    assert.deepEqual(paths.filter(path => path.startsWith('native/')), serverPaths, event);
    for (const path of paths.filter(path => path.startsWith('native/'))) {
      const file = path.replace('/**', '/src/lib.rs');
      assert.ok(owners(file).includes('desktop'), `${path} is runic-server's`);
    }
  }
  const libraries = readFileSync(resolve(root, '.github/workflows/native-libraries.yml'), 'utf8');
  const key = /key=runic-server-\$\{\{ hashFiles\(([^)]*)\) \}\}/.exec(libraries)?.[1];
  assert.ok(key, 'native-libraries.yml computes the key');
  assert.deepEqual(key.split(', ').map(item => item.slice(1, -1)), ['native/runic-server/**', 'native/Cargo.toml', 'native/rust-toolchain.toml',
    'eng/native/build-runic-server.sh', '.github/workflows/native-libraries.yml']);
  // ci.yml's native job restores the linux-x64 library under the same key.
  const restore = workflow.jobs.native.steps.find(step => step.name === "Restore the native server's linux-x64 library");
  assert.equal(restore.with.key, `runic-server-\${{ hashFiles(${key}) }}-linux-x64`);
  // Each shipped library's notices file is in its crate's directory, so the filters and the key cover it.
  const crates = readFileSync(resolve(root, 'eng/native/crates.mjs'), 'utf8');
  const notices = [...crates.matchAll(/notices: "([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(notices, ['native/runic-server/THIRD-PARTY-NOTICES.md']);
  for (const path of notices) assert.ok(tracked(path).length === 1, path);
});

test('the CI scripts of platform steps belong to the component whose job runs them and plan its legs only', () => {
  for (const [file, component, legs, jobs] of [
    ['eng/ci/macos-voiceover.sh', 'platform', ['osx-arm64'], ['native']],
    ['eng/ci/macos-appearance.sh', 'platform', ['osx-arm64'], ['native']],
    ['eng/ci/macos-open-probe.sh', 'platform', ['osx-arm64'], ['native']],
    ['eng/ci/windows-nvda.ps1', 'platform', ['win-x64'], ['screen-readers']],
    ['eng/ci/windows-native-run.mjs', 'platform', ['win-x64', 'win-arm64'], ['native']],
    ['eng/ci/real-portal.sh', 'platform', [], ['native']],
    ['eng/ci/windows-dismiss-oobe.ps1', 'desktop', ['win-x64', 'win-arm64'], ['native', 'wpf']],
    ['eng/ci/native-musl.sh', 'desktop', [], ['native-musl']],
    ['eng/ci/windows-httpsys-isolation.ps1', 'application', ['win-x64', 'osx-arm64'], ['native']],
    ['eng/ci/hdiutil.sh', 'packaging', ['osx-arm64'], ['packaging']],
    ['eng/ci/packaging-probe-check.mjs', 'packaging', platforms, ['packaging']],
    ['eng/ci/packaging-rpm.sh', 'packaging', [], ['packaging']],
    ['eng/ci/windows-icon-check.mjs', 'packaging', platforms, ['packaging']],
  ]) {
    assert.deepEqual(ownedBy(file), [component], file);
    const result = plan([file]);
    assert.equal(result.full, false, file);
    assert.deepEqual(result.platforms, legs, file);
    for (const job of jobs) assert.ok(runs(result, job), `${file}: ${job}`);
  }
});


// Recorded `git diff --no-renames -U0` output of two real post-release commits: 07829c0b8 (#795, after 0.15.1: baselines
// and the Nix guide's tag) and eb0493963 (#1079, after 0.21.0: shipped API moves, baselines, suppressions removed and the
// version bump to 0.22.0 across manifests and lock files).
const fixture = name => resolve(root, 'eng/ci/fixtures/diff-kind', name);
const postRelease = {
  '0.15.1': readFileSync(fixture('post-release-0.15.1.diff'), 'utf8'),
  '0.21.0': gunzipSync(readFileSync(fixture('post-release-0.21.0.diff.gz'))).toString('utf8'),
};
const hunk = (path, removed, added, header = '') => [`diff --git a/${path} b/${path}`, header, `--- ${removed.length || !header.includes('new') ? `a/${path}` : '/dev/null'}`,
  `+++ ${added.length || !header.includes('deleted') ? `b/${path}` : '/dev/null'}`, '@@ -1 +1 @@', ...removed.map(line => `-${line}`), ...added.map(line => `+${line}`)]
  .filter(line => line !== '').join('\n') + '\n';

test('a real post-release diff is version-only, and a release or train step', () => {
  for (const [version, diff] of Object.entries(postRelease))
    assert.deepEqual(diffKind(diff), { versionOnly: true, release: 'it changes eng/Versions.props' }, version);
  // The 0.21.0 diff moves shipped API, deletes suppressions and rewrites versions in manifests and lock files.
  const paths = [...postRelease['0.21.0'].matchAll(/^diff --git a\/(\S+)/gm)].map(match => match[1]);
  for (const pattern of [/PublicAPI\.Shipped\.txt$/, /PublicAPI\.Unshipped\.txt$/, /CompatibilitySuppressions\.xml$/, /packages\.lock\.json$/,
    /^bun\.lock$/, /^eng\/workspace\.json$/, /^eng\/dependencies\/package-downloads\.json$/, /^packages\/web\/[^/]+\/package\.json$/])
    assert.ok(paths.some(path => pattern.test(path)), `${pattern} in the 0.21.0 diff`);
});

test('a diff that also touches code, a test or a toolchain pin is not version-only', () => {
  const diff = postRelease['0.21.0'];
  for (const [why, extra] of [
    ['code', hunk('packages/dotnet/Runic.Desktop/Window.cs', ['    const int Limit = 3;'], ['    const int Limit = 4;'])],
    ['a test', hunk('tests/dotnet/Runic.Desktop.Tests/WindowTests.cs', ['        Assert.Equal("0.20.0", version);'], ['        Assert.Equal("0.21.0", version);'])],
    ['the .NET SDK pin', hunk('global.json', ['    "version": "10.0.403",'], ['    "version": "10.0.404",'])],
    ['a third-party package', hunk('Directory.Packages.props', ['    <PackageVersion Include="TUnit" Version="1.2.3" />'], ['    <PackageVersion Include="TUnit" Version="1.2.4" />'])],
    ['a third-party lock entry', hunk('examples/status-icon/packages.lock.json', ['        "contentHash": "AAAA"'], ['        "contentHash": "BBBB"'])],
    ['a .NET runtime pack in the downloads', hunk('eng/dependencies/package-downloads.json',
      ['    "version": "10.0.14",', '    "contentHash": "AAAA"'], ['    "version": "10.0.15",', '    "contentHash": "BBBB"'])],
    ['an added line in a version file', hunk('eng/workspace.json', [], ['  "extra": true,'])],
    ['an edited suppression file', hunk('packages/dotnet/Runic.Desktop/CompatibilitySuppressions.xml', ['<a/>'], ['<b/>'])],
    ['a new Unshipped entry', hunk('packages/dotnet/Runic.Desktop/PublicAPI.Unshipped.txt', [], ['Runic.Desktop.Window.Extra() -> void'])],
    ['a Shipped line that Unshipped did not have', hunk('packages/dotnet/Runic.Desktop/PublicAPI.Shipped.txt', [], ['Runic.Desktop.Window.Other() -> void'])],
    ['an analyzer release file', hunk('packages/dotnet/Runic.Platform.Analyzers/AnalyzerReleases.Shipped.md', ['## Release 0.20.0'], ['## Release 0.21.0'])],
  ]) assert.equal(diffKind(diff + extra).versionOnly, false, why);
  assert.equal(diffKind('').versionOnly, false, 'an empty diff');
  assert.equal(diffKind(hunk('docs/guide.md', ['Runic 0.20.0 adds windows.'], ['Runic 0.21.0 adds menus.'])).versionOnly, false, 'prose');
});

test('version-only parts: shipped API moves, baselines, release notes and version numbers', () => {
  const baselines = { baselines: ['0.20.0'] };
  const move = hunk('packages/dotnet/Runic.Desktop/PublicAPI.Unshipped.txt', ['Runic.Desktop.Window.Show() -> void', '*REMOVED*Runic.Desktop.Window.Old() -> void'], [])
    + hunk('packages/dotnet/Runic.Desktop/PublicAPI.Shipped.txt', ['Runic.Desktop.Window.Old() -> void'], ['Runic.Desktop.Window.Show() -> void']);
  assert.deepEqual(diffKind(move, baselines), { versionOnly: true, release: null });
  assert.deepEqual(diffKind(hunk('eng/release/notes/0.21.0/fixes/12.md', [], ['- A fix.'], 'new file mode 100644'), baselines), { versionOnly: true, release: null });
  assert.deepEqual(diffKind(hunk('packages/dotnet/Runic.Desktop/CompatibilitySuppressions.xml', ['<a/>'], [], 'deleted file mode 100644'), baselines),
    { versionOnly: true, release: null });
  // A baseline package moves to a version eng/Versions.props names as a baseline (before the diff, or in it).
  const download = (from, to) => hunk('eng/dependencies/package-downloads.json', [`    "version": "${from}",`, '    "contentHash": "AAAA"'],
    [`    "version": "${to}",`, '    "contentHash": "BBBB"']);
  assert.equal(diffKind(download('0.19.0', '0.20.0'), baselines).versionOnly, true);
  assert.equal(diffKind(download('0.20.0', '0.21.0'), baselines).versionOnly, false);
  assert.equal(diffKind(download('0.20.0', '0.21.0') + hunk('eng/Versions.props',
    ['    <RunicPackageValidationBaselineVersion>0.20.0</RunicPackageValidationBaselineVersion>'],
    ['    <RunicPackageValidationBaselineVersion>0.21.0</RunicPackageValidationBaselineVersion>']), baselines).versionOnly, true);
  assert.equal(diffKind(hunk('eng/dependencies/package-downloads.json', ['    "contentHash": "AAAA"'], ['    "contentHash": "BBBB"']), baselines).versionOnly, false,
    'a hash without its version');
  for (const path of ['eng/workspace.json', 'bun.lock', 'packages/web/views/package.json', 'tests/dotnet/Runic.Desktop.Tests/packages.lock.json',
    'native/Cargo.toml', 'packages/web/views/src/version.ts', 'nix/README.md'])
    assert.equal(diffKind(hunk(path, ['  "version": "0.20.0",'], ['  "version": "0.21.0",']), baselines).versionOnly, true, path);
  assert.ok(versionFiles.some(pattern => pattern.test('tools/dotnet-runic/metadata/runic.compatibility-set.json')));
});

test('release and train steps: eng/Versions.props, a TrainCommit or assembled release notes', () => {
  const kind = text => diffKind(text, { baselines: [] }).release;
  assert.equal(kind(hunk('eng/release/notes/0.24.0.md', [], ['# Runic SDK 0.24.0'], 'new file mode 100644')),
    'it changes the assembled release notes eng/release/notes/0.24.0.md');
  assert.equal(kind(hunk('Directory.Packages.props', ['    <PackageVersion Include="Runic.Translations" Version="0.15.0" />'],
    ['    <PackageVersion Include="Runic.Translations" Version="0.15.1" TrainCommit="' + 'a'.repeat(40) + '" />'])),
  'it changes a TrainCommit in Directory.Packages.props');
  assert.equal(kind(hunk('Directory.Packages.props', ['    <PackageVersion Include="TUnit" Version="1.2.3" />'], ['    <PackageVersion Include="TUnit" Version="1.2.4" />'])), null);
  assert.equal(kind(hunk('eng/release/notes/0.24.0/fixes/12.md', [], ['- A fix.'])), null, 'a fragment');
  assert.equal(kind(hunk('eng/Versions.props', ['<A>1</A>'], ['<A>2</A>'])), 'it changes eng/Versions.props');
  // An older base planner prints a plan instead of a diff kind: neither kind.
  for (const text of ['', 'skip=[]', '[]', 'null', '{"versionOnly":"yes","release":3}'])
    assert.deepEqual(parseDiffKind(text), { versionOnly: false, release: null }, text);
  assert.deepEqual(parseDiffKind(cli(['--diff-kind'], postRelease['0.15.1'])), { versionOnly: true, release: 'it changes eng/Versions.props' });
});

test('a version-only pull request plans Linux and the Linux packaging legs; the label still plans every leg', () => {
  const decided = affected(['eng/Versions.props', 'bun.lock', 'packages/dotnet/Runic.Desktop/PublicAPI.Shipped.txt']);
  assert.deepEqual(decided.platforms, platforms, 'eng/Versions.props alone would plan every leg');
  const result = planFor(versionOnly(decided));
  assert.deepEqual(result.platforms, []);
  assert.match(result.why, /^no Windows or macOS leg: a version-only diff/);
  assert.deepEqual(result.packaging.include, packagingLegs.filter(leg => !leg.platform));
  assert.deepEqual(result.native.include, nativeLegs.filter(leg => !leg.platform));
  assert.deepEqual(result.skip, ['wpf']);
  // A shipped API move in one library adds the Linux packaging legs to that library's plan.
  const moved = planFor(versionOnly(affected(['packages/dotnet/Runic.Assets/PublicAPI.Shipped.txt', 'packages/dotnet/Runic.Assets/PublicAPI.Unshipped.txt'])));
  assert.deepEqual([moved.platforms, moved.packaging.include], [[], packagingLegs.filter(leg => !leg.platform)]);
  const cliVersionOnly = outputsOf(cli(['--plan', JSON.stringify(decided), '--version-only']));
  assert.deepEqual([cliVersionOnly.platforms, cliVersionOnly.skip], [[], ['wpf']]);
  const labelled = outputsOf(cli(['--plan', JSON.stringify(decided), '--all-platforms', 'label ci:all-platforms', '--version-only']));
  assert.deepEqual(labelled.platforms, platforms);
});

test('a push covered by a passed merge queue run runs only what that run skipped', () => {
  const url = `https://github.com/${repository}/actions/runs/21`;
  const linux = planFor(affected(['packages/dotnet/Runic.Platform.MacOS/MacOSPlatformProvider.cs']));
  // The same plan: nothing runs, build and engineering included, except the native server libraries' lookup, which
  // builds and caches a library only when main has no cache entry for it.
  assert.deepEqual(coveredSkip, reusedSkip.filter(job => job !== 'native-libraries'));
  const same = coveredPlan(linux, linux, url);
  assert.deepEqual(same.skip, coveredSkip);
  assert.deepEqual([same.platforms, same.managed, same.web, same.native, same.frameworks], [[], [], [], { include: [] }, false]);
  assert.equal(same.why, `covered by ${url}, which passed this commit with every job and leg this push plans`);
  // The queue ran a Linux-only plan of a web package, the push's diff plans the macOS provider too (a group of two entries).
  const queued = planFor(affected(['packages/web/svelte/src/index.ts']));
  const both = planFor(affected(['packages/web/svelte/src/index.ts', 'packages/dotnet/Runic.Platform.MacOS/MacOSPlatformProvider.cs']));
  const rest = coveredPlan(both, queued, url);
  assert.deepEqual(rest.platforms, ['osx-arm64']);
  assert.ok(rest.native.include.every(leg => !queued.native.include.some(done => done.rid === leg.rid)));
  assert.ok(!rest.web.includes('svelte'), 'the queue ran the svelte tests');
  assert.ok(rest.skip.includes('engineering') && !rest.skip.includes('build') && !rest.skip.includes('native-libraries'));
  assert.ok(!rest.skip.includes('native'));
  assert.match(rest.why, new RegExp(`^covered by ${url} except .*native.*\\(legs osx-arm64\\)$`));
  // A queue run that planned more covers less; one that reused a run (ran nothing) covers nothing.
  assert.deepEqual(coveredPlan(linux, plan(null), url).skip, coveredSkip);
  const nothing = coveredPlan(both, reusedPlan(url), url);
  assert.deepEqual(nothing.native, both.native);
  assert.deepEqual(skippableJobs.filter(job => !nothing.skip.includes(job)), skippableJobs.filter(job => !both.skip.includes(job)));
  // The command line: --covered plans the push's diff (not every Linux job) and subtracts.
  const decided = affected(['packages/dotnet/Runic.Platform.MacOS/MacOSPlatformProvider.cs']);
  const summary = resolve(mkdtempSync(resolve(tmpdir(), 'runic-ci-covered-')), 'summary.md');
  try {
    const covered = outputsOf(cli(['--plan', JSON.stringify(decided), '--covered', JSON.stringify({ url, plan: linux })], '', { GITHUB_STEP_SUMMARY: summary }));
    assert.deepEqual(covered.skip, coveredSkip);
    assert.match(readFileSync(summary, 'utf8'), new RegExp(`passed merge queue run ${url}\\. This push runs nothing else`));
  } finally { rmSync(dirname(summary), { recursive: true, force: true }); }
  const partial = outputsOf(cli(['--plan', JSON.stringify(decided), '--covered', JSON.stringify({ url, plan: queued })]));
  assert.deepEqual(partial.platforms, ['osx-arm64']);
  // An unusable value plans the push as one without a queue run: every Linux job.
  for (const value of ['', '{}', JSON.stringify({ url, plan: { ...linux, skip: ['unknown'] } }), JSON.stringify({ plan: linux })]) {
    assert.equal(parseCovered(value), null, value);
    const fallback = outputsOf(cli(['--plan', JSON.stringify(decided), '--covered', value]));
    assert.deepEqual([fallback.full, fallback.platforms], [true, ['osx-arm64']], value);
  }
  // No usable diff: everything is planned, then the covered part is subtracted.
  assert.deepEqual(outputsOf(cli(['--all', 'push run without a usable diff', '--covered', JSON.stringify({ url, plan: plan(null) })])).skip, coveredSkip);
});

test('every run records its plan for a later push of the same commit', () => {
  const directory = mkdtempSync(resolve(tmpdir(), 'runic-ci-record-'));
  try {
    const file = resolve(directory, 'ci-plan/plan.json');
    const decided = affected(['packages/web/svelte/src/index.ts']);
    cli(['--plan', JSON.stringify(decided)], '', { CI_PLAN_RECORD: file });
    const recorded = JSON.parse(readFileSync(file, 'utf8'));
    assert.deepEqual(recorded, planRecord(planFor(decided)));
    assert.ok(validPlan(recorded.plan));
    const url = `https://github.com/${repository}/actions/runs/11`;
    cli(['--reused', url], '', { CI_PLAN_RECORD: file });
    assert.deepEqual(JSON.parse(readFileSync(file, 'utf8')), planRecord(reusedPlan(url), url));
    // The base planner's calls record nothing.
    rmSync(file);
    cli(['--affected'], 'a.txt\0', { CI_PLAN_RECORD: file });
    cli(['--diff-kind'], '', { CI_PLAN_RECORD: file });
    assert.equal(existsSync(file), false);
  } finally { rmSync(directory, { recursive: true, force: true }); }
  const { plan: job } = workflow.jobs;
  const step = job.steps.find(item => item.id === 'plan');
  assert.equal(step.env.CI_PLAN_RECORD, '${{ runner.temp }}/ci-plan/plan.json');
  const upload = job.steps[job.steps.indexOf(step) + 1];
  assert.equal(upload.uses, workflow.jobs.build.steps.find(item => item.uses?.startsWith('actions/upload-artifact@')).uses);
  assert.equal(upload['continue-on-error'], true);
  assert.deepEqual(upload.with, { name: 'ci-plan', path: '${{ runner.temp }}/ci-plan/plan.json', 'if-no-files-found': 'error',
    'retention-days': 7, 'compression-level': 0 });
});

test('the plan step: version-only pull requests, release and train entries in the queue, covered pushes', () => {
  const pull = () => ({ PULL_REQUEST: '7', HEAD_REPOSITORY: repository, LABEL: allPlatformsLabel });
  const labels = (names = []) => () => ({ [`repos/${repository}/issues/7/labels`]: names.map(name => ({ name })) });
  const kind = value => shas => ({ ...pull(), STUB_DIFF_KIND: JSON.stringify(value) });
  const strip = text => text.split('\n').filter(line => !line.startsWith('tested-')).join('\n');
  // A pull request: the base planner's diff kind; version-only adds --version-only, a release kind does nothing here.
  assert.equal(strip(planStep('pull_request', kind({ versionOnly: true, release: 'it changes eng/Versions.props' }), ['a.txt', 'b.txt'], labels(), { merge: true })),
    'bun eng/ci/plan.mjs --plan b.txt --version-only');
  assert.equal(strip(planStep('pull_request', kind({ versionOnly: true, release: null }), ['a.txt', 'b.txt'], labels([allPlatformsLabel]), { merge: true })),
    'bun eng/ci/plan.mjs --plan b.txt --all-platforms label ci:all-platforms --version-only');
  for (const value of [{ versionOnly: false, release: 'it changes eng/Versions.props' }, 'skip=["wpf"]', ''])
    assert.equal(strip(planStep('pull_request', shas => ({ ...pull(), STUB_DIFF_KIND: typeof value === 'string' ? value : JSON.stringify(value) }),
      ['a.txt', 'b.txt'], labels(), { merge: true })), 'bun eng/ci/plan.mjs --plan b.txt', JSON.stringify(value));
  // A merge queue entry: a release or train step plans every leg and never reuses; version-only changes nothing.
  const queued = value => shas => ({ QUEUE_BASE: shas[0], QUEUE_HEAD: shas.at(-1), STUB_DIFF_KIND: JSON.stringify(value) });
  const reusable = ({ shas, tree }) => ({
    [`repos/${repository}/actions/artifacts?name=ci-passed-${shas[0]}-${tree}&per_page=100`]: { artifacts: [{ expired: false, workflow_run: { id: 11 } }] },
    [`repos/${repository}/actions/runs/11`]: { event: 'pull_request', path: '.github/workflows/ci.yml', head_repository: { full_name: repository },
      html_url: `https://github.com/${repository}/actions/runs/11` },
    [`repos/${repository}/actions/runs/11/jobs?per_page=100`]: { jobs: [{ name: 'verify', conclusion: 'success' }] },
  });
  assert.equal(planStep('merge_group', queued({ versionOnly: true, release: 'it changes eng/Versions.props' }), ['a.txt', 'b.txt'], reusable),
    'bun eng/ci/plan.mjs --plan b.txt --all-platforms a release or train step: it changes eng/Versions.props');
  assert.equal(planStep('merge_group', queued({ versionOnly: true, release: null }), ['a.txt', 'b.txt'], reusable),
    `bun eng/ci/plan.mjs --reused https://github.com/${repository}/actions/runs/11`);
  assert.equal(planStep('merge_group', queued({ versionOnly: true, release: null }), ['a.txt', 'b.txt']), 'bun eng/ci/plan.mjs --plan b.txt');
  // A push: a passed queue run of the commit replaces --all-linux with --covered; without one it plans as before.
  const covered = JSON.stringify({ url: 'https://github.com/r/actions/runs/21', plan: { skip: [] } });
  assert.equal(planStep('push', shas => ({ BEFORE: shas[0], STUB_COVERED: covered }), ['a.txt', 'b.txt']), `bun eng/ci/plan.mjs --plan b.txt --covered ${covered}`);
  assert.equal(planStep('push', shas => ({ BEFORE: 'f'.repeat(40), STUB_COVERED: covered }), ['a.txt', 'b.txt']),
    `bun eng/ci/plan.mjs --all push run without a usable diff --covered ${covered}`);
  assert.equal(planStep('push', shas => ({ BEFORE: shas[0] }), ['a.txt', 'b.txt']), 'bun eng/ci/plan.mjs --plan b.txt --all-linux');
  assert.equal(planStep('push', shas => ({ BEFORE: 'f'.repeat(40) }), ['a.txt', 'b.txt']), 'bun eng/ci/plan.mjs --all push run without a usable diff');
  // Schedules and dispatches never look a queue run up.
  assert.equal(planStep('schedule', () => ({ STUB_COVERED: covered }), ['a.txt']), 'bun eng/ci/plan.mjs --all schedule run');
});

// R2 (D077.26): job-scoped planning for ci.yml, its actions and CI scripts, and the release and pack scripts.
const ciText = readFileSync(resolve(root, workflowPath), 'utf8');
const gitGrep = key => {
  try { return execFileSync('git', ['grep', '-l', '-F', '-e', key], { cwd: root, encoding: 'utf8' }).split('\n').filter(Boolean); }
  catch (error) { return error.status === 1 ? [] : null; }
};
// The tracked JavaScript files that import `file`, as checkoutContext and revisionContext find them.
const gitImporters = file => gitGrep(file.split('/').pop())?.filter(path => scriptFile(path)
  && importsOf(path, readFileSync(resolve(root, path), 'utf8')).includes(file)) ?? null;
const context = (head, base = ciText) => ({ base, head, referrers: gitGrep, importers: gitImporters });
// Replaces `pattern` in job `job`'s block (or, without a job, in the text outside the jobs).
const editJob = (job, pattern, replacement, text = ciText) => {
  const lines = text.split('\n');
  const start = lines.findIndex(line => line === `  ${job}:`);
  assert.ok(start > 0, job);
  let end = lines.findIndex((line, index) => index > start && /^ {0,2}[A-Za-z_]/.test(line));
  if (end < 0) end = lines.length;
  const block = lines.slice(start, end).join('\n');
  assert.match(block, pattern, `${job} ${pattern}`);
  return [...lines.slice(0, start), block.replace(pattern, replacement), ...lines.slice(end)].join('\n');
};
const ran = result => skippableJobs.filter(job => runs(result, job));
const macosJobs = result => [...result.native.include, ...result.packaging.include].filter(leg => leg.platform === 'osx-arm64').length;
const plansEverything = (result, label) => {
  assert.equal(result.full, true, label);
  assert.deepEqual(result.skip, [], label);
  assert.deepEqual(result.platforms, platforms, label);
};

test('the planner reads the same ci.yml jobs and needs as a YAML parser, without comments', () => {
  const parts = workflowParts(ciText);
  assert.deepEqual([...parts.jobs.keys()], Object.keys(workflow.jobs));
  for (const [name, job] of Object.entries(workflow.jobs))
    assert.deepEqual(parts.needs.get(name), job.needs === undefined ? [] : [job.needs].flat(), name);
  // Comment lines and blank lines outside block scalars are not part of a job; a `#` line in a run script is.
  assert.deepEqual(workflowParts(editJob('native', /\n {4}runs-on:/, '\n    # a new comment\n\n    runs-on:')), parts);
  const script = workflowParts(editJob('managed', /\n(\s+)(bun eng\/ci\/test-managed\.mjs)/, '\n$1# a shell comment\n$1$2'));
  assert.notEqual(script.jobs.get('managed'), parts.jobs.get('managed'));
  // The other shapes of needs.
  const listed = workflowParts('on: push\njobs:\n  a:\n    runs-on: x\n  b:\n    needs:\n      - a\n    runs-on: x\n  c:\n    needs: a\n');
  assert.deepEqual([...listed.needs], [['a', []], ['b', ['a']], ['c', ['a']]]);
  for (const text of ['', 'jobs:\n\ta:\n', 'on: push\n', '  indented: true\njobs:\n  a:\n', 'jobs:\n  a:\n  a:\n', null])
    assert.equal(workflowParts(text), null, JSON.stringify(text));
});

test('a ci.yml edit plans only the jobs whose definition changed, each with all its legs', () => {
  const edit = (job, pattern = /timeout-minutes: (\d+)/, replacement = 'timeout-minutes: 999') => editJob(job, pattern, replacement);
  for (const [job, legs, macos] of [['templates', [], 0], ['managed', [], 0], ['native', platforms, 2], ['packaging', ['win-x64', 'osx-arm64'], 1],
    ['wpf', ['win-x64', 'win-arm64'], 0], ['screen-readers', ['win-x64'], 0], ['native-musl', [], 0], ['webkit-cdp', [], 0]]) {
    const decided = affected([workflowPath], context(edit(job)));
    assert.deepEqual(Object.keys(decided.jobs), [job], job);
    assert.equal(decided.full, false, job);
    const result = planFor(decided);
    assert.deepEqual(ran(result), [job], job);
    assert.deepEqual(result.platforms, legs, job);
    assert.equal(macosJobs(result), macos, job);
    if (job === 'native') assert.deepEqual(result.native.include, nativeLegs);
    if (job === 'packaging') assert.deepEqual(result.packaging.include, packagingLegs);
    if (job === 'managed') assert.deepEqual(result.managed, managedGroups);
    if (legs.length) assert.match(result.why, new RegExp(`^${legs[0]}: \\.github/workflows/ci\\.yml \\(job ${job}\\)`));
  }
  // A changed matrix is part of the job.
  assert.deepEqual(Object.keys(affected([workflowPath], context(edit('templates', /first: react/, 'first: vue'))).jobs), ['templates']);
  // The web job runs every web package and the frameworks; node-26 runs the Node packages, which the web job then runs too.
  const web = planFor(affected([workflowPath], context(edit('web'))));
  assert.deepEqual([web.web, web.frameworks, ran(web)], [webTests().map(item => item.package), true, ['web', 'node-26']]);
  const node = planFor(affected([workflowPath], context(edit('node-26'))));
  assert.deepEqual([node.web, ran(node)], [nodePackages(), ['web', 'node-26']]);
  // Comments, and an always-run job (engineering), plan no skippable job.
  for (const head of [editJob('native', /\n {4}runs-on:/, '\n    # only a comment\n    runs-on:'), edit('engineering')]) {
    const result = planFor(affected([workflowPath], context(head)));
    assert.deepEqual([result.full, result.skip, result.platforms], [false, skippableJobs, []]);
  }
  // The other changed files plan as usual, on top of the changed jobs.
  const mixed = planFor(affected([workflowPath, 'packages/web/svelte/src/index.ts', 'docs/README.md'], context(edit('wpf'))));
  assert.deepEqual(ran(mixed), [...new Set([...ran(plan(['packages/web/svelte/src/index.ts'])), 'wpf'])].filter(job => skippableJobs.includes(job))
    .sort((a, b) => skippableJobs.indexOf(a) - skippableJobs.indexOf(b)));
  assert.deepEqual(mixed.platforms, ['win-x64', 'win-arm64']);
  // A job that now needs another skippable job plans that one too.
  const needs = affected([workflowPath], context(editJob('views', /needs: \[plan, build\]/, 'needs: [plan, build, templates]')));
  assert.deepEqual(Object.keys(needs.jobs), ['views', 'templates']);
  // Without a context (the plan step without a separate base checkout, an older caller), ci.yml plans everything.
  plansEverything(plan([workflowPath]), 'no context');
});

test('shared parts of ci.yml, and edits the planner cannot compare, plan everything', () => {
  const top = (pattern, replacement) => { assert.match(ciText, pattern); return ciText.replace(pattern, replacement); };
  for (const [label, head] of [
    ['on', top(/\n {4}branches: \[main\]/, '\n    branches: [main, next]')],
    ['env', top(/\n {2}CONFIGURATION: Release/, '\n  CONFIGURATION: Debug')],
    ['defaults', top(/\njobs:\n/, '\ndefaults:\n  run:\n    shell: bash\njobs:\n')],
    ['concurrency', top(/cancel-in-progress: \$\{\{ github.event_name == 'pull_request' \}\}/, 'cancel-in-progress: false')],
    ['permissions', top(/^permissions:\n {2}contents: read/m, 'permissions:\n  contents: write')],
    ['name', top(/^name: SDK CI$/m, 'name: SDK CI 2')],
    ['plan job', editJob('plan', /timeout-minutes: \d+/, 'timeout-minutes: 9')],
    ['verify job', editJob('verify', /run: bun eng\/ci\/gate\.mjs/, 'run: bun eng/ci/gate.mjs --strict')],
    ['build job, which other jobs need', editJob('build', /timeout-minutes: \d+/, 'timeout-minutes: 99')],
    ['native-libraries job, which build needs', editJob('native-libraries', /uses: \.\/\.github\/workflows\/native-libraries\.yml/, 'uses: ./.github/workflows/native-libraries.yml # v2\n    with:\n      all: true')],
    ['an added job', top(/\n {2}verify:\n/, '\n  extra:\n    runs-on: ubuntu-24.04\n    steps:\n      - run: true\n  verify:\n')],
    ['a removed job', editJob('webkit-cdp', /[^]*/, '  webkit-cdp-renamed:\n    runs-on: ubuntu-24.04')],
    ['an unparseable head', `${ciText}\n\tbroken`],
    ['the same text although the diff lists ci.yml', ciText],
  ]) plansEverything(planFor(affected([workflowPath], context(head))), label);
  for (const value of [{ base: null, head: ciText }, { base: ciText, head: null }])
    plansEverything(planFor(affected([workflowPath], { ...value, referrers: gitGrep })), JSON.stringify(Object.keys(value)));
  // A release or train input in the same diff: the queue run stands for the green main run (D077.22).
  const native = editJob('native', /timeout-minutes: \d+/, 'timeout-minutes: 999');
  for (const file of ['eng/Versions.props', 'Directory.Packages.props', 'eng/release/notes/0.23.0.md']) {
    const decided = affected([workflowPath, file], context(native));
    assert.equal(decided.jobs, undefined, file);
    assert.equal(decided.full, file !== 'eng/release/notes/0.23.0.md' || decided.full, file);
    plansEverything(planFor(affected([workflowPath, file], context(native))), file);
  }
});

test('actions and CI scripts plan the jobs that name them; those of shared jobs, or named elsewhere, plan everything', () => {
  const scoped = file => {
    const decided = affected([file], context(ciText));
    return decided.full ? null : Object.keys(decided.jobs).sort();
  };
  const named = key => Object.entries(workflow.jobs).filter(([, job]) => JSON.stringify(job).includes(key)).map(([name]) => name);
  // An action that only test jobs use: those jobs, directly or through another action.
  const browser = scoped('.github/actions/install-browser/action.yml');
  assert.ok(browser.includes('native') && browser.includes('templates') && !browser.includes('managed'), browser.join());
  assert.deepEqual(scoped('.github/actions/restore-build/action.yml'), named('./.github/actions/restore-build').filter(job => skippableJobs.includes(job)).sort());
  // A neutral CI script plans its jobs' Linux legs only.
  assert.deepEqual(scoped('eng/ci/test-managed.mjs'), ['managed']);
  const sandbox = planFor(affected(['eng/ci/permit-webkit-sandbox.sh'], context(ciText)));
  assert.deepEqual(sandbox.platforms, []);
  assert.ok(runs(sandbox, 'webkit-cdp') && !runs(sandbox, 'managed'));
  // Used by plan or build (setup-sdk, its dependency cache, the build archive), named by MSBuild or test support files
  // (flakes.json, quarantine.mjs), by a script outside eng/ci, or by nothing at all: everything, as before.
  for (const file of ['.github/actions/setup-sdk/action.yml', '.github/actions/dependency-cache/action.yml', '.github/actions/download-artifact/action.yml',
    'eng/ci/source-state.mjs', 'eng/ci/build-artifact.mjs', 'eng/ci/train-feed.mjs', 'eng/ci/flakes.json', 'eng/ci/quarantine.mjs', 'eng/ci/retry.sh']) {
    assert.equal(scoped(file), null, file);
    plansEverything(plan([file]), file);
  }
  assert.equal(scoped('eng/ci/candidate-feed.mjs'), null, 'no job runs it');
  // A failed lookup leaves the file unattributed.
  assert.equal(affected(['eng/ci/test-managed.mjs'], { base: ciText, head: ciText, referrers: () => null }).full, true);
});

test('release and pack scripts plan the jobs that test the packed packages', () => {
  // The jobs that download the packages the build job packs.
  const packageJobs = Object.entries(workflow.jobs).filter(([name, job]) => name !== 'build'
    && (job.steps ?? []).some(step => step.with?.name === packageArtifact)).map(([name]) => name);
  assert.deepEqual(packageJobs, ['package-consumers', 'wpf', 'templates', 'webkit-cdp']);
  for (const file of ['eng/release/notes.mjs', 'eng/release/stage.mjs', 'eng/release/nuget-policy.json', 'eng/release/template-locks.mjs']) {
    const decided = affected([file], context(ciText));
    assert.deepEqual(Object.keys(decided.jobs), packageJobs, file);
    const result = planFor(decided);
    // Release tooling is neutral (no Windows or macOS job reads it), so WPF, which has no Linux leg, does not run.
    assert.deepEqual(ran(result), ['package-consumers', 'templates', 'webkit-cdp'], file);
    assert.deepEqual(result.platforms, [], file);
  }
  // #1302: the WPF job's packed consumer smoke imports eng/verify-packages.mjs and runs on win-x64 only, so a change to it
  // runs that leg as well.
  const verifier = affected(['eng/verify-packages.mjs'], context(ciText));
  assert.deepEqual(Object.keys(verifier.jobs), packageJobs);
  assert.deepEqual(verifier.jobs.wpf, { platforms: ['win-x64'], file: 'eng/verify-packages.mjs',
    why: { 'win-x64': 'eng/verify-packages.mjs (imported by tests/fixtures/navigation/wpf-consumer/package-smoke.mjs)' } });
  assert.deepEqual(parseAffected(JSON.stringify(verifier)), verifier);
  const verified = planFor(verifier);
  assert.deepEqual([ran(verified), verified.platforms], [packageJobs, ['win-x64']]);
  assert.deepEqual(verified.wpf.include, wpfLegs.filter(leg => leg.rid === 'win-x64'));
  assert.equal(macosJobs(verified), 0);
  // eng/version.mjs is a shared input with every leg: the package jobs with their Windows legs.
  const version = planFor(affected(['eng/version.mjs'], context(ciText)));
  assert.deepEqual([ran(version), version.platforms], [packageJobs, ['win-x64', 'win-arm64']]);
  assert.deepEqual(version.wpf.include, wpfLegs);
  // Without a job that downloads the packages, they plan everything.
  const unpacked = ciText.replaceAll(packageArtifact, 'renamed-${{ github.run_id }}');
  assert.equal(affected(['eng/release/notes.mjs'], { base: unpacked, head: unpacked, referrers: gitGrep }).full, true);
  // Engineering-only files (tests, notes fragments) still plan nothing.
  assert.deepEqual(plan(['eng/release/notes.test.mjs', 'eng/release/notes/0.23.0/fixes/1.md']).skip, skippableJobs);
  for (const path of packScripts) assert.ok(tracked(path).length > 0, path);
});

test('a release or pack script plans the jobs that run a module importing it, with that module\'s legs', () => {
  const script = 'eng/release/notes.mjs', smoke = 'tests/fixtures/navigation/wpf-consumer/package-smoke.mjs';
  const helper = 'tests/fixtures/navigation/package-helper.mjs';
  const decide = importers => affected([script], { ...context(ciText), importers });
  // A new importer that a job names is planned without editing the planner, through modules no job names too.
  for (const importers of [file => file === script ? [smoke] : [], file => ({ [script]: [helper], [helper]: [smoke] })[file] ?? []]) {
    const decided = decide(importers);
    assert.deepEqual(decided.jobs.wpf.platforms, ['win-x64']);
    assert.match(decided.jobs.wpf.why['win-x64'], /^eng\/release\/notes\.mjs \(imported by tests\/fixtures\/navigation\/wpf-consumer\/package-smoke\.mjs\)$/);
    assert.deepEqual(planFor(decided).wpf.include, wpfLegs.filter(leg => leg.rid === 'win-x64'));
  }
  // The lookup stops at shared inputs (eng/run.mjs, which every job imports) and skips engineering-only and CI files.
  const stopped = decide(file => ({ [script]: ['eng/run.mjs', 'eng/ci/plan.mjs', 'eng/release/notes.test.mjs'],
    'eng/run.mjs': [smoke], 'eng/ci/plan.mjs': [smoke], 'eng/release/notes.test.mjs': [smoke] })[file] ?? []);
  assert.deepEqual(stopped.jobs.wpf.platforms, []);
  // A failed or missing lookup leaves the script unattributed.
  for (const importers of [() => null, file => file === script ? [helper] : null, undefined])
    assert.equal(decide(importers).full, true, String(importers));
  // Both contexts find the importers by their import statements, not by any mention.
  const found = revisionContext('HEAD').importers('eng/verify-packages.mjs');
  for (const file of [smoke, 'tests/fixtures/navigation/reactiveui-consumer/package-smoke.mjs', 'eng/run.mjs']) assert.ok(found.includes(file), file);
  for (const file of ['package.json', 'eng/release/workflow.test.mjs', 'specs/desktop/conformance/cs-webui-example-parity.json'])
    assert.ok(!found.includes(file), file);
  assert.deepEqual(importsOf('a/b/c.mjs', [`import { x } from "../d.mjs";`, `const y = await import('./e.mjs');`, `export * from "../../f.js";`,
    `import "./g.mjs";`, `import fs from "node:fs";`, `// see "./h.mjs"`].join('\n')), ['a/d.mjs', 'a/b/e.mjs', 'f.js', 'a/b/g.mjs']);
});

// #1302: every tracked module that imports a release or pack script either runs in a job that the lookup above finds
// (a job names it or a module importing it), or is listed here with where it runs. A new importer that runs on a
// Windows or macOS leg some other way (a shell script, a package.json script) fails until the planner covers it.
const packScriptImportersOutsideJobs = {
  // The shared runner: the build job packs with it, and package-consumers runs its verify-packages command.
  'eng/run.mjs': 'build, package-consumers',
  // bun run verify:templates in the Linux templates and webkit-cdp jobs.
  'eng/verify-templates.mjs': 'templates, webkit-cdp',
  // Maintainer and release commands that CI does not run.
  'eng/dependencies/update-template-locks.mjs': 'maintainers',
  // The release tooling's own modules: release.yml and the build job (Linux).
  'eng/release/*': 'release, build',
  // Template checks run by Test-Templates.sh and Test-NixTemplate.sh in the Linux templates job and nix.yml.
  'tests/templates/template-npm-registry.mjs': 'templates (Linux)', 'tests/templates/bind-template-candidate-integrities.mjs': 'templates (Linux)',
};
test('every module importing a release or pack script runs where the planner finds it', () => {
  const blocks = Object.values(workflow.jobs).map(job => JSON.stringify(job));
  const named = file => blocks.some(block => block.includes(file));
  const reaches = (file, seen = new Set()) => {
    if (named(file)) return true;
    if (seen.has(file) || globalPaths.some(path => matchesPath(file, path))) return false;
    seen.add(file);
    return gitImporters(file).some(importer => !engineeringOnly(importer) && reaches(importer, seen));
  };
  const scripts = tracked(...packScripts).filter(scriptFile);
  const unplanned = [...new Set(scripts.flatMap(gitImporters))]
    .filter(file => !engineeringOnly(file) && !file.startsWith('eng/ci/') && !scripts.includes(file) && !reaches(file))
    .filter(file => !Object.keys(packScriptImportersOutsideJobs).some(path => matchesPath(file, path)));
  assert.deepEqual(unplanned, []);
  for (const path of Object.keys(packScriptImportersOutsideJobs)) assert.ok(tracked(path.replace(/\*$/, '')).length > 0, path);
});

// Real squash commits of November 2026 that edit ci.yml or the release scripts: the base and head ci.yml, the changed
// files and the referrer lookups the planner made at that commit. Expected: the jobs scoped (null: every job, as before),
// the skippable jobs that run, the Windows and macOS platforms, and the macOS jobs (before: each planned every job,
// and all but #1264 every leg). The macOS jobs count the two osx-arm64 shards (R1) and Packaging / macos.
const workflowEdits = JSON.parse(brotliDecompressSync(readFileSync(resolve(root, 'eng/ci/fixtures/workflow-edits/ci-yml-prs.json.br'))).toString('utf8')).fixtures;
const everyJob = skippableJobs;
// The base and head platforms.mjs of the pull requests that changed it (#1288 and #1033 above, #1308), and #1308's files and
// ci.yml texts.
const platformsEdits = JSON.parse(brotliDecompressSync(readFileSync(resolve(root, 'eng/ci/fixtures/workflow-edits/platforms-mjs.json.br'))).toString('utf8')).fixtures;
test('this month\'s ci.yml and release script pull requests plan their changed jobs', () => {
  const expected = {
    // C4: the managed job's step and its script.
    1292: [['managed'], ['managed'], [], 0],
    // C2: the native job's shard split (platforms.mjs is the CI component's).
    1288: [['native'], ['native'], platforms, 2],
    // The windows-11-arm account prompt step in the WPF and native jobs, with Desktop and WPF test changes.
    1066: [['wpf', 'native'], everyJob.filter(job => job !== 'webkit-cdp'), platforms, 3],
    // hdiutil retries: the packaging job's macOS steps, with dotnet runic packaging code.
    1019: [['packaging'], everyJob.filter(job => job !== 'screen-readers'), platforms, 3],
    // dotnet runic dev --browser: the webkit-cdp job, with tool changes.
    1053: [['webkit-cdp'], everyJob.filter(job => job !== 'screen-readers'), ['win-x64'], 0],
    // doctor: the templates job, with tool changes.
    1095: [['templates'], everyJob.filter(job => !['wpf', 'screen-readers'].includes(job)), [], 0],
    // Views single-file publish: the views job, with a Views targets change.
    1124: [['views'], everyJob.filter(job => !['screen-readers', 'webkit-cdp'].includes(job)), ['win-x64', 'osx-arm64'], 3],
    // WebKit CDP adapter: only comments changed in ci.yml.
    1072: [[], everyJob.filter(job => !['wpf', 'screen-readers'].includes(job)), [], 0],
    // Release notes anchors: a release script and its test.
    1264: [['package-consumers', 'wpf', 'templates', 'webkit-cdp'], ['package-consumers', 'templates', 'webkit-cdp'], [], 0],
    // Shared parts: the plan job (A1), the verify job (#1033), the build job and eng/run.mjs (#1105), setup-sdk's
    // dependency cache (#1130), and assembled release notes in the same diff (#928).
    1280: [null, everyJob, platforms, 3],
    1033: [null, everyJob, platforms, 3],
    1105: [null, everyJob, platforms, 3],
    1130: [null, everyJob, platforms, 3],
    928: [null, everyJob, platforms, 3],
  };
  assert.deepEqual(workflowEdits.map(item => item.pr).sort(), Object.keys(expected).map(Number).sort());
  for (const item of workflowEdits) {
    const [jobs, jobsRun, legs, macos] = expected[item.pr];
    // Before: every job; release tooling is neutral, so #1264 planned no Windows or macOS leg (and no WPF).
    const before = plan(item.files);
    if (item.pr === 1264) assert.deepEqual([before.full, before.skip, before.platforms], [true, ['wpf'], []]);
    else plansEverything(before, `${item.pr} before`);
    const definitions = platformsEdits.find(edit => edit.pr === item.pr);
    assert.equal(Boolean(definitions), item.files.includes(platformsPath), `${item.pr}`);
    // The fixture records the referrers of its scripts; the readers of a platforms.mjs definition come from this checkout.
    const decided = affected(item.files, { base: item.base, head: item.head, referrers: key => item.referrers[key] ?? (definitions ? gitGrep(key) : null),
      importers: gitImporters, texts: path => path === platformsPath ? definitions.platforms : null });
    assert.deepEqual(decided.jobs ? Object.keys(decided.jobs) : null, jobs, `${item.pr} ${item.title}`);
    const result = planFor(decided);
    assert.deepEqual(ran(result), jobsRun, `${item.pr}`);
    assert.deepEqual(result.platforms, legs, `${item.pr}`);
    assert.equal(macosJobs(result), macos, `${item.pr}`);
  }
});

test('pushes to main, the nightly run and release and train queue runs plan as before', () => {
  const native = editJob('native', /timeout-minutes: \d+/, 'timeout-minutes: 999');
  const decided = affected([workflowPath, 'packages/web/svelte/src/index.ts'], context(native));
  assert.deepEqual(Object.keys(decided.jobs), ['native']);
  assert.deepEqual(decided.unscoped, affected([workflowPath, 'packages/web/svelte/src/index.ts']));
  assert.deepEqual(unscoped(decided), decided.unscoped);
  const scoped = JSON.stringify(decided);
  const before = JSON.stringify(decided.unscoped);
  // Pushes: --all-linux, and --covered with or without a usable queue run, plan exactly as the unscoped result does.
  const covered = JSON.stringify({ url: 'https://github.com/r/actions/runs/21', plan: planFor(decided) });
  for (const options of [['--all-linux'], ['--covered', covered], ['--covered', 'not json']]) {
    const now = cli(['--plan', scoped, ...options]);
    assert.equal(now, cli(['--plan', before, ...options]), options[0]);
    if (options[1] !== covered) plansEverything(outputsOf(now), options.join(' '));
  }
  // The covered push runs what the scoped queue run skipped.
  assert.notDeepEqual(outputsOf(cli(['--plan', scoped, '--covered', covered])).skip, coveredSkip);
  // A release or train merge queue entry (the plan step's --all-platforms reason) runs every job and leg.
  const release = outputsOf(cli(['--plan', scoped, '--all-platforms', 'a release or train step: it changes eng/Versions.props']));
  plansEverything(release, 'release');
  assert.deepEqual(release.native, { include: nativeLegs });
  // A labelled pull request keeps the scoping and adds the legs.
  const labelled = outputsOf(cli(['--plan', scoped, '--all-platforms', `label ${allPlatformsLabel}`]));
  assert.equal(labelled.full, false);
  assert.deepEqual(labelled.platforms, platforms);
  // The nightly and manual runs have no diff, so they plan everything.
  plansEverything(outputsOf(cli(['--all', 'schedule run'])), 'schedule');
  // The plan step passes exactly these options.
  const step = workflow.jobs.plan.steps.find(item => item.id === 'plan').run;
  assert.match(step, /options=\(--all-platforms "a release or train step: \$release"\)/);
  assert.match(step, /options=\(--all-linux\)/);
  assert.match(step, /options=\(--covered "\$covered"\)/);
  assert.match(step, /git diff --name-only --no-renames -z "\$base" HEAD \| bun "\$RUNNER_TEMP\/base\/eng\/ci\/plan\.mjs" --affected/);
});

test('the base planner scopes against the head checkout in its working directory only', () => {
  const head = mkdtempSync(resolve(tmpdir(), 'runic-ci-r2-head-'));
  try {
    mkdirSync(resolve(head, '.github/workflows'), { recursive: true });
    writeFileSync(resolve(head, workflowPath), editJob('packaging', /timeout-minutes: \d+/, 'timeout-minutes: 999'));
    const run = cwd => {
      const result = spawnSync(process.execPath, [resolve(root, 'eng/ci/plan.mjs'), '--affected'], { cwd, input: `${workflowPath}\0`, encoding: 'utf8' });
      assert.equal(result.status, 0, result.stderr);
      return JSON.parse(result.stdout);
    };
    const decided = run(head);
    assert.deepEqual(Object.keys(decided.jobs), ['packaging']);
    assert.deepEqual(parseAffected(JSON.stringify(decided)), decided);
    const planned = outputsOf(cli(['--plan', JSON.stringify(decided)]));
    assert.deepEqual(planned.skip, skippableJobs.filter(job => job !== 'packaging'));
    // In its own checkout (no separate head) it does not scope.
    assert.equal(run(root).full, true);
    assert.equal(checkoutContext(root, root), null);
  } finally { rmSync(head, { recursive: true, force: true }); }
});

test('a malformed scoped result plans everything', () => {
  const decided = affected([workflowPath], context(editJob('views', /timeout-minutes: \d+/, 'timeout-minutes: 999')));
  for (const value of [{ ...decided, jobs: { unknown: { platforms: [], why: {} } } }, { ...decided, jobs: [] },
    { ...decided, jobs: { views: { platforms: ['win-x64'], why: {} } } }, { ...decided, unscoped: undefined },
    { ...decided, unscoped: { ...decided.unscoped, components: ['renamed'] } }, { ...decided, unscoped: decided }])
    assert.equal(parseAffected(JSON.stringify(value)), null, JSON.stringify(value).slice(0, 120));
  assert.match(cli(['--plan', JSON.stringify({ ...decided, jobs: [] })]), /^full=true$/m);
});

// #1312: a change to a leg definition in platforms.mjs plans the jobs whose matrix it is, with all their legs.
const platformsText = readFileSync(resolve(root, platformsPath), 'utf8');
const definitionContext = (head, base = platformsText, workflowHead = editJob('native', /(# The planned legs) of nativeLegs/, '$1 (edited) of nativeLegs')) =>
  ({ ...context(workflowHead), texts: path => path === platformsPath ? [base, head] : null });
// Replaces `pattern` in platforms.mjs's text.
const editPlatforms = (pattern, replacement, text = platformsText) => {
  assert.match(text, pattern);
  return text.replace(pattern, replacement);
};

test('every leg list of platforms.mjs is the matrix of one job, which a change to the list plans', () => {
  const lists = Object.entries(platformDefinitions).filter(([, value]) => Array.isArray(value) && value.length
    && value.every(item => item && typeof item === 'object' && typeof item.os === 'string'));
  // A new leg list needs an entry in legLists (eng/ci/plan.mjs), which plans its job and carries its plan output.
  assert.deepEqual(lists.map(([name]) => name).sort(), Object.values(legLists).map(definitionName).sort());
  for (const [output, list] of Object.entries(legLists)) {
    assert.ok(skippableJobs.includes(output), output);
    assert.deepEqual(Object.entries(workflow.jobs).filter(([, job]) => job.strategy?.matrix === `\${{ fromJSON(needs.plan.outputs.${output}) }}`)
      .map(([name]) => name), [output], output);
    const name = definitionName(list);
    const decided = affected([platformsPath], definitionContext(editPlatforms(new RegExp(`(export const ${name} = \\[[^{]*\\{ )`), '$1extra: 1, ')));
    assert.deepEqual(Object.keys(decided.jobs), [output], name);
    const result = planFor(decided);
    assert.deepEqual(ran(result), [output], name);
    assert.deepEqual(result[output].include, list, name);
  }
});

test('#1308\'s leg definition change plans the native job on all its legs', () => {
  const item = platformsEdits.find(edit => edit.pr === 1308);
  assert.ok(item.files.includes(platformsPath) && item.files.includes(workflowPath));
  assert.deepEqual(changedDefinitions(...item.platforms), ['nativeLegs', 'releaseJobs']);
  const decided = affected(item.files, { base: item.workflow[0], head: item.workflow[1], referrers: gitGrep, importers: gitImporters,
    texts: path => path === platformsPath ? item.platforms : null });
  assert.deepEqual(Object.keys(decided.jobs), ['native']);
  assert.deepEqual(decided.jobs.native.platforms, platforms);
  const result = planFor(decided);
  assert.deepEqual(ran(result), ['native']);
  assert.deepEqual(result.native.include, nativeLegs);
  assert.deepEqual(result.platforms, platforms);
  // Before #1312 (the CI component alone, and a ci.yml comment edit): no job.
  assert.deepEqual(ran(planFor(affected(item.files.filter(file => file !== platformsPath), { ...context(item.workflow[1], item.workflow[0]) }))), []);
  // Alone, without the ci.yml comment edit, it plans the same.
  const alone = planFor(affected([platformsPath], { ...context(ciText, ciText), texts: () => item.platforms }));
  assert.deepEqual([ran(alone), alone.native.include], [['native'], nativeLegs]);
  // The base planner's result, through the CLI as the plan step runs it: pull request, labelled, and push.
  const scoped = JSON.stringify(decided);
  const pull = outputsOf(cli(['--plan', scoped]));
  assert.deepEqual([pull.skip, pull.native.include], [skippableJobs.filter(job => job !== 'native'), nativeLegs]);
  const labelled = outputsOf(cli(['--plan', scoped, '--all-platforms', `label ${allPlatformsLabel}`]));
  assert.deepEqual([labelled.skip, labelled.native.include], [pull.skip, nativeLegs]);
  // A push without a covering queue run runs every job and, for the leg definition, every leg.
  const push = outputsOf(cli(['--plan', scoped, '--all-linux']));
  plansEverything(push, 'push');
  assert.deepEqual(push.native.include, nativeLegs);
  // The same through the checkout context the plan step uses: the base checkout's planner against the head.
  const head = mkdtempSync(resolve(tmpdir(), 'runic-ci-1312-head-'));
  try {
    mkdirSync(resolve(head, '.github/workflows'), { recursive: true });
    mkdirSync(resolve(head, 'eng/ci'), { recursive: true });
    writeFileSync(resolve(head, workflowPath), editJob('native', /(# The planned legs) of nativeLegs/, '$1 (edited) of nativeLegs'));
    writeFileSync(resolve(head, platformsPath), editPlatforms(/(export const nativeLegs = \[\n)/, '$1  { os: "windows-11-arm", rid: "win-arm64", shard: "extra", provider: "Windows", extension: ".exe", cswebui: false, platform: "win-arm64" },\n'));
    const found = checkoutContext(root, head).texts(platformsPath);
    assert.deepEqual(changedDefinitions(...found), ['nativeLegs', 'releaseJobs']);
  } finally { rmSync(head, { recursive: true, force: true }); }
});

test('a path rule, label or comment change in platforms.mjs plans no job, as before', () => {
  const unchanged = { full: false, skip: skippableJobs, platforms: [] };
  const plans = head => {
    const result = planFor(affected([platformsPath], definitionContext(head, platformsText, ciText)));
    return { full: result.full, skip: result.skip, platforms: result.platforms };
  };
  for (const [label, head] of [
    ['path rule', editPlatforms(/"eng\/ci\/macos-\*", /, '"eng/ci/macos-*", "eng/ci/macos-new.sh", ')],
    ['neutral path', editPlatforms(/"eng\/ci\/candidate-feed"\]/, '"eng/ci/candidate-feed", "eng/ci/other"]')],
    ['webkit-cdp path', editPlatforms(/"tests\/webkit-cdp", /, '"tests/webkit-cdp", "tests/webkit-cdp-more", ')],
    ['every platform list', editPlatforms(/const all = platforms;/, 'const all = [...platforms];')],
    ['label', editPlatforms(/"ci:all-platforms"/, '"ci:every-platform"')],
    ['comment', editPlatforms(/(\/\/ The native job's name for a leg)/, '$1 (edited)')],
    ['added path list', `${platformsText}\nexport const morePaths = ["a"];\n`],
  ]) assert.deepEqual(plans(head), unchanged, label);
  // #1033 added webKitCdpPaths: a path rule.
  const item = platformsEdits.find(edit => edit.pr === 1033);
  assert.deepEqual(definitionJobs([platformsPath], { ...context(ciText), texts: () => item.platforms }), { jobs: {} });
  // A path rule change with other files plans those as usual.
  const mixed = planFor(affected([platformsPath, 'packages/web/svelte/src/index.ts'],
    definitionContext(editPlatforms(/"eng\/ci\/macos-\*", /, '"eng/ci/macos-*", "eng/ci/macos-new.sh", '), platformsText, ciText)));
  const svelte = plan(['packages/web/svelte/src/index.ts']);
  assert.deepEqual([ran(mixed), mixed.platforms, mixed.native], [ran(svelte), svelte.platforms, svelte.native]);
});

test('a platforms.mjs change the planner cannot attribute plans everything', () => {
  // The test timeout scales are read in the setup action of every job, the shared build job included.
  plansEverything(planFor(affected([platformsPath], definitionContext(editPlatforms(/"Windows-ARM64": "6"/, '"Windows-ARM64": "7"'), platformsText, ciText))),
    'testTimeoutScales');
  for (const [label, value] of [
    ['no context', null],
    ['no texts', context(ciText)],
    ['deleted', { ...context(ciText), texts: () => [platformsText, null] }],
    ['another shape', { ...context(ciText), texts: () => [platformsText, `${platformsText}\nfunction helper() {}\n`] }],
    ['unparsable ci.yml', { ...context('jobs:\n\ta:\n'), texts: () => [platformsText, editPlatforms(/"Windows-ARM64": "6"/, '"Windows-ARM64": "7"')] }],
  ]) plansEverything(planFor(affected([platformsPath], value)), label);
  // A leg list that no job reads as its matrix any more.
  const unread = editJob('native', /fromJSON\(needs\.plan\.outputs\.native\)/, 'fromJSON(needs.plan.outputs.native-legs)');
  plansEverything(planFor(affected([platformsPath], { ...context(unread, unread), texts: () => [platformsText,
    editPlatforms(/(export const nativeLegs = \[\n)/, '$1  { os: "x", rid: "y" },\n')] })), 'unread leg list');
  // The declarations split at column 0 only.
  assert.deepEqual([...declarations('// c\nexport const a = [\n  1, // x\n];\nconst b = a;\n').entries()], [['a', 'export const a = [\n  1, // x\n];'], ['b', 'const b = a;']]);
  for (const text of [null, '', 'import x from "y";\n', '/* a */\nconst a = 1;\n', 'const a = 1;\nconst a = 2;\n'])
    assert.equal(declarations(text), null, JSON.stringify(text));
});

test('ci:all-platforms plans every leg of every job a change plans', () => {
  // The webkit sandbox script is neutral: its jobs run on their Linux legs only, unless the pull request is labelled.
  const decided = affected(['eng/ci/permit-webkit-sandbox.sh'], context(ciText));
  const jobs = Object.keys(decided.jobs);
  assert.ok(jobs.includes('native') && jobs.every(name => decided.jobs[name].platforms.length === 0), jobs.join());
  const plain = planFor(decided);
  assert.deepEqual(plain.native.include, nativeLegs.filter(leg => !leg.platform));
  const labelled = outputsOf(cli(['--plan', JSON.stringify(decided), '--all-platforms', `label ${allPlatformsLabel}`]));
  assert.deepEqual(labelled.skip, plain.skip);
  assert.deepEqual(labelled.platforms, platforms);
  for (const job of ['native', 'wpf', 'packaging', 'screen-readers'].filter(name => jobs.includes(name)))
    assert.deepEqual(labelled[job].include, legLists[job], job);
  assert.match(labelled.why, /label ci:all-platforms/);
});
