import { test, expect } from 'bun:test';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { join, delimiter } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { VERSION } from './artifacts.mjs';
const root = fileURLToPath(new URL('../..', import.meta.url));
const source = execFileSync('git', ['rev-parse', 'HEAD'], {cwd: root, encoding: 'utf8'}).trim();
const ghScript = String.raw`
import {appendFileSync} from 'node:fs';
const args=process.argv.slice(2), mode=process.env.TEST_MODE, source=process.env.TEST_SOURCE;
appendFileSync(process.env.TEST_CALLS,JSON.stringify(args)+'\n');
if(args[0]==='release'&&args[1]==='view'){
  if(mode==='release-error'){console.error('HTTP 502: Bad Gateway');process.exit(1);}
  if(!['existing','wrong-release','draft','annotated'].includes(mode)){console.error('release not found');process.exit(1);}
  console.log(JSON.stringify({isDraft:mode==='draft',targetCommitish:source}));
}
if(args[0]==='api'){
  if(mode==='new'||mode==='draft'){console.error('gh: Not Found (HTTP 404)');process.exit(1);}
  if(mode==='tag-error'){console.error('gh: Server Error (HTTP 502)');process.exit(1);}
  if(mode==='annotated'&&args[1].includes('/git/ref/tags/')){console.log('tag '+'e'.repeat(40));process.exit(0);}
  console.log('commit '+(mode.startsWith('wrong')?'f'.repeat(40):source));
}
`;
const assets = [`runic-sdk-${VERSION}-packages.tar.gz`, `runic-sdk-${VERSION}.cdx.json`, 'SHA256SUMS'];
// Runs github.mjs release over a release directory that holds the described assets.
function fixture(mode, check, extra = [], {described = true} = {}) {
  const directory = mkdtempSync(join(tmpdir(), 'runic-release-test-'));
  try {
    const bin = join(directory, 'bin'), output = join(directory, 'output');
    mkdirSync(bin); mkdirSync(output);
    if (described) for (const name of assets) writeFileSync(join(output, name), `${name} fixture`);
    const calls = join(directory, 'calls.jsonl');
    writeFileSync(calls, '');
    writeFileSync(join(bin, 'gh'), `#!${process.execPath}\n${ghScript}`, {mode: 0o755});
    const result = spawnSync(process.execPath, [join(root, 'eng/release/github.mjs'), 'release', output, ...extra], {
      cwd: root, encoding: 'utf8', env: {...process.env, PATH: `${bin}${delimiter}${process.env.PATH}`, TEST_MODE: mode, TEST_SOURCE: source, TEST_CALLS: calls},
    });
    check(result, output, readFileSync(calls, 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line)));
  } finally { rmSync(directory, {recursive: true, force: true}); }
}
test('new release uploads the described bundle, SBOM and checksums', () => fixture('new', (result, output, calls) => {
  expect(result.status).toBe(0);
  const create = calls.find(args => args[0] === 'release' && args[1] === 'create');
  expect(create).toContain('--draft');
  const upload = calls.find(args => args[1] === 'upload');
  expect(upload.slice(-3)).toEqual(assets.map(name => join(output, name)));
  expect(calls.at(-1)).toContain('--draft=false');
}));
test('a release without described assets stops before writing, also in a dry run', () => {
  for (const extra of [[], ['--dry-run']]) fixture('new', (result, output, calls) => {
    expect(result.status).not.toBe(0);
    expect(result.stderr).toContain('run github.mjs describe first');
    expect(calls.some(args => ['create', 'upload', 'edit'].includes(args[1]))).toBe(false);
  }, extra, {described: false});
});
test('retry preserves existing release while conflicting release or tag prevents creation', () => {
  fixture('existing', (result, output, calls) => {
    expect(result.status).toBe(0);
    expect(calls.some(args => ['create', 'upload', 'edit'].includes(args[1]))).toBe(false);
  });
  for (const mode of ['wrong-release', 'wrong-tag']) fixture(mode, (result, output, calls) => {
    expect(result.status).not.toBe(0); expect(calls.some(args => args[1] === 'create')).toBe(false);
  });
});

test('an interrupted draft upload is resumed before the release becomes public', () => fixture('draft', (result, output, calls) => {
  expect(result.status).toBe(0);
  expect(calls.some(args => args[1] === 'create')).toBe(false);
  expect(calls.find(args => args[1] === 'upload')).toContain('--clobber');
  expect(calls.at(-1)).toContain('--draft=false');
}));

test('a dry run checks tag and release ownership but never creates, uploads or publishes', () => {
  for (const mode of ['new', 'draft', 'existing']) fixture(mode, (result, output, calls) => {
    expect(result.status).toBe(0);
    expect(calls.every(args => (args[0] === 'release' && args[1] === 'view') || args[0] === 'api')).toBe(true);
  }, ['--dry-run']);
  for (const mode of ['wrong-release', 'wrong-tag']) fixture(mode, (result, output, calls) => {
    expect(result.status).not.toBe(0);
    expect(calls.every(args => (args[0] === 'release' && args[1] === 'view') || args[0] === 'api')).toBe(true);
  }, ['--dry-run']);
});

test('only a missing tag or release (404) counts as absent; other lookup errors stop before writing', () => {
  for (const mode of ['tag-error', 'release-error']) for (const extra of [[], ['--dry-run']]) fixture(mode, (result, output, calls) => {
    expect(result.status).not.toBe(0);
    expect(calls.some(args => ['create', 'upload', 'edit'].includes(args[1]))).toBe(false);
  }, extra);
  fixture('new', (result, output, calls) => {
    const lookup = calls.find(args => args[0] === 'api');
    expect(lookup[1]).toBe(`repos/Runic-Artifex/runic-sdk/git/ref/tags/v${VERSION}`);
  }, ['--dry-run']);
});

test('an annotated tag is followed to its commit', () => fixture('annotated', (result, output, calls) => {
  expect(result.status).toBe(0);
  expect(calls.filter(args => args[0] === 'api').map(args => args[1])).toEqual(
    [`repos/Runic-Artifex/runic-sdk/git/ref/tags/v${VERSION}`, `repos/Runic-Artifex/runic-sdk/git/tags/${'e'.repeat(40)}`]);
}, ['--dry-run']));
