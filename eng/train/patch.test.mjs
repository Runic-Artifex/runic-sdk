import { expect, test } from 'bun:test';
import { createHash } from 'node:crypto';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { bindTemplateApplication, describeBinding, integritySource, patchBinding } from './patch.mjs';
import { lintTemplatePins, lintToolPins } from './checks.mjs';
import { bindTemplateLock, verifyTemplateLock } from '../release/template-locks.mjs';

const root = resolve(import.meta.dirname, '../..');
const template = join(root, 'tools/Runic.Application.Templates/content/runic-app');
const pins = (...versions) => versions.map(version => ({ name: 'RunicWindowApp.csproj', id: 'Runic.Translations', version }));
const versions = (translations, cli = '0.14.0') => ({ translations, cli, sdk: '0.24.0' });

test('a Translations patch binds the earlier patches of its line that the template pins: the 0.15.1 case', () => {
  // runic-sdk main pins Translations 0.15.0 (template and tool manifest); the candidate is 0.15.1 (the 11-20 patch).
  expect(patchBinding({ row: 'P-tr', versions: versions('0.15.1'), templatePins: pins('0.15.0', '0.15.0') }))
    .toEqual({ translations: { from: ['0.15.0'], to: '0.15.1' }, cli: null });
  expect(patchBinding({ row: 'N-pub', versions: versions('0.15.2'), templatePins: pins('0.15.0', '0.15.1') }).translations)
    .toEqual({ from: ['0.15.0', '0.15.1'], to: '0.15.2' });
  for (const [why, input] of [
    ['a minor keeps R5', { row: 'P-tr', versions: versions('0.16.0'), templatePins: pins('0.15.1') }],
    ['pins already equal to the candidate', { row: 'P-tr', versions: versions('0.15.1'), templatePins: pins('0.15.1') }],
    ['a pin on another line', { row: 'P-tr', versions: versions('0.15.1'), templatePins: pins('0.14.0') }],
    ['a pin later than the candidate', { row: 'P-tr', versions: versions('0.15.1'), templatePins: pins('0.15.2') }],
    ['a prerelease', { row: 'P-tr', versions: versions('0.15.1-rc.1'), templatePins: pins('0.15.0') }],
    ['no pins', { row: 'P-tr', versions: versions('0.15.1'), templatePins: [] }],
    // runic-sdk's own publish: its template must pin what Directory.Packages.props builds against.
    ['row P-sdk', { row: 'P-sdk', versions: versions('0.15.1'), templatePins: pins('0.15.0') }],
  ]) expect(patchBinding(input).translations, why).toBeNull();
});

test('a Command Line patch accepts tools that bundle an earlier patch of its line', () => {
  const binding = patchBinding({ row: 'T', versions: versions('0.15.0', '0.14.1'), templatePins: pins('0.15.0'), bundled: ['0.14.0', '0.14.0'] });
  expect(binding).toEqual({ translations: null, cli: { from: ['0.14.0'], to: '0.14.1' } });
  expect(describeBinding(binding)).toEqual(['tools that bundle Runic.CommandLine 0.14.0 keep it; the candidate 0.14.1 is a patch of that line']);
  expect(patchBinding({ row: 'T', versions: versions('0.15.0', '0.15.0'), bundled: ['0.14.0'] }).cli).toBeNull();
  expect(patchBinding({ row: 'P-sdk', versions: versions('0.15.0', '0.14.1'), bundled: ['0.14.0'] }).cli).toBeNull();
  // S6 accepts exactly the bound versions.
  const tools = [{ id: 'dotnet-runic', version: '0.24.0', bundled: ['0.14.0'] }];
  expect(lintToolPins(tools, '0.14.1')).toHaveLength(1);
  expect(lintToolPins(tools, '0.14.1', { bound: ['0.14.0'] })).toEqual([]);
  expect(lintToolPins([{ ...tools[0], bundled: ['0.13.0'] }], '0.14.1', { bound: ['0.14.0'] })).toHaveLength(1);
});

test('S6 accepts the bound template pins and still refuses any other', () => {
  const files = [{ name: 'RunicWindowApp.csproj', text: '<PackageReference Include="Runic.Translations" Version="0.15.0" />' },
    { name: '.config/dotnet-tools.json', text: '"dotnet-runic-translations": { "version": "0.15.0" }' }];
  expect(lintTemplatePins(files, '0.15.1').failures).toHaveLength(2);
  expect(lintTemplatePins(files, '0.15.1', { bound: ['0.15.0'] }).failures).toEqual([]);
  expect(lintTemplatePins(files, '0.16.0', { bound: [] }).failures[0].message).toContain('(R5)');
});

// A generated --translations React application as runic-sdk main would make it with Translations 0.15.0 pinned: the
// template's own files, with its Translations pins set back to 0.15.0.
function generatedApplication(directory) {
  const pinned = text => text.replaceAll('0.15.1', '0.15.0');
  mkdirSync(join(directory, '.config'), { recursive: true });
  mkdirSync(join(directory, 'Frontend'), { recursive: true });
  writeFileSync(join(directory, 'RunicWindowApp.csproj'), readFileSync(join(template, 'RunicWindowApp.csproj'), 'utf8'));
  writeFileSync(join(directory, '.config/dotnet-tools.json'), readFileSync(join(template, '.config/dotnet-tools.json'), 'utf8'));
  cpSync(join(template, 'translations/react/package.json'), join(directory, 'Frontend/package.json'));
  cpSync(join(template, 'translations/react/package-lock.json'), join(directory, 'Frontend/package-lock.json'));
  for (const file of ['RunicWindowApp.csproj', '.config/dotnet-tools.json', 'Frontend/package.json', 'Frontend/package-lock.json']) {
    const path = join(directory, file);
    const text = readFileSync(path, 'utf8');
    expect(text, file).toContain('0.15.1');
    writeFileSync(path, pinned(text));
  }
}

