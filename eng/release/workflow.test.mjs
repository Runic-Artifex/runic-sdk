import { test, expect } from 'bun:test';
import { readFileSync, existsSync } from 'node:fs';
const workflow = name => Bun.YAML.parse(readFileSync(new URL(`../../.github/workflows/${name}`, import.meta.url), 'utf8'));
test('publication depends on full reusable CI and consumes its artifacts from the same run', () => {
  const ci = workflow('ci.yml'), release = workflow('publish-preview.yml');
  expect(Object.hasOwn(ci.on, 'workflow_call')).toBe(true);
  expect(release.jobs.checks.uses).toBe('./.github/workflows/ci.yml');
  expect(release.jobs.checks.needs).toBe('preflight');
  expect(release.jobs.publish.needs).toBe('checks');
  expect(release.jobs.publish.if).toBeUndefined(); // normal needs success semantics
  const download = release.jobs.publish.steps.find(s => s.uses?.startsWith('actions/download-artifact@'));
  expect(download.with.name).toBe(ci.jobs.packages.steps.find(s => s.uses?.startsWith('actions/upload-artifact@')).with.name);
  expect(download.with['run-id']).toBeUndefined(); // current run, never a caller-supplied artifact
  expect(release.jobs.preflight.if).toContain("github.ref == 'refs/heads/main'");
  expect(release.concurrency['cancel-in-progress']).toBe(false);
  expect(ci.concurrency.group).not.toBe(release.concurrency.group);
});
test('registry smoke precedes release creation and release has no evidence input', () => {
  const release = workflow('publish-preview.yml');
  const steps = release.jobs.publish.steps;
  expect(steps.findIndex(s => s.run?.includes('smoke.mjs'))).toBeLessThan(steps.findIndex(s => s.run?.includes('github.mjs')));
  expect(Object.keys(release.on.workflow_dispatch.inputs)).toEqual(['version']);
  expect(existsSync(new URL('../../.github/workflows/preview-evidence.yml', import.meta.url))).toBe(false);
  expect(release.jobs.publish.permissions['id-token']).toBe('write');
  expect(release.jobs.publish.environment).toBe('preview');
});
