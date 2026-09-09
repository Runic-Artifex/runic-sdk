import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {readFileSync} from 'node:fs';

export function readNpmCandidates(archives) {
  return new Map(archives.map(archive => {
    const manifest = JSON.parse(execFileSync('tar', ['-xOf', archive, 'package/package.json'], {encoding: 'utf8'}));
    return [manifest.name, {version: manifest.version, integrity: `sha512-${createHash('sha512').update(readFileSync(archive)).digest('base64')}`}];
  }));
}

// Validate the immutable customer lock before acceptance redirects a download URL.
// These deliberately narrow parsers match our committed npm/pnpm/Bun lock formats;
// an unsupported layout fails closed instead of silently skipping Runic entries.
export function verifyTemplateLock(text, filename, candidates) {
  let count = 0;
  const seen = new Set();
  function entry(name, version, integrity) {
    const candidate = candidates.get(name);
    assert.ok(candidate, `${filename}: unknown Runic package ${name}`);
    assert.equal(version, candidate.version, `${filename}: stale version for ${name}`);
    assert.equal(integrity, candidate.integrity, `${filename}: stale integrity for ${name}`);
    seen.add(name); count++;
  }
  function declarations(dependencies) {
    for (const [name, version] of Object.entries(dependencies ?? {})) if (name.startsWith('@runic-artifex/')) {
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
    assert.equal(count, [...packages.matchAll(/^  ['\"]?@runic-artifex\//gm)].length, `${filename}: unparsed Runic pnpm resolution`);
    for (const match of text.matchAll(/^      '(@runic-artifex\/[^']+)':\n        specifier: ([^\n]+)\n        version: ([^\n(]+)/gm)) {
      declarations({[match[1]]: match[2]});
      assert.equal(match[3], candidates.get(match[1]).version, `${filename}: stale pnpm importer resolution`);
    }
  } else if (filename.endsWith('bun.lock')) {
    for (const match of text.matchAll(/^    "(@runic-artifex\/[^"/]+)": \[(.+)\],?$/gm)) {
      const record = JSON.parse(`[${match[2]}]`);
      assert.ok(record[0].startsWith(`${match[1]}@`), `${filename}: unexpected Bun resolution`);
      entry(match[1], record[0].slice(match[1].length + 1), record.at(-1));
    }
    assert.equal(count, [...text.matchAll(/^    "@runic-artifex\//gm)].length, `${filename}: unparsed Runic Bun resolution`);
    for (const match of text.matchAll(/^        "(@runic-artifex\/[^"/]+)": "([^"]+)"/gm)) declarations({[match[1]]: match[2]});
  } else throw new Error(`Unsupported template lock: ${filename}`);
  assert.ok(count > 0, `${filename}: no Runic resolutions verified`);
  return count;
}

export function verifyPackagedTemplateLocks(nupkg, archives) {
  const candidates = readNpmCandidates(archives);
  const files = JSON.parse(execFileSync('python3', ['-c', 'import zipfile,json,sys\nwith zipfile.ZipFile(sys.argv[1]) as z: print(json.dumps({n:z.read(n).decode() for n in z.namelist() if n.endswith(("package-lock.json","pnpm-lock.yaml","bun.lock"))}))', nupkg], {encoding: 'utf8', maxBuffer: 32 * 1024 * 1024}));
  const expected = ['react', 'vue', 'svelte', 'angular'].flatMap(framework => ['package-lock.json', 'pnpm-lock.yaml', 'bun.lock'].map(lock => `content/content/${framework}/Frontend/${lock}`));
  assert.deepEqual(Object.keys(files).sort(), expected.sort(), 'Packaged template lock inventory differs');
  const entries = Object.entries(files).reduce((total, [filename, text]) => total + verifyTemplateLock(text, filename, candidates), 0);
  return {locks: expected.length, entries};
}