test('the gate binds the 0.15.1 candidate into a template application that pins 0.15.0, and its lock then binds', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'runic-train-patch-'));
  try {
    generatedApplication(directory);
    const lockFile = join(directory, 'Frontend/package-lock.json');
    const plugin = '@runic-artifex/vite-plugin-runic-translations';
    const lock = () => JSON.parse(readFileSync(lockFile, 'utf8'));
    // The candidates the gate's registry serves: runic-sdk's npm packages as locked, and the Translations candidate.
    const candidates = new Map(Object.entries(lock().packages).filter(([path]) => path.includes('node_modules/@runic-artifex/'))
      .map(([path, entry]) => [path.slice(path.lastIndexOf('node_modules/') + 13), { version: entry.version, integrity: entry.integrity }]));
    candidates.set(plugin, { version: '0.15.1', integrity: 'sha512-candidate' });
    // Without the patch path, this is the 0.15.1 failure: the lock names 0.15.0 and the candidate is 0.15.1.
    expect(() => bindTemplateLock(readFileSync(lockFile, 'utf8'), lockFile, candidates, 'http://127.0.0.1:1')).toThrow('is locked, but the train candidate is 0.15.1');

    const asked = [];
    const changed = await bindTemplateApplication(directory, { from: ['0.15.0'], to: '0.15.1' }, async (name, version) => { asked.push(`${name}@${version}`); return 'sha512-candidate'; });
    expect(changed.map(file => file.slice(directory.length + 1)).sort())
      .toEqual(['.config/dotnet-tools.json', 'Frontend/package-lock.json', 'Frontend/package.json', 'RunicWindowApp.csproj']);
    expect(asked).toEqual([`${plugin}@0.15.1`]);
    const project = readFileSync(join(directory, 'RunicWindowApp.csproj'), 'utf8');
    expect(project).toContain('<PackageReference Include="Runic.Translations" Version="0.15.1" />');
    expect(project).toContain('<PackageReference Include="Runic.Translations.Build" Version="0.15.1"');
    expect(project).not.toMatch(/Version="0\.15\.0"/);
    expect(readFileSync(join(directory, '.config/dotnet-tools.json'), 'utf8')).toMatch(/"dotnet-runic-translations": \{\s*"version": "0\.15\.1"/);
    expect(JSON.parse(readFileSync(join(directory, 'Frontend/package.json'), 'utf8')).devDependencies?.[plugin]
      ?? JSON.parse(readFileSync(join(directory, 'Frontend/package.json'), 'utf8')).dependencies?.[plugin]).toBe('0.15.1');
    const entry = lock().packages[`node_modules/${plugin}`];
    expect(entry).toMatchObject({ version: '0.15.1', integrity: 'sha512-candidate',
      resolved: `https://registry.npmjs.org/${plugin}/-/vite-plugin-runic-translations-0.15.1.tgz` });
    const root = lock().packages[''];
    expect(root.dependencies?.[plugin] ?? root.devDependencies?.[plugin]).toBe('0.15.1');
    // The gate's next step, unchanged: the lock checks against the candidates and binds to the registry.
    const text = readFileSync(lockFile, 'utf8');
    verifyTemplateLock(text, lockFile, candidates);
    const bound = JSON.parse(bindTemplateLock(text, lockFile, candidates, 'http://127.0.0.1:1'));
    expect(bound.packages[`node_modules/${plugin}`].resolved).toBe(`http://127.0.0.1:1/${plugin}/-/vite-plugin-runic-translations-0.15.1.tgz`);
    // Binding again changes nothing; a pin on another version is left alone.
    expect(await bindTemplateApplication(directory, { from: ['0.15.0'], to: '0.15.1' }, async () => 'sha512-candidate')).toEqual([]);
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

test('without an integrity for the candidate the binding fails instead of testing the old pin', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'runic-train-patch-'));
  try {
    generatedApplication(directory);
    await expect(bindTemplateApplication(directory, { from: ['0.15.0'], to: '0.15.1' }, async () => null)).rejects.toThrow('No integrity for @runic-artifex/vite-plugin-runic-translations 0.15.1');
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

test('the integrity comes from the candidate archive, or from npm for a published patch', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'runic-train-patch-'));
  try {
    const archive = join(directory, 'plugin.tgz');
    writeFileSync(archive, 'bytes');
    const requests = [];
    const fetchImpl = async url => { requests.push(url); return Response.json({ dist: { integrity: 'sha512-published' } }); };
    const of = integritySource({ npm: [{ name: '@runic-artifex/vite-plugin-runic-translations', version: '0.15.1', file: archive }] }, { fetchImpl });
    expect(await of('@runic-artifex/vite-plugin-runic-translations', '0.15.1')).toBe(`sha512-${createHash('sha512').update('bytes').digest('base64')}`);
    expect(requests).toEqual([]);
    expect(await integritySource({ npm: [] }, { fetchImpl })('@runic-artifex/vite-plugin-runic-translations', '0.15.1')).toBe('sha512-published');
    expect(requests).toEqual(['https://registry.npmjs.org/@runic-artifex/vite-plugin-runic-translations/0.15.1']);
    expect(await integritySource({}, { fetchImpl: async () => new Response('', { status: 404 }) })('x', '1.0.1')).toBeNull();
  } finally { rmSync(directory, { recursive: true, force: true }); }
});
