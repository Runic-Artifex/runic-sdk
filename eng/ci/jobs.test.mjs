import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { root } from '../run.mjs';

// eng/ci/jobs/*.sh hold the Linux job bodies that ci.yml and runic-ci (.runic/ci.nix) both run.
// GitHub runs one named step per script step; runic-ci runs whole scripts.
const workflow = readFileSync(resolve(root, '.github/workflows/ci.yml'), 'utf8');
const steps = script => {
  const result = spawnSync('bash', [resolve(root, 'eng/ci/jobs', script), '--list'], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  return result.stdout.trim().split('\n');
};
// Steps the native job shares with its Windows and macOS legs stay inline in ci.yml.
const sharedNativeSteps = ['views-runtime', 'desktop-contracts'];

test('ci.yml runs every Linux job script step', () => {
  for (const [script, shared] of [['views.sh', []], ['package-consumers.sh', []], ['native-linux.sh', sharedNativeSteps]]) {
    for (const step of steps(script)) {
      if (shared.includes(step)) continue;
      assert.match(workflow, new RegExp(`bash eng/ci/jobs/${script.replace('.', '\\.')} ${step}(;|\\n)`), `${script} ${step} is not run by ci.yml`);
    }
  }
  assert.ok(workflow.includes('bash eng/ci/jobs/packaging-flatpak.sh\n'));
});

test('the shared native steps run the same commands inline', () => {
  const script = readFileSync(resolve(root, 'eng/ci/jobs/native-linux.sh'), 'utf8');
  for (const command of ['bun run --bun build', 'dotnet build tests/dotnet/Runic.Desktop.Tests -c Release'])
    assert.ok(script.includes(command) && workflow.includes(command), command);
});

test('an unknown step fails', () => {
  const result = spawnSync('bash', [resolve(root, 'eng/ci/jobs/views.sh'), 'no-such-step'], { encoding: 'utf8' });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /unknown step no-such-step/);
});

test('.runic/ci.nix runs scripts that exist', () => {
  const definition = readFileSync(resolve(root, '.runic/ci.nix'), 'utf8');
  for (const [, script] of definition.matchAll(/bash (eng\/ci\/jobs\/[a-z-]+\.sh)/g))
    assert.ok(readFileSync(resolve(root, script), 'utf8').includes('run_steps "$@"'), script);
});

test('contract tests read a job-script call as the commands of its steps', async () => {
  const { expandJobScripts, jobScriptSteps } = await import('./jobs.mjs');
  assert.equal(expandJobScripts('bash eng/ci/jobs/package-consumers.sh isolated-consumers'), 'bun run verify-packages');
  assert.match(expandJobScripts('bash eng/ci/jobs/views.sh first-window'), /^node examples\/first-window\/browser-smoke\.mjs\nnode examples\/first-window\/navigation-smoke\.mjs$/);
  assert.deepEqual(jobScriptSteps('views.sh'), steps('views.sh'));
});
