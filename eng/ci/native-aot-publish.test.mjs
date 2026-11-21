import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { root } from '../run.mjs';
import { msbuildArguments, parseArguments, projectFile, publishProperties, traversal } from './native-aot-publish.mjs';
import { nativeLegs } from './platforms.mjs';

test('the arguments name the RID, the parallelism and each output with its project', () => {
  assert.deepEqual(parseArguments(['--rid', 'win-x64', '--parallel', '3', 'clipboard-aot=tests/dotnet/Runic.Platform.Windows.Tests']),
    { rid: 'win-x64', parallel: 3, apps: [{ name: 'clipboard-aot', project: 'tests/dotnet/Runic.Platform.Windows.Tests' }] });
  for (const [why, argv] of [
    ['no RID', ['--parallel', '2', 'a=b']],
    ['no parallelism', ['--rid', 'linux-x64', 'a=b']],
    ['zero', ['--rid', 'linux-x64', '--parallel', '0', 'a=b']],
    ['no application', ['--rid', 'linux-x64', '--parallel', '2']],
    ['no name', ['--rid', 'linux-x64', '--parallel', '2', 'tests/dotnet/X']],
    ['a path as the name', ['--rid', 'linux-x64', '--parallel', '2', '../x=tests/dotnet/X']],
    ['one output twice', ['--rid', 'linux-x64', '--parallel', '2', 'a=b', 'a=c']],
  ]) assert.throws(() => parseArguments(argv), undefined, why);
});

test('one MSBuild invocation publishes every application with dotnet publish\'s global properties', () => {
  const args = msbuildArguments('/w/publish.proj', 'osx-arm64', 2);
  assert.deepEqual(args.slice(0, 5), ['msbuild', '/w/publish.proj', '-restore', '-t:Publish', '-m:2']);
  for (const property of ['Configuration=Release', 'RuntimeIdentifier=osx-arm64', 'SelfContained=true', 'PublishAot=true',
    'IlcTreatWarningsAsErrors=true', 'TrimmerSingleWarn=false', '_IsPublishing=true'])
    assert.ok(args.includes(`-p:${property}`), property);
  // No PublishDir: a global property reaches every referenced library and would make each application's build of it a
  // configuration of its own, built concurrently into the same obj directory.
  assert.ok(!Object.keys(publishProperties('linux-x64')).includes('PublishDir'));
  const project = traversal(['/w/a & b/A.csproj', '/w/B.csproj']);
  assert.match(project, /<RunicAotApplication Include="\/w\/a &amp; b\/A.csproj" \/>/);
  assert.match(project, /<MSBuild Projects="@\(RunicAotApplication\)" Targets="Restore" BuildInParallel="false" \/>/);
  assert.match(project, /<MSBuild Projects="@\(RunicAotApplication\)" Targets="Publish" BuildInParallel="true" \/>/);
});

test('every application the native job publishes at once resolves to one project on each provider', () => {
  const workflow = Bun.YAML.parse(readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8'));
  const steps = workflow.jobs.native.steps;
  const index = steps.findIndex(step => step.run?.includes('node eng/ci/native-aot-publish.mjs'));
  assert.ok(index > 0);
  const apps = [...steps[index].run.matchAll(/([a-z0-9-]+)="?(tests\/[^\s")]+|examples\/[^\s")]+)/g)].map(match => ({ name: match[1], project: match[2] }));
  assert.ok(apps.length >= 10, apps.map(app => app.name).join(', '));
  for (const leg of nativeLegs) {
    assert.ok(Number.isInteger(leg.aotParallel) && leg.aotParallel >= 1, leg.rid);
    for (const app of apps) assert.ok(projectFile(app.project.replace('$PROVIDER', leg.provider)), `${app.name} on ${leg.rid}`);
  }
  // Each output is published there only, before the steps that run it.
  for (const { name } of apps) {
    const uses = steps.map((step, at) => [at, step.run ?? '']).filter(([, run]) => run.includes(`artifacts/${name}/`) || run.includes(`artifacts/${name} `));
    assert.ok(uses.length > 0, `${name} is used`);
    assert.ok(uses.every(([at]) => at > index), `${name} is used after it is published`);
    assert.ok(!steps.some(step => step.run?.includes(`-o artifacts/${name}\n`) || step.run?.endsWith(`-o artifacts/${name}`)), `${name} is published once`);
  }
});
