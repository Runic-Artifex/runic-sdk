import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {readFileSync} from 'node:fs';

// Every template frontend installs the shared Views runtime and its framework binding, and the Runic Vite plugin.
// The keys are the frontends' builds: angular builds with the Angular CLI, angular-vite (--angular-build vite) with
// Vite and the Analog Angular plugin.
export const templateRunicPackages = Object.freeze({
  react: ['@runic-artifex/react', '@runic-artifex/views', '@runic-artifex/vite-plugin-runic'],
  vue: ['@runic-artifex/vue', '@runic-artifex/views', '@runic-artifex/vite-plugin-runic'],
  svelte: ['@runic-artifex/svelte', '@runic-artifex/views', '@runic-artifex/vite-plugin-runic'],
  // SvelteKit builds through its own Vite plugin and the Runic SvelteKit adapter; vite-plugin-runic adds the
  // development client there (runic({ html: false })), as it does for Angular (runicDevSessionProxy()).
  sveltekit: ['@runic-artifex/svelte', '@runic-artifex/sveltekit', '@runic-artifex/views', '@runic-artifex/vite-plugin-runic'],
  angular: ['@runic-artifex/angular', '@runic-artifex/views', '@runic-artifex/vite-plugin-runic'],
  'angular-vite': ['@runic-artifex/angular', '@runic-artifex/views', '@runic-artifex/vite-plugin-runic'],
});

// The runic-app template keeps one frontend directory per frontend build. angular-vite adds its package.json, lock
// files and Vite files to the Angular frontend's sources.
export const templateFrontends = Object.freeze(Object.keys(templateRunicPackages));
export const templateFrontendDirectory = framework => `runic-app/frontends/${framework}`;

// Runic packages that another Runic repository releases on its own (DoctorChecks.IsIndependentlyReleasedNpmIdentity):
// the template pins a published version, so a lock resolves them from npm, not from this repository's candidates.
export const isIndependentRunicPackage = name =>
  name === '@runic-artifex/vite-plugin-runic-translations' || name.startsWith('@runic-artifex/translations');

// --translations replaces the package.json and lock files of the Vite frontends (runic-app/translations/<framework>):
// their page compiles the catalog with the Translations Vite plugin. The Angular CLI imports the ESM the .NET build
// writes and keeps its frontend's files.
export const templateTranslationsVitePlugin = '@runic-artifex/vite-plugin-runic-translations';
export const templateTranslationsFrontends = Object.freeze(templateFrontends.filter(framework => framework !== 'angular'));

// Every directory of the template with a package.json and its three lock files: the frontends, then their translated
// variants. runicPackages are the candidate packages each lock resolves; independentPackages the published ones.
export const templateLockDirectories = Object.freeze([
  ...templateFrontends.map(framework => ({
    framework, translations: false, directory: templateFrontendDirectory(framework),
    runicPackages: templateRunicPackages[framework], independentPackages: [],
  })),
  ...templateTranslationsFrontends.map(framework => ({
    framework, translations: true, directory: `runic-app/translations/${framework}`,
    runicPackages: templateRunicPackages[framework], independentPackages: [templateTranslationsVitePlugin],
  })),
].map(entry => Object.freeze({ ...entry, pnpmWorkspace: templatePnpmWorkspace(entry) })));

// The pnpm-workspace.yaml whose overrides a pnpm lock resolves with, if it exists: pnpm reads none from package.json.
// Both Angular builds install @angular/build, so they share the Angular frontend's.
function templatePnpmWorkspace({ framework, directory }) {
  return framework.startsWith('angular') ? 'runic-app/frontends/angular/pnpm-workspace.yaml' : `${directory}/pnpm-workspace.yaml`;
}

export function readNpmCandidates(archives) {
  return new Map(archives.map(archive => {
    const manifest = JSON.parse(execFileSync('tar', ['-xOf', archive, 'package/package.json'], {encoding: 'utf8'}));
    return [manifest.name, {version: manifest.version, integrity: `sha512-${createHash('sha512').update(readFileSync(archive)).digest('base64')}`}];
  }));
}

