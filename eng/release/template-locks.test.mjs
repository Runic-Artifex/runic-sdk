import {test, expect} from 'bun:test';
import {readFileSync} from 'node:fs';
import {verifyTemplateLock} from './template-locks.mjs';

const name = '@runic-artifex/application-bridge';
const version = '0.2.0-preview.1';
const integrity = 'sha512-final-artifact-bytes';
const candidates = new Map([[name, {version, integrity}]]);
const fixtures = {
  'package-lock.json': JSON.stringify({packages: {'': {dependencies: {[name]: version}}, [`node_modules/${name}`]: {version, integrity, resolved: `https://registry.npmjs.org/${name}/-/application-bridge-${version}.tgz`}}}),
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
for (const framework of ['react', 'vue', 'svelte', 'angular']) {
  test(`${framework} real locks parse every Runic package and reject a changed candidate`, () => {
    const base = new URL(`../../tools/Runic.Application.Templates/content/${framework}/Frontend/`, import.meta.url);
    const npm = JSON.parse(readFileSync(new URL('package-lock.json', base), 'utf8'));
    const real = new Map(Object.entries(npm.packages).filter(([p]) => p.startsWith('node_modules/@runic-artifex/')).map(([p, v]) => [p.slice('node_modules/'.length), {version: v.version, integrity: v.integrity}]));
    for (const filename of ['package-lock.json', 'pnpm-lock.yaml', 'bun.lock']) {
      const text = readFileSync(new URL(filename, base), 'utf8');
      expect(verifyTemplateLock(text, filename, real)).toBe(real.size);
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
