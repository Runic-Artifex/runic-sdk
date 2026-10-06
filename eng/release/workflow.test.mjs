import { test, expect } from 'bun:test';
import { readFileSync, existsSync } from 'node:fs';
const workflow = name => Bun.YAML.parse(readFileSync(new URL(`../../.github/workflows/${name}`, import.meta.url), 'utf8'));
const runs = job => job.steps.filter(s => s.run).map(s => s.run).join('\n');
test('publication reuses the package artifact of the successful CI push run without rerunning CI', () => {
  const ci = workflow('ci.yml'), release = workflow('publish-preview.yml');
  expect(Object.hasOwn(ci.on, 'workflow_call')).toBe(false);
  expect(Object.values(release.jobs).some(job => job.uses)).toBe(false);
  expect(runs(release.jobs.preflight)).toContain('node eng/release/ci-run.mjs');
  expect(release.jobs.preflight.if).toContain("github.ref == 'refs/heads/main'");
  expect(release.jobs.preflight.permissions).toEqual({contents: 'read', actions: 'read'});
  const uploaded = ci.jobs.packages.steps.find(s => s.uses?.startsWith('actions/upload-artifact@')).with.name;
  expect(uploaded).toBe('runic-sdk-${{ github.run_id }}');
  for (const name of ['candidate', 'publish']) {
    const job = release.jobs[name];
    expect(job.needs).toContain('preflight');
    expect(job.env.CI_RUN_ID).toBe('${{ needs.preflight.outputs.ci-run-id }}');
    const download = job.steps.find(s => s.uses?.startsWith('actions/download-artifact@'));
    expect(download.with).toMatchObject({name: '${{ needs.preflight.outputs.artifact }}', path: 'artifacts/packages',
      'run-id': '${{ needs.preflight.outputs.ci-run-id }}', 'github-token': '${{ github.token }}'});
    expect(job.permissions.actions).toBe('read');
    expect(runs(job)).not.toMatch(/run\.mjs pack|verify-packages|verify:|bun run test|eng\/test\.mjs/);
  }
  expect(release.jobs.publish.needs).toEqual(['preflight', 'candidate']);
  expect(release.concurrency['cancel-in-progress']).toBe(false);
  expect(ci.concurrency.group).not.toBe(release.concurrency.group);
});
test('a dry run performs every read-only check and never reaches OIDC, registry writes, release or tags', () => {
  const release = workflow('publish-preview.yml');
  expect(release.on.workflow_dispatch.inputs['dry-run']).toMatchObject({type: 'boolean', default: false});
  const {candidate, publish} = release.jobs;
  expect(candidate.environment).toBeUndefined();
  expect(candidate.permissions).toEqual({contents: 'read', actions: 'read'});
  expect(candidate.steps.some(s => s.uses?.startsWith('NuGet/login'))).toBe(false);
  const checks = candidate.steps.filter(s => /cli\.mjs (publish|tag-latest)|github\.mjs/.test(s.run ?? ''));
  expect(checks.map(s => s.run.match(/cli\.mjs (publish|tag-latest)|github\.mjs/)[0])).toEqual(['cli.mjs publish', 'github.mjs', 'cli.mjs tag-latest']);
  for (const step of checks) expect(step.run.trim().endsWith('--dry-run')).toBe(true);
  expect(publish.if).toBe('${{ !inputs.dry-run }}');
  expect(publish.environment).toBe('preview');
  expect(publish.permissions).toEqual({contents: 'write', 'id-token': 'write', actions: 'read'});
  expect(runs(publish)).not.toContain('--dry-run');
  expect(Object.entries(release.jobs).filter(([, job]) => job.permissions?.['id-token']).map(([name]) => name)).toEqual(['publish']);
});
test('publication verifies the candidate inventory and does not wait for registry indexing', () => {
  const release = workflow('publish-preview.yml');
  const steps = release.jobs.publish.steps;
  expect(release.jobs.candidate.steps.find(s => s.id === 'prepare').run).toContain('cli.mjs prepare artifacts/packages "$GITHUB_SHA" "$CI_RUN_ID"');
  const index = text => steps.findIndex(s => s.run?.includes(text));
  expect(index('cli.mjs verify')).toBeGreaterThan(-1);
  expect(index('cli.mjs verify')).toBeLessThan(index('cli.mjs publish'));
  expect(steps.some(s => s.run?.includes('smoke.mjs') || s.run?.includes('cli.mjs registry'))).toBe(false);
  expect(index('cli.mjs publish')).toBeLessThan(index('github.mjs'));
  expect(steps.findLast(s => s.uses?.startsWith('actions/upload-artifact@')).with.overwrite).toBe(true);
  expect(Object.keys(release.on.workflow_dispatch.inputs)).toEqual(['version', 'dry-run']);
  expect(existsSync(new URL('../../.github/workflows/preview-evidence.yml', import.meta.url))).toBe(false);
});
test('npm latest follows the preview after its GitHub release, never backwards', async () => {
  const { needsLatest } = await import('./registry.mjs');
  const steps = workflow('publish-preview.yml').jobs.publish.steps;
  expect(steps.findIndex(s => s.run?.includes('github.mjs'))).toBeLessThan(steps.findIndex(s => s.run?.includes('cli.mjs tag-latest')));
  expect(needsLatest(undefined, '0.6.0-preview.1')).toBe(true);
  expect(needsLatest('0.2.0-preview.1', '0.6.0-preview.1')).toBe(true);
  expect(needsLatest('0.6.0-preview.1', '0.6.0-preview.2')).toBe(true);
  expect(needsLatest('0.6.0-preview.1', '0.6.0-preview.1')).toBe(false);
  expect(needsLatest('0.7.0-preview.1', '0.6.0-preview.2')).toBe(false);
});
