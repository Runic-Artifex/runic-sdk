import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, relative, resolve } from 'node:path';
import { root, workspace } from '../run.mjs';
import { plan, skippableJobs, changedFiles, managedGroups, webTests, outputs } from './plan.mjs';
import { incomplete } from './gate.mjs';

const workflow = Bun.YAML.parse(readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8'));
const tracked = (...patterns) => execFileSync('git', ['ls-files', ...patterns], { cwd: root, encoding: 'utf8' }).trim().split('\n').filter(Boolean);
const runs = (result, job) => !result.skip.includes(job);
const everything = plan(null);

test('pushes and unknown diffs plan every job', () => {
  assert.equal(everything.full, true);
  assert.deepEqual(everything.skip, []);
  assert.deepEqual(everything.managed, managedGroups);
  assert.deepEqual(everything.web.include, webTests());
  assert.deepEqual(everything.components.sort(), Object.keys(workspace.components).sort());
});

test('documentation-only pull requests skip every skippable job', () => {
  for (const files of [['docs/README.md'], ['README.md', 'CONTRIBUTING.md'], ['specs/desktop/README.md'],
    ['eng/release/notes/0.6.0-preview.1.md'], ['examples/first-window/README.md'], ['tests/engineering/markdown-links.test.mjs'],
    ['eng/ci/plan.test.mjs'], ['.github/workflows/dynamicdata.yml'], []]) {
    const result = plan(files);
    assert.deepEqual(result.components, [], files.join());
    assert.deepEqual(result.skip, skippableJobs, files.join());
    assert.deepEqual(result.managed, []);
    assert.deepEqual(result.web, { include: [] });
  }
});

test('shared or unowned inputs run everything', () => {
  for (const file of ['Directory.Build.props', 'Directory.Packages.props', 'global.json', 'bun.lock', 'eng/run.mjs', 'eng/ci/plan.mjs',
    'eng/build/nuget-lock.targets', '.github/workflows/ci.yml', '.github/actions/setup-sdk/action.yml', 'flake.nix', 'LICENSE']) {
    const result = plan([file, 'docs/README.md']);
    assert.deepEqual(result.skip, [], file);
    assert.deepEqual(result.managed, managedGroups, file);
    assert.deepEqual(result.web.include, webTests(), file);
  }
});

test('package READMEs and fixtures are build inputs, not documentation', () => {
  for (const file of ['packages/web/views/README.md', 'packages/dotnet/Runic.Desktop/README.md',
    'tools/Runic.Application.Templates/content/runic-app/README.md', 'tests/fixtures/application/notes.md'])
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
  assert.deepEqual(result.skip, []);
  assert.deepEqual(result.managed, managedGroups);
});

test('template and Svelte changes run their lanes without unrelated suites', () => {
  const templates = plan(['tests/templates/Test-Templates.sh']);
  for (const job of ['packages', 'package-consumers', 'templates']) assert.ok(runs(templates, job), job);
  for (const job of ['managed', 'web', 'framework-consumers', 'views', 'native']) assert.ok(!runs(templates, job), job);

  const svelte = plan(['packages/web/svelte/src/index.ts']);
  for (const job of ['web', 'framework-consumers', 'views', 'templates', 'native']) assert.ok(runs(svelte, job), job);
  assert.deepEqual(svelte.web.include.map(item => item.package).sort(), ['svelte', 'sveltekit']);
  assert.deepEqual(svelte.managed, []);
});

test('Windows administration changes run native checks', () => {
  const result = plan(['packages/dotnet/Runic.Platform.Administration.Windows/Inspection.cs']);
  assert.ok(runs(result, 'native'));
  assert.ok(runs(result, 'packages'));
  assert.ok(!runs(result, 'views'));
});

test('pull requests diff the merge commit against its base parent, including both rename sides', () => {
  const calls = [];
  const git = args => {
    calls.push(args);
    return args[0] === 'diff' ? 'docs/old.md\0packages/web/views/src/new.ts\0' : 'abc\n';
  };
  assert.deepEqual(changedFiles('pull_request', git), ['docs/old.md', 'packages/web/views/src/new.ts']);
  assert.deepEqual(calls.at(-1), ['diff', '--name-only', '--no-renames', '-z', 'HEAD^1', 'HEAD']);
  assert.equal(changedFiles('push', git), null);
  assert.equal(changedFiles('workflow_dispatch', git), null);
  assert.equal(changedFiles('pull_request', () => { throw new Error('no merge parent'); }), null);
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

test('every core solution project commits a NuGet lock file and no other lock files exist', () => {
  const projects = [...readFileSync(resolve(root, 'RunicSdk.Core.slnx'), 'utf8').matchAll(/<Project Path="([^"]+)"/g)].map(([, path]) => path);
  const shared = new Set(projects.map(dirname).filter((directory, index, all) => all.indexOf(directory) !== index));
  const expected = projects.map(path => shared.has(dirname(path))
    ? `${dirname(path)}/packages.${path.split('/').at(-1).replace(/\.csproj$/, '')}.lock.json`
    : `${dirname(path)}/packages.lock.json`).sort();
  for (const path of expected) assert.ok(existsSync(resolve(root, path)), `${path} is missing; run bun run lock:nuget`);
  assert.deepEqual(tracked('*packages.lock.json', '*packages.*.lock.json').sort(), expected);
});
