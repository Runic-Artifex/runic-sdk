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
    // By id, so a later re-upload under the same name cannot change what is published.
    expect(download.with).toEqual({'artifact-ids': '${{ needs.preflight.outputs.artifact-id }}', path: 'artifacts/packages',
      'run-id': '${{ needs.preflight.outputs.ci-run-id }}', 'github-token': '${{ github.token }}'});
    expect(job.steps.some(s => s.uses === './.github/actions/setup-sdk' || /bun install|npm (ci|install)(?! --global npm@[0-9.]+ --ignore-scripts$)/.test(s.run ?? ''))).toBe(false);
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
  const checks = candidate.steps.filter(s => /cli\.mjs (publish|tag-latest)|github\.mjs release/.test(s.run ?? ''));
  expect(checks.map(s => s.run.match(/cli\.mjs (publish|tag-latest)|github\.mjs release/)[0])).toEqual(['cli.mjs publish', 'github.mjs release', 'cli.mjs tag-latest']);
  for (const step of checks) expect(step.run.trim().endsWith('--dry-run')).toBe(true);
  expect(publish.if).toBe('${{ !inputs.dry-run }}');
  expect(publish.environment).toBe('preview');
  expect(publish.permissions).toEqual({contents: 'write', 'id-token': 'write', attestations: 'write', actions: 'read'});
  expect(runs(publish)).not.toContain('--dry-run');
  for (const permission of ['id-token', 'attestations', 'contents'])
    expect(Object.entries(release.jobs).filter(([, job]) => job.permissions?.[permission] === 'write').map(([name]) => name)).toEqual(['publish']);
  for (const job of Object.values(release.jobs)) expect(job.permissions).toBeDefined();
  expect(release.permissions).toEqual({contents: 'read'});
});
test('only the publish job attests, after verifying and before publishing, every package and release asset', () => {
  const release = workflow('publish-preview.yml');
  const attesting = Object.entries(release.jobs).filter(([, job]) => job.steps.some(s => s.uses?.startsWith('actions/attest')));
  expect(attesting.map(([name]) => name)).toEqual(['publish']);
  const steps = release.jobs.publish.steps;
  const attest = steps.filter(s => s.uses?.startsWith('actions/attest'));
  expect(attest.map(s => s.uses.split('@')[0])).toEqual(['actions/attest-build-provenance', 'actions/attest']);
  for (const step of attest) expect(step.uses).toMatch(/@[0-9a-f]{40}$/);
  const lines = step => step.with['subject-path'].trim().split('\n');
  const packages = ['artifacts/packages/nuget/*.nupkg', 'artifacts/packages/npm/*.tgz'];
  const bundle = 'artifacts/release/runic-sdk-${{ inputs.version }}-packages.tar.gz', sbom = 'artifacts/release/runic-sdk-${{ inputs.version }}.cdx.json';
  expect(lines(attest[0])).toEqual([...packages, bundle, sbom, 'artifacts/release/SHA256SUMS']);
  expect(lines(attest[1])).toEqual([...packages, bundle]);
  expect(attest[1].with['sbom-path']).toBe(sbom);
  const index = predicate => steps.findIndex(predicate);
  const first = index(s => s.uses?.startsWith('actions/attest'));
  expect(index(s => s.run?.includes('sha256sum --check --strict'))).toBeLessThan(first);
  expect(index(s => s.run?.includes('cli.mjs verify'))).toBeLessThan(first);
  for (const write of ['cli.mjs publish', 'github.mjs release', 'cli.mjs tag-latest']) expect(index(s => s.run?.includes(write))).toBeGreaterThan(first);
  expect(index(s => s.uses?.startsWith('NuGet/login'))).toBeGreaterThan(first);
});
test('the read-only candidate describes the release and hands its files to publish by hash', () => {
  const {candidate, publish} = workflow('publish-preview.yml').jobs;
  const describe = candidate.steps.find(s => s.id === 'describe');
  expect(describe.run).toContain('github.mjs describe artifacts/packages artifacts/release');
  expect(describe.run).toContain('sha256sum -- *');
  expect(candidate.outputs['release-sha256']).toBe('${{ steps.describe.outputs.sha256 }}');
  const upload = candidate.steps.find(s => s.uses?.startsWith('actions/upload-artifact@'));
  expect(candidate.steps.indexOf(upload)).toBeGreaterThan(candidate.steps.indexOf(describe));
  expect(upload.with.path).toBe('artifacts/release');
  expect(candidate.steps.find(s => /github\.mjs release/.test(s.run ?? '')).run.trim()).toBe('bun eng/release/github.mjs release artifacts/release --dry-run');
  const check = publish.steps.find(s => s.run?.includes('sha256sum --check --strict'));
  expect(check['working-directory']).toBe('artifacts/release');
  expect(check.env.RELEASE_SHA256).toBe('${{ needs.candidate.outputs.release-sha256 }}');
  expect(publish.steps.indexOf(check)).toBeGreaterThan(publish.steps.findIndex(s => s.with?.name === 'release-candidate-${{ github.run_id }}'));
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
