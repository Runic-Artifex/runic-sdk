import { test, expect } from 'bun:test';
import { selection } from './test.mjs';
test('focused selections include only their managed suite or web package', () => {
  const commandLine = selection('command-line');
  expect(commandLine.kind).toBe('managed');
  expect(commandLine.paths.length).toBeGreaterThan(0);
  expect(commandLine.paths.every(p => p.includes('Runic.CommandLine') || p.startsWith('examples/command-line/'))).toBe(true);
  expect(selection('web/svelte')).toEqual({kind: 'web', paths: ['packages/web/svelte']});
  expect(() => selection('command-lien')).toThrow('Unknown test scope');
});
test('single file and single .NET test project avoid full workspace execution', () => {
  expect(selection('eng/release/contracts.test.mjs').paths).toHaveLength(1);
  expect(selection('tests/dotnet/Runic.Desktop.Tests/Runic.Desktop.Tests.csproj').kind).toBe('dotnet-test');
});
test('application checks run both ReactiveUI flavors through the shared conformance suite', () => {
  const application = selection('application');
  expect(application.kind).toBe('managed');
  for (const flavor of ['ReactiveUI', 'ReactiveUI.Reactive']) {
    expect(application.paths).toContain(`tests/fixtures/application/reactiveui-behavioral-conformance/Runic.Application.${flavor}.Conformance.Tests.csproj`);
  }
});