// Validate the immutable customer lock before acceptance redirects a download URL.
// These deliberately narrow parsers match our committed npm/pnpm/Bun lock formats;
// an unsupported layout fails closed instead of silently skipping Runic entries.
export function verifyTemplateLock(text, filename, candidates, { requireRunic = true, expectedPackages = [] } = {}) {
  let count = 0;
  const seen = new Set();
  // The independently released packages: npm's own integrity covers them; the template package tests pin their version.
  const independent = new Set();
  function entry(name, version, integrity) {
    if (isIndependentRunicPackage(name)) { independent.add(name); return; }
    const candidate = candidates.get(name);
    assert.ok(candidate, `${filename}: unknown Runic package ${name}`);
    assert.equal(version, candidate.version, `${filename}: stale version for ${name}`);
    assert.equal(integrity, candidate.integrity, `${filename}: stale integrity for ${name}`);
    seen.add(name); count++;
  }
  function declarations(dependencies) {
    for (const [name, version] of Object.entries(dependencies ?? {})) if (name.startsWith('@runic-artifex/') && !isIndependentRunicPackage(name)) {
      assert.ok(candidates.has(name), `${filename}: unknown Runic dependency ${name}`);
      assert.equal(version, candidates.get(name).version, `${filename}: stale dependency for ${name}`);
      assert.ok(seen.has(name), `${filename}: missing resolved Runic package ${name}`);
    }
  }
  if (filename.endsWith('package-lock.json')) {
    const lock = JSON.parse(text);
    assert.ok(lock.packages?.[''], `${filename}: missing root package`);
    for (const [path, value] of Object.entries(lock.packages)) {
      const name = path.slice(path.lastIndexOf('node_modules/') + 'node_modules/'.length);
      if (!path.includes('node_modules/') || !name.startsWith('@runic-artifex/')) continue;
      entry(name, value.version, value.integrity);
      assert.equal(new URL(value.resolved).hostname, 'registry.npmjs.org', `${filename}: nonpublic Runic URL`);
    }
    for (const value of Object.values(lock.packages)) for (const field of ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies']) declarations(value[field]);
  } else if (filename.endsWith('pnpm-lock.yaml')) {
    const packages = text.split(/^packages:[ \t]*$/m).slice(1).map(section => section.split(/^snapshots:[ \t]*$/m)[0]).join('\n');
    assert.ok(packages, `${filename}: missing pnpm packages`);
    for (const match of packages.matchAll(/^  '(@runic-artifex\/[^@']+)@([^']+)':\n([\s\S]*?)(?=^  [^ ]|$(?![\s\S]))/gm)) {
      entry(match[1], match[2], match[3].match(/resolution: \{integrity: (sha512-[^}]+)\}/)?.[1]);
    }
    assert.equal(count + independent.size, [...packages.matchAll(/^  ['\"]?@runic-artifex\//gm)].length, `${filename}: unparsed Runic pnpm resolution`);
    for (const match of text.matchAll(/^      '(@runic-artifex\/[^']+)':\n        specifier: ([^\n]+)\n        version: ([^\n(]+)/gm)) {
      if (isIndependentRunicPackage(match[1])) continue;
      declarations({[match[1]]: match[2]});
      assert.equal(match[3], candidates.get(match[1]).version, `${filename}: stale pnpm importer resolution`);
    }
  } else if (filename.endsWith('bun.lock')) {
    for (const match of text.matchAll(/^    "(@runic-artifex\/[^"/]+)": \[(.+)\],?$/gm)) {
      const record = JSON.parse(`[${match[2]}]`);
      assert.ok(record[0].startsWith(`${match[1]}@`), `${filename}: unexpected Bun resolution`);
      entry(match[1], record[0].slice(match[1].length + 1), record.at(-1));
    }
    assert.equal(count + independent.size, [...text.matchAll(/^    "@runic-artifex\//gm)].length, `${filename}: unparsed Runic Bun resolution`);
    for (const match of text.matchAll(/^        "(@runic-artifex\/[^"/]+)": "([^"]+)"/gm)) declarations({[match[1]]: match[2]});
  } else throw new Error(`Unsupported template lock: ${filename}`);
  if (requireRunic) assert.ok(count > 0, `${filename}: no Runic resolutions verified`);
  for (const name of expectedPackages) assert.ok(seen.has(name) || independent.has(name), `${filename}: missing ${name} resolution`);
  return count;
}

export function verifyPackagedTemplateLocks(nupkg, archives) {
  const candidates = readNpmCandidates(archives);
  const files = JSON.parse(execFileSync('python3', ['-c', 'import zipfile,json,sys\nwith zipfile.ZipFile(sys.argv[1]) as z: print(json.dumps({n:z.read(n).decode() for n in z.namelist() if n.endswith(("package-lock.json","pnpm-lock.yaml","bun.lock"))}))', nupkg], {encoding: 'utf8', maxBuffer: 32 * 1024 * 1024}));
  const expected = new Map(templateLockDirectories.flatMap(entry => ['package-lock.json', 'pnpm-lock.yaml', 'bun.lock']
    .map(lock => [`content/content/${entry.directory}/${lock}`, entry])));
  assert.deepEqual(Object.keys(files).sort(), [...expected.keys()].sort(), 'Packaged template lock inventory differs');
  const entries = Object.entries(files).reduce((total, [filename, text]) => {
    const { runicPackages, independentPackages } = expected.get(filename);
    return total + verifyTemplateLock(text, filename, candidates, { expectedPackages: [...runicPackages, ...independentPackages] });
  }, 0);
  return {locks: expected.size, entries};
}
