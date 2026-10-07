import { test, expect } from 'bun:test';
import { selection } from './test.mjs';
test('focused selections include only their managed suite or web package', () => {
  const assets = selection('assets');
  expect(assets.kind).toBe('managed');
  expect(assets.paths.length).toBeGreaterThan(0);
  expect(assets.paths.every(p => p.includes('Runic.Assets'))).toBe(true);
  expect(selection('web/svelte')).toEqual({kind: 'web', paths: ['packages/web/svelte']});
  expect(() => selection('command-line')).toThrow('Unknown test scope');
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
test('application checks also build and run the fixtures outside the core solution', () => {
  const lines = selection('application').commands.map(([command, args]) => [command, ...args].join(' '));
  expect(lines.some(line => line.includes('reactiveui-reactive-flavor/ReactiveUiReactiveFlavorProof.csproj'))).toBe(true);
  expect(lines.some(line => line.includes('reactiveui-reactive-flavor/ReactiveUiReactiveSourceGeneratorProof.csproj'))).toBe(true);
  expect(lines.some(line => line.includes('ReactiveUi25AotProof'))).toBe(process.platform === 'linux');
  expect(selection('assets').commands).toEqual([]);
});
