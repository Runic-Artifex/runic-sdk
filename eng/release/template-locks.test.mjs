import {test, expect} from 'bun:test';
import {readFileSync} from 'node:fs';
import {bindTemplateLock, templateLibraryDependency, templateLibraryPackageName, templateLockDirectories, templateTranslationsVitePlugin, verifyTemplateLock} from './template-locks.mjs';

const name = '@runic-artifex/svelte';
const version = '0.2.0-preview.1';
const integrity = 'sha512-final-artifact-bytes';
const candidates = new Map([[name, {version, integrity}]]);
const fixtures = {
  'package-lock.json': JSON.stringify({packages: {'': {dependencies: {[name]: version}}, [`node_modules/${name}`]: {version, integrity, resolved: `https://registry.npmjs.org/${name}/-/svelte-${version}.tgz`}}}),
  'pnpm-lock.yaml': `importers:\n  .:\n    dependencies:\n      '${name}':\n        specifier: ${version}\n        version: ${version}\npackages:\n  '${name}@${version}':\n    resolution: {integrity: ${integrity}}\nsnapshots:\n  '${name}@${version}': {}\n`,
  'bun.lock': `{\n  "workspaces": {\n    "": {\n      "dependencies": {\n        "${name}": "${version}",\n      },\n    },\n  },\n  "packages": {\n    "${name}": ["${name}@${version}", "", {}, "${integrity}"],\n  },\n}\n`,
};
for (const [filename, text] of Object.entries(fixtures)) {
  test(`${filename} accepts exact artifact resolution without modifying its bytes`, () => {
    const before = text;
    expect(verifyTemplateLock(text, filename, candidates)).toBe(1);
    expect(text).toBe(before);
  });
  test(`${filename} rejects the stale hash formerly hidden by candidate rebinding`, () => {
    expect(() => verifyTemplateLock(text.replace(integrity, 'sha512-old-ci-artifact'), filename, candidates)).toThrow('stale integrity');
  });
  test(`${filename} rejects a wrong version instead of rebinding it`, () => {
    expect(() => verifyTemplateLock(text.replaceAll(version, '0.1.0'), filename, candidates)).toThrow('stale version');
  });
}
test('npm public package URLs must not ship a local acceptance registry', () => {
  expect(() => verifyTemplateLock(fixtures['package-lock.json'].replace('registry.npmjs.org', '127.0.0.1:42111'), 'package-lock.json', candidates)).toThrow('nonpublic');
});
test('unknown or missing Runic resolutions fail closed', () => {
  expect(() => verifyTemplateLock(fixtures['package-lock.json'], 'package-lock.json', new Map())).toThrow('unknown');
  expect(() => verifyTemplateLock('{"packages":{"":{}}}', 'package-lock.json', candidates)).toThrow('no Runic');
});
// Real committed pnpm/Bun layouts exercise parser boundaries and peer suffixes.
for (const {directory, runicPackages, independentPackages} of templateLockDirectories) {
  test(`${directory} real locks parse every Runic package and reject a changed candidate`, () => {
    const base = new URL(`../../tools/Runic.Application.Templates/content/${directory}/`, import.meta.url);
    const npm = JSON.parse(readFileSync(new URL('package-lock.json', base), 'utf8'));
    // A library variant's workspace links its own packages, the application's frontend among them.
    const resolved = Object.entries(npm.packages).filter(([p, v]) => p.startsWith('node_modules/@runic-artifex/') && !v.link).map(([p, v]) => [p.slice('node_modules/'.length), {version: v.version, integrity: v.integrity}]);
    expect(resolved.map(([name]) => name).sort()).toEqual([...runicPackages, ...independentPackages].sort());
    // The candidates; the independently released packages are npm's, which the checks leave alone.
    const real = new Map(resolved.filter(([name]) => !independentPackages.includes(name)));
    const expectedPackages = [...runicPackages, ...independentPackages];
    for (const filename of ['package-lock.json', 'pnpm-lock.yaml', 'bun.lock']) {
      const text = readFileSync(new URL(filename, base), 'utf8');
      for (const name of independentPackages) expect(text).toContain(name);
      expect(verifyTemplateLock(text, filename, real, {expectedPackages})).toBe(real.size);
      const wrong = new Map([...real].map(([n, v]) => [n, {...v, integrity: 'sha512-final-published-artifact'}]));
      expect(() => verifyTemplateLock(text, filename, wrong)).toThrow('stale integrity');
    }
  });
}
test('malformed transitive pnpm and Bun resolutions cannot hide beside a valid direct package', () => {
  const pnpm = fixtures['pnpm-lock.yaml'].replace('snapshots:', `  "@runic-artifex/hidden@${version}":\n    resolution: {integrity: stale}\nsnapshots:`);
  expect(() => verifyTemplateLock(pnpm, 'pnpm-lock.yaml', candidates)).toThrow('unparsed Runic');
  const bun = fixtures['bun.lock'].replace('  "packages": {', '  "packages": {\n    "@runic-artifex/hidden": {"malformed": true},');
  expect(() => verifyTemplateLock(bun, 'bun.lock', candidates)).toThrow('unparsed Runic');
});
test('the translated Vite frontends resolve the published Translations Vite plugin, not a candidate', () => {
  const translated = templateLockDirectories.filter(entry => entry.translations);
  expect(translated.map(entry => entry.framework)).toEqual(['react', 'vue', 'svelte', 'sveltekit', 'angular-vite']);
  for (const entry of translated) expect(entry.independentPackages).toEqual([templateTranslationsVitePlugin]);
  const plugin = `node_modules/${templateTranslationsVitePlugin}`;
  const lock = JSON.parse(fixtures['package-lock.json']);
  lock.packages[''].devDependencies = {[templateTranslationsVitePlugin]: '0.11.0'};
  lock.packages[plugin] = {version: '0.11.0', integrity: 'sha512-published', resolved: `https://registry.npmjs.org/${templateTranslationsVitePlugin}/-/vite-plugin-runic-translations-0.11.0.tgz`};
  expect(verifyTemplateLock(JSON.stringify(lock), 'package-lock.json', candidates, {expectedPackages: [name, templateTranslationsVitePlugin]})).toBe(1);
  // It still comes from npm itself.
  lock.packages[plugin].resolved = 'http://127.0.0.1:42111/x.tgz';
  expect(() => verifyTemplateLock(JSON.stringify(lock), 'package-lock.json', candidates)).toThrow('nonpublic');
});
// The template tests bind every manager's lock to the train candidate of the independently released Translations Vite
// plugin: an upstream commit that changes the plugin's inputs packs the same version with a different gitHead, so the
// committed hash goes stale.
const registry = 'http://127.0.0.1:42111';
const trainIntegrity = 'sha512-train-candidate-of-this-run';
for (const {directory, runicPackages, independentPackages} of templateLockDirectories.filter(entry => entry.translations)) {
  test(`${directory} locks of every manager take the Translations train candidate's integrity`, () => {
    const base = new URL(`../../tools/Runic.Application.Templates/content/${directory}/`, import.meta.url);
    const npm = JSON.parse(readFileSync(new URL('package-lock.json', base), 'utf8'));
    const locked = name => npm.packages[`node_modules/${name}`];
    const published = locked(templateTranslationsVitePlugin).integrity;
    const sdk = new Map(runicPackages.map(name => [name, {version: locked(name).version, integrity: locked(name).integrity}]));
    const train = new Map([...sdk, [templateTranslationsVitePlugin, {version: locked(templateTranslationsVitePlugin).version, integrity: trainIntegrity}]]);
    for (const filename of ['package-lock.json', 'pnpm-lock.yaml', 'bun.lock']) {
      const text = readFileSync(new URL(filename, base), 'utf8');
      expect(text).toContain(published);
      const bound = bindTemplateLock(text, filename, train, registry);
      expect(bound).not.toContain(published);
      expect(bound).toContain(trainIntegrity);
      // Only the plugin's integrity (and npm's URLs) change; the Runic SDK candidates stay verified.
      if (filename !== 'package-lock.json') {
        expect(bound).toBe(text.replaceAll(published, trainIntegrity));
        expect(verifyTemplateLock(bound, filename, sdk, {expectedPackages: [...runicPackages, ...independentPackages]})).toBe(sdk.size);
      } else {
        const lock = JSON.parse(bound);
        const plugin = lock.packages[`node_modules/${templateTranslationsVitePlugin}`];
        expect(plugin.integrity).toBe(trainIntegrity);
        expect(plugin.resolved).toStartWith(`${registry}/${templateTranslationsVitePlugin}/-/`);
      }
      // Without a train candidate the registry passes npm's package through, so the lock keeps npm's integrity.
      const passthrough = bindTemplateLock(text, filename, sdk, registry);
      expect(passthrough).toContain(published);
      if (filename !== 'package-lock.json') expect(passthrough).toBe(text);
      // A candidate of another version is refused.
      const other = new Map([...sdk, [templateTranslationsVitePlugin, {version: '0.0.1', integrity: trainIntegrity}]]);
      expect(() => bindTemplateLock(text, filename, other, registry)).toThrow('is locked, but the train candidate is 0.0.1');
    }
  });
}
test('a pnpm or Bun lock whose Translations resolution the binding cannot parse fails closed', () => {
  const train = new Map([[templateTranslationsVitePlugin, {version: '0.13.0', integrity: trainIntegrity}]]);
  const pnpm = `packages:\n  '${templateTranslationsVitePlugin}@0.13.0':\n    resolution: {tarball: x}\n`;
  expect(() => bindTemplateLock(pnpm, 'pnpm-lock.yaml', train, registry)).toThrow('unparsed resolution');
  const bun = `  "packages": {\n    "${templateTranslationsVitePlugin}": ["${templateTranslationsVitePlugin}@0.13.0", "", {}],\n  },\n`;
  expect(() => bindTemplateLock(bun, 'bun.lock', train, registry)).toThrow('unparsed resolution');
  expect(() => bindTemplateLock('', 'yarn.lock', train, registry)).toThrow('Unsupported template lock');
});
// --library: each frontend's workspace root locks the application's frontend and the library's package, linked to each
// other, with the members' package.json files the template writes.
test('the library variants lock a workspace of the application frontend and the library package for every frontend', () => {
  const library = templateLockDirectories.filter(entry => entry.library);
  expect(library.map(entry => entry.framework)).toEqual(['react', 'vue', 'svelte', 'sveltekit', 'angular', 'angular-vite']);
  const content = new URL('../../tools/Runic.Application.Templates/content/', import.meta.url);
  const read = path => readFileSync(new URL(path, content), 'utf8');
  for (const {directory, members} of library) {
    const root = JSON.parse(read(`${directory}/package.json`));
    expect(root.workspaces).toEqual(Object.keys(members));
    expect(read(`${directory}/pnpm-workspace.yaml`)).toContain(Object.keys(members).map(folder => `  - ${folder}`).join('\n'));
    const manifests = Object.fromEntries(Object.entries(members).map(([folder, file]) => [folder, JSON.parse(read(file))]));
    expect(manifests['RunicCoreLibrary/Frontend'].name).toBe(templateLibraryPackageName);
    expect(manifests['RunicWindowApp/Frontend'].dependencies[templateLibraryPackageName]).toBe('__LIBRARY_DEPENDENCY__');
    const npm = JSON.parse(read(`${directory}/package-lock.json`));
    expect(npm.packages[`node_modules/${templateLibraryPackageName}`]).toEqual({resolved: 'RunicCoreLibrary/Frontend', link: true});
    expect(npm.packages['RunicWindowApp/Frontend'].dependencies[templateLibraryPackageName]).toBe(templateLibraryDependency('npm'));
    expect(read(`${directory}/pnpm-lock.yaml`)).toMatch(/^  RunicWindowApp\/Frontend:\n[\s\S]*?      runic-library-starter:\n        specifier: workspace:\*\n        version: link:\.\.\/\.\.\/RunicCoreLibrary\/Frontend$/m);
    const bun = read(`${directory}/bun.lock`);
    expect(bun).toContain(`"${templateLibraryPackageName}": "workspace:*",`);
    expect(bun).toContain(`"${templateLibraryPackageName}": ["${templateLibraryPackageName}@workspace:RunicCoreLibrary/Frontend"],`);
  }
});
test('a workspace link to a Runic-scoped member resolves nothing and fails nothing', () => {
  const app = '@runic-artifex/runic-app-starter';
  const lock = JSON.parse(fixtures['package-lock.json']);
  lock.packages[`node_modules/${app}`] = {resolved: 'RunicWindowApp/Frontend', link: true};
  expect(verifyTemplateLock(JSON.stringify(lock), 'package-lock.json', candidates)).toBe(1);
  const bun = fixtures['bun.lock'].replace('  "packages": {', `  "packages": {\n    "${app}": ["${app}@workspace:RunicWindowApp/Frontend"],`);
  expect(verifyTemplateLock(bun, 'bun.lock', candidates)).toBe(1);
});
