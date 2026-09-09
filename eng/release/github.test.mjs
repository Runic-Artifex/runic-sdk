import { test, expect } from 'bun:test';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { join, delimiter } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { VERSION, sha256 } from './artifacts.mjs';
const root = fileURLToPath(new URL('../..', import.meta.url));
const source = execFileSync('git', ['rev-parse', 'HEAD'], {cwd: root, encoding: 'utf8'}).trim();
function fixture(mode, check) {
  const directory = mkdtempSync(join(tmpdir(), 'runic-release-test-'));
  try {
    const bin = join(directory, 'bin'), packages = join(directory, 'packages'), output = join(directory, 'output');
    mkdirSync(bin); mkdirSync(join(packages, 'npm'), {recursive: true}); mkdirSync(join(packages, 'nuget'));
    writeFileSync(join(packages, 'npm', 'test.tgz'), 'npm fixture');
    writeFileSync(join(packages, 'nuget', 'test.nupkg'), 'nuget fixture');
    const calls = join(directory, 'calls.jsonl');
    writeFileSync(join(bin, 'gh'), `#!${process.execPath}\nimport {appendFileSync} from 'node:fs';\nconst args=process.argv.slice(2);appendFileSync(process.env.TEST_CALLS,JSON.stringify(args)+'\\n');\nif(args[0]==='release'&&args[1]==='view')process.exit(process.env.TEST_MODE==='existing'||process.env.TEST_MODE==='wrong-release'?0:1);\nif(args[0]==='api'){if(process.env.TEST_MODE==='new')process.exit(1);console.log(process.env.TEST_MODE.startsWith('wrong')?'f'.repeat(40):process.env.TEST_SOURCE);}\n`, {mode: 0o755});
    const result = spawnSync(process.execPath, [join(root, 'eng/release/github.mjs'), packages, output], {
      cwd: root, encoding: 'utf8', env: {...process.env, PATH: `${bin}${delimiter}${process.env.PATH}`, TEST_MODE: mode, TEST_SOURCE: source, TEST_CALLS: calls},
    });
    check(result, output, readFileSync(calls, 'utf8').trim().split('\n').map(line => JSON.parse(line)));
  } finally { rmSync(directory, {recursive: true, force: true}); }
}
test('new release uploads only distributables and their checksum', () => fixture('new', (result, output, calls) => {
  expect(result.status).toBe(0);
  const name = `runic-sdk-${VERSION}-packages.tar.gz`;
  expect(readFileSync(join(output, 'SHA256SUMS'), 'utf8')).toBe(`${sha256(readFileSync(join(output, name)))}  ${name}\n`);
  const archive = execFileSync('tar', ['-tzf', join(output, name)], {encoding: 'utf8'});
  expect(archive).toContain('npm/test.tgz'); expect(archive).toContain('nuget/test.nupkg');
  const create = calls.find(args => args[0] === 'release' && args[1] === 'create');
  expect(create.slice(-2)).toEqual([join(output, name), join(output, 'SHA256SUMS')]);
}));
test('retry preserves existing release while conflicting release or tag prevents creation', () => {
  fixture('existing', (result, output, calls) => {
    expect(result.status).toBe(0); expect(existsSync(output)).toBe(false);
    expect(calls.some(args => args[1] === 'create')).toBe(false);
  });
  for (const mode of ['wrong-release', 'wrong-tag']) fixture(mode, (result, output, calls) => {
    expect(result.status).not.toBe(0); expect(calls.some(args => args[1] === 'create')).toBe(false);
  });
});
