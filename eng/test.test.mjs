import { test, expect } from 'bun:test';
import { selection } from './test.mjs';
test('focused selections include only their managed suite or web package', () => {
  const commandLine = selection('command-line');
  expect(commandLine.kind).toBe('managed');
  expect(commandLine.paths.length).toBeGreaterThan(0);
  expect(commandLine.paths.every(p => p.includes('Runic.CommandLine'))).toBe(true);
  expect(selection('web/application-bridge')).toEqual({kind: 'web', paths: ['packages/web/application-bridge']});
  expect(() => selection('command-lien')).toThrow('Unknown test scope');
});
test('single file and single .NET test project avoid full workspace execution', () => {
  expect(selection('eng/release/contracts.test.mjs').paths).toHaveLength(1);
  expect(selection('tests/dotnet/Runic.Desktop.Tests/Runic.Desktop.Tests.csproj').kind).toBe('dotnet-test');
});
