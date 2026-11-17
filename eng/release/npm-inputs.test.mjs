import { test, expect } from 'bun:test';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { assertNpmSource, dirtyNpmInputs, extraNpmInputs, npmInputs, npmSourceCommit, sharedNpmInputs } from './npm-inputs.mjs';
import { packNpm } from './pack-npm.mjs';
import { inspect, sha256 } from './artifacts.mjs';

const root = resolve(import.meta.dirname, '../..');
const workspace = JSON.parse(readFileSync(join(root, 'eng/workspace.json'), 'utf8'));

// A repository with a shared build input and two packages: app depends on lib.
function repository(callback) {
  const directory = mkdtempSync(join(tmpdir(), 'runic-npm-inputs-test-'));
  const git = (...args) => execFileSync('git', ['-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', '-c', 'commit.gpgsign=false', ...args],
    { cwd: directory, encoding: 'utf8' }).trim();
  const write = (path, text) => { mkdirSync(dirname(join(directory, path)), { recursive: true }); writeFileSync(join(directory, path), text); };
  const commit = (path, text) => { write(path, text); git('add', '--', path); git('commit', '-q', '-m', `change ${path}`); return git('rev-parse', 'HEAD'); };
  const manifest = (name, dependencies = {}) => JSON.stringify({ name, version: '1.0.0', repository: 'https://github.com/Runic-Artifex/runic-sdk',
    files: ['dist'], peerDependencies: dependencies }, null, 2) + '\n';
  const local = { npm: [{ name: '@runic-artifex/app', path: 'packages/web/app' }, { name: '@runic-artifex/lib', path: 'packages/web/lib' }] };
  try {
    git('init', '-q', '-b', 'main');
    write('eng/workspace.json', JSON.stringify(local));
    write('packages/web/lib/package.json', manifest('@runic-artifex/lib'));
    write('packages/web/lib/dist/index.js', 'export const lib = 1;\n');
    write('packages/web/app/package.json', manifest('@runic-artifex/app', { '@runic-artifex/lib': '1.0.0' }));
    write('packages/web/app/dist/index.js', 'export const app = 1;\n');
    write('bun.lock', '{}\n');
    write('docs/notes.md', 'notes\n');
    git('add', '.');
    git('commit', '-q', '-m', 'initial');
    return callback({ directory, git, write, commit, workspace: local });
  } finally { rmSync(directory, { recursive: true, force: true }); }
}

test('a package\'s inputs are its directory, its first-party dependencies, its extras and the shared build inputs', () => repository(({ directory, workspace }) => {
  const app = npmInputs('@runic-artifex/app', { root: directory, workspace });
  expect(app).toContain('packages/web/app');
  expect(app).toContain('packages/web/lib');
  for (const input of sharedNpmInputs) expect(app).toContain(input);
  expect(npmInputs('@runic-artifex/lib', { root: directory, workspace })).not.toContain('packages/web/app');
  expect(() => npmInputs('@runic-artifex/missing', { root: directory, workspace })).toThrow('not an npm package');
}));

test('the real input sets name existing paths and cover what each package\'s build reads outside its directory', () => {
  for (const input of [...sharedNpmInputs, ...Object.values(extraNpmInputs).flat()]) expect(existsSync(join(root, input))).toBe(true);
  for (const name of Object.keys(extraNpmInputs)) expect(workspace.npm.map(p => p.name)).toContain(name);
  expect(npmInputs('@runic-artifex/react', { root, workspace })).toContain('packages/web/views');
  expect(npmInputs('@runic-artifex/views-translations', { root, workspace }))
    .toContain('packages/dotnet/Runic.Application.Views.Translations/translations');
  // tsconfig bases and build-script imports that leave the package directory.
  for (const { name, path } of workspace.npm) {
    const inputs = npmInputs(name, { root, workspace });
    const covered = file => inputs.some(input => file === input || file.startsWith(`${input}/`));
    const directory = join(root, path);
    const references = [];
    for (const file of readdirSync(directory).filter(file => /^tsconfig.*\.json$/.test(file))) {
      const base = JSON.parse(readFileSync(join(directory, file), 'utf8')).extends;
      if (base?.startsWith('.')) references.push(resolve(directory, base));
    }
    const scripts = join(directory, 'scripts');
    for (const file of existsSync(scripts) ? readdirSync(scripts).filter(file => file.endsWith('.mjs')) : [])
      for (const [, specifier] of readFileSync(join(scripts, file), 'utf8').matchAll(/from\s+["'](\.[^"']+)["']/g))
        references.push(resolve(scripts, specifier));
    for (const reference of references) expect({ name, file: relative(root, reference), covered: covered(relative(root, reference)) })
      .toEqual({ name, file: relative(root, reference), covered: true });
  }
});

