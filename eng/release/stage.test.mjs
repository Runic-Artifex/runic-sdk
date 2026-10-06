import { test, expect } from 'bun:test';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { gunzipSync } from 'node:zlib';
import { createStaging, stageAndPromote } from './stage.mjs';
import { packNpm } from './pack-npm.mjs';
import { inspect } from './artifacts.mjs';

function scratch(callback) {
  const directory = mkdtempSync(join(tmpdir(), 'runic-stage-test-'));
  try { return callback(directory); } finally { rmSync(directory, { recursive: true, force: true }); }
}
const tree = path => readdirSync(path, { recursive: true }).sort();

test('a failed pack leaves the previous candidate and no staging directory', () => scratch(directory => {
  const target = join(directory, 'packages');
  mkdirSync(join(target, 'npm'), { recursive: true });
  writeFileSync(join(target, 'npm', 'old.tgz'), 'old');
  expect(() => stageAndPromote(target, staging => {
    writeFileSync(join(staging, 'partial.tgz'), 'partial');
    throw new Error('interrupted');
  })).toThrow('interrupted');
  expect(tree(target)).toEqual(['npm', 'npm/old.tgz']);
  expect(readdirSync(directory)).toEqual(['packages']);
}));

test('a complete pack replaces the candidate as a whole', () => scratch(directory => {
  const target = join(directory, 'packages');
  mkdirSync(target);
  writeFileSync(join(target, 'stale.tgz'), 'stale');
  stageAndPromote(target, staging => writeFileSync(join(staging, 'new.tgz'), 'new'));
  expect(tree(target)).toEqual(['new.tgz']);
  expect(readdirSync(directory)).toEqual(['packages']);
}));

test('leftovers from a killed pack are removed before the next pack', () => scratch(directory => {
  const target = join(directory, 'packages');
  mkdirSync(join(directory, '.packages-staging-killed'));
  // A promotion killed between renames: the previous candidate is restored.
  mkdirSync(join(directory, '.packages-previous-1'));
  writeFileSync(join(directory, '.packages-previous-1', 'old.tgz'), 'old');
  const staging = createStaging(target);
  expect(readdirSync(directory).sort()).toEqual([staging.split(/[\\/]/).pop(), 'packages'].sort());
  expect(tree(target)).toEqual(['old.tgz']);
}));

test('npm packing stamps gitHead into the archive without writing the source manifest', () => scratch(directory => {
  const source = join(directory, 'source'), output = join(directory, 'out'), revision = 'a'.repeat(40);
  mkdirSync(join(source, 'dist'), { recursive: true });
  mkdirSync(output);
  const manifestText = '{ "name": "@runic-artifex/stage-test", "version": "1.0.0",\n  "repository": "https://github.com/Runic-Artifex/runic-sdk", "files": ["dist"] }\n';
  writeFileSync(join(source, 'package.json'), manifestText);
  writeFileSync(join(source, 'dist', 'index.js'), 'export const value = 1;\n'.repeat(100));
  const archive = packNpm(source, output, revision);
  expect(readFileSync(join(source, 'package.json'), 'utf8')).toBe(manifestText);
  expect(inspect(archive, 'npm').source).toBe(revision);
  const listing = execFileSync('tar', ['-xzOf', archive, 'package/dist/index.js'], { encoding: 'utf8' });
  expect(listing).toBe('export const value = 1;\n'.repeat(100));
  expect(gunzipSync(readFileSync(archive)).length % 512).toBe(0);
}));

test('staging is a sibling of the target so promotion is a rename', () => scratch(directory => {
  const target = join(directory, 'artifacts', 'packages');
  const staging = createStaging(target);
  expect(existsSync(staging)).toBe(true);
  expect(join(staging, '..')).toBe(join(directory, 'artifacts'));
}));