test('packing at two commits that differ only outside the inputs gives byte-identical archives', () => repository(({ directory, commit, workspace }) => {
  const app = join(directory, 'packages/web/app');
  const pack = output => { mkdirSync(output); return packNpm(app, output, npmSourceCommit('@runic-artifex/app', { root: directory, workspace })); };
  const first = readFileSync(pack(join(directory, 'out-1')));
  commit('docs/notes.md', 'other notes\n');
  commit('.github/workflows/ci.yml', 'on: push\n');
  const second = readFileSync(pack(join(directory, 'out-2')));
  expect(sha256(second)).toBe(sha256(first));
  // The gzip header names no host operating system.
  expect(second[9]).toBe(0xff);
}));

test('a change to an input changes the gitHead, a change elsewhere does not', () => repository(({ directory, git, commit, workspace }) => {
  const source = name => npmSourceCommit(name, { root: directory, workspace });
  const initial = git('rev-parse', 'HEAD');
  expect(source('@runic-artifex/app')).toBe(initial);
  commit('docs/notes.md', 'other notes\n');
  expect(source('@runic-artifex/app')).toBe(initial);
  const lib = commit('packages/web/lib/dist/index.js', 'export const lib = 2;\n');
  expect(source('@runic-artifex/lib')).toBe(lib);
  expect(source('@runic-artifex/app')).toBe(lib);
  const shared = commit('bun.lock', '{"lockfileVersion":1}\n');
  expect(source('@runic-artifex/lib')).toBe(shared);
  const app = commit('packages/web/app/dist/index.js', 'export const app = 2;\n');
  expect(source('@runic-artifex/app')).toBe(app);
  expect(source('@runic-artifex/lib')).toBe(shared);
  // An archive built from such a checkout would not be its gitHead's.
  expect(dirtyNpmInputs('@runic-artifex/app', { root: directory, workspace })).toEqual([]);
  writeFileSync(join(directory, 'packages/web/lib/dist/index.js'), 'export const lib = 3;\n');
  expect(dirtyNpmInputs('@runic-artifex/app', { root: directory, workspace })).toEqual([' M packages/web/lib/dist/index.js']);
}));

test('the release check accepts an ancestor with the same inputs and refuses every other source', () => repository(({ directory, git, commit, workspace }) => {
  const check = (stamped, release) => assertNpmSource('@runic-artifex/app', stamped, release, { root: directory, workspace });
  const stamped = git('rev-parse', 'HEAD');
  const release = commit('docs/notes.md', 'release notes\n');
  check(stamped, release);
  check(release, release);
  expect(() => check(undefined, release)).toThrow('no gitHead');
  expect(() => check('b'.repeat(40), release)).toThrow('is not an ancestor');
  // A descendant of the release commit is not its source.
  const later = commit('docs/notes.md', 'later notes\n');
  expect(() => check(later, release)).toThrow('is not an ancestor');
  // A commit of another branch, even with the same inputs.
  git('checkout', '-q', '-b', 'side', stamped);
  const side = commit('docs/side.md', 'side\n');
  git('checkout', '-q', 'main');
  expect(() => check(side, later)).toThrow('is not an ancestor');
  // An ancestor whose inputs changed before the release commit.
  const changed = commit('packages/web/lib/dist/index.js', 'export const lib = 2;\n');
  expect(() => check(stamped, changed)).toThrow('its inputs changed after');
  check(changed, commit('docs/notes.md', 'after\n'));
}));

test('the source commit needs the full history', () => repository(({ directory, commit, workspace }) => {
  commit('docs/notes.md', 'second\n');
  const clone = join(directory, 'shallow');
  execFileSync('git', ['clone', '-q', '--depth', '1', `file://${directory}`, clone]);
  expect(() => npmSourceCommit('@runic-artifex/app', { root: clone, workspace })).toThrow('fetch-depth: 0');
}));

test('an archive stamped with its input commit passes the release scan of a later commit', () => repository(({ directory, git, commit, workspace }) => {
  const stamped = git('rev-parse', 'HEAD');
  const output = join(directory, 'out');
  mkdirSync(output);
  const archive = packNpm(join(directory, 'packages/web/app'), output, npmSourceCommit('@runic-artifex/app', { root: directory, workspace }));
  expect(inspect(archive, 'npm').source).toBe(stamped);
  const release = commit('docs/notes.md', 'release\n');
  expect(() => assertNpmSource('@runic-artifex/app', inspect(archive, 'npm').source, release, { root: directory, workspace })).not.toThrow();
}));
