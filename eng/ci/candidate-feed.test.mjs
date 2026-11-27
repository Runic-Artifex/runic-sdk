import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { request } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { cleanScript, editorsRunOf, encodedPowerShell, machineBunfig, machineNpmrc, machineNugetConfig, scpPath, sshOptions, windowsPath } from './candidate-feed.mjs';
import { bindNpmLock } from './candidate-feed/bind-npm-lock.mjs';

const remote = 'C:/Users/TestUser/runic-candidate';

test('the machine NuGet.config adds the candidate folder for runic-sdk ids next to nuget.org, with its own packages folder', () => {
  const config = machineNugetConfig(remote);
  assert.match(config, /<add key="globalPackagesFolder" value="C:\/Users\/TestUser\/runic-candidate\/packages" \/>/);
  assert.match(config, /<clear \/>/);
  assert.match(config, /<add key="runic-candidate" value="C:\/Users\/TestUser\/runic-candidate\/nuget" \/>/);
  const sources = Object.fromEntries([...config.matchAll(/<packageSource key="([^"]+)">([\s\S]*?)<\/packageSource>/g)]
    .map(([, key, body]) => [key, [...body.matchAll(/pattern="([^"]+)"/g)].map(match => match[1])]));
  // The editors' fixtures restore published Runic releases, so nuget.org serves runic-sdk's ids too; only the folder has
  // the candidate's version until the publish.
  for (const pattern of ['Runic.*', 'dotnet-runic']) {
    assert.ok(sources['runic-candidate'].includes(pattern), pattern);
    assert.ok(sources['nuget.org'].includes(pattern), pattern);
  }
  // Command Line and Translations come from nuget.org only (the train's other repositories).
  for (const pattern of ['Runic.Translations.*', 'Runic.CommandLine.*', 'dotnet-runic-translations', '*'])
    assert.ok(sources['nuget.org'].includes(pattern) && !sources['runic-candidate'].includes(pattern), pattern);
});

test('the npm configuration scopes @runic-artifex to the forwarded registry and keeps npm and Bun caches in the folder', () => {
  assert.equal(machineNpmrc(remote, 48731),
    '@runic-artifex:registry=http://127.0.0.1:48731/\ncache=C:/Users/TestUser/runic-candidate/npm-cache\nfetch-timeout=60000\n');
  assert.equal(machineBunfig(remote), '[install.cache]\ndir = "C:/Users/TestUser/runic-candidate/bun-cache"\n');
});

test('paths and SSH options', () => {
  assert.equal(windowsPath('C:\\Users\\TestUser\\runic-candidate\\'), remote);
  assert.equal(scpPath(remote), `/${remote}`);
  assert.equal(scpPath('runic-candidate'), 'runic-candidate');
  const options = sshOptions({ identity: '/keys/vm', knownHosts: '/tmp/known_hosts' });
  assert.ok(options.includes('StrictHostKeyChecking=yes') && options.includes('BatchMode=yes') && options.includes('UserKnownHostsFile=/tmp/known_hosts'));
  assert.ok(!options.some(item => /StrictHostKeyChecking=(no|accept-new)/.test(item)));
  assert.equal(Buffer.from(encodedPowerShell("'ä' | Out-Host"), 'base64').toString('utf16le'), "'ä' | Out-Host");
});

test('clean removes only a candidate folder and the resolver cache entries inside it', () => {
  const script = cleanScript(remote);
  assert.match(script, /\[IO\.Path\]::GetFullPath\('C:\/Users\/TestUser\/runic-candidate'\)/);
  assert.match(script, /-notmatch '"runic\.sdk\.candidate-feed\/1"'\) \{ throw/);
  // Windows PowerShell emits a JSON array as one pipeline object; the entries must be enumerated before filtering.
  assert.match(script, /\$entries = @\(\$parsed \| ForEach-Object \{ \$_ \}\)/);
  assert.match(script, /WriteAllText/);
  assert.equal(cleanScript("C:/Users/O'Brien/x"), cleanScript("C:/Users/O'Brien/x"));
  assert.match(cleanScript("C:/Users/O'Brien/x"), /GetFullPath\('C:\/Users\/O''Brien\/x'\)/);
});

test('the editors candidate is the newest successful ci.yml run of the commit, a pull request run included', () => {
  const sha = 'a'.repeat(40);
  const run = (id, extra = {}) => ({ id, head_sha: sha, status: 'completed', conclusion: 'success', path: '.github/workflows/ci.yml', event: 'pull_request', ...extra });
  assert.equal(editorsRunOf([run(1), run(3, { conclusion: 'failure' }), run(2), run(4, { path: '.github/workflows/release.yml' })], sha).id, 2);
  assert.throws(() => editorsRunOf([run(1, { head_sha: 'b'.repeat(40) })], sha), /no successful ci.yml run/);
});

test('an npm lock binds every @runic-artifex entry to the registry and checks the candidates', () => {
  const candidates = [{ name: '@runic-artifex/react', version: '0.24.0', integrity: 'sha512-react' }, { name: '@runic-artifex/views', version: '0.24.0', integrity: 'sha512-views' }];
  const lock = packages => JSON.stringify({ name: 'app', lockfileVersion: 3, packages: { '': { name: 'app' }, ...packages } });
  const entry = (name, version, integrity) => ({ version, resolved: `https://registry.npmjs.org/${name}/-/${name.split('/').at(-1)}-${version}.tgz`, integrity });
  const { text, bound } = bindNpmLock(lock({
    'node_modules/@runic-artifex/react': entry('@runic-artifex/react', '0.24.0', 'sha512-react'),
    'node_modules/@runic-artifex/views': entry('@runic-artifex/views', '0.24.0', 'sha512-views'),
    // Released by Translations: npm serves it through the registry's pass-through.
    'node_modules/@runic-artifex/vite-plugin-runic-translations': entry('@runic-artifex/vite-plugin-runic-translations', '0.15.1', 'sha512-tr'),
    'node_modules/react': entry('react', '19.3.0', 'sha512-r'),
  }), candidates, 'http://127.0.0.1:48731');
  const packages = JSON.parse(text).packages;
  assert.equal(bound, 3);
  assert.equal(packages['node_modules/@runic-artifex/react'].resolved, 'http://127.0.0.1:48731/@runic-artifex/react/-/react-0.24.0.tgz');
  assert.equal(packages['node_modules/@runic-artifex/vite-plugin-runic-translations'].resolved,
    'http://127.0.0.1:48731/@runic-artifex/vite-plugin-runic-translations/-/vite-plugin-runic-translations-0.15.1.tgz');
  assert.equal(packages['node_modules/react'].resolved, 'https://registry.npmjs.org/react/-/react-19.3.0.tgz');
  assert.throws(() => bindNpmLock(lock({ 'node_modules/@runic-artifex/react': entry('@runic-artifex/react', '0.23.0', 'sha512-react') }), candidates, 'http://x'),
    /locks @runic-artifex\/react 0.23.0, but the candidate is 0.24.0/);
  assert.throws(() => bindNpmLock(lock({ 'node_modules/@runic-artifex/react': entry('@runic-artifex/react', '0.24.0', 'sha512-other') }), candidates, 'http://x'),
    /other bytes of @runic-artifex\/react 0.24.0/);
});

test('an npm lock entry that pins no bytes is bound to the candidate\'s; an unpinned non-candidate is refused', () => {
  const candidates = [{ name: '@runic-artifex/views', version: '0.24.0', integrity: 'sha512-views' }];
  const lock = packages => JSON.stringify({ name: 'app', lockfileVersion: 3, packages: { '': { name: 'app' }, ...packages } });
  // examples/split-ci locks its Runic entries without resolved and integrity.
  const { text, bound } = bindNpmLock(lock({ 'node_modules/@runic-artifex/views': { version: '0.24.0', license: 'MIT' } }), candidates, 'http://127.0.0.1:48731');
  assert.equal(bound, 1);
  assert.deepEqual(JSON.parse(text).packages['node_modules/@runic-artifex/views'],
    { version: '0.24.0', license: 'MIT', integrity: 'sha512-views', resolved: 'http://127.0.0.1:48731/@runic-artifex/views/-/views-0.24.0.tgz' });
  assert.throws(() => bindNpmLock(lock({ 'node_modules/@runic-artifex/views': { version: '0.23.0' } }), candidates, 'http://x'),
    /locks @runic-artifex\/views 0.23.0, but the candidate is 0.24.0/);
  assert.throws(() => bindNpmLock(lock({ 'node_modules/@runic-artifex/vite-plugin-runic-translations': { version: '0.15.1' } }), candidates, 'http://x'),
    /locks no integrity for @runic-artifex\/vite-plugin-runic-translations 0.15.1, which is not a candidate/);
});

const pwsh = spawnSync('pwsh', ['-NoProfile', '-Command', 'exit 0']).status === 0;
test('Use-Candidate.ps1 -Path takes a project whose bunfig.toml is the candidate\'s with CRLF line endings, and refuses another', { skip: !pwsh && 'pwsh is not on PATH' }, () => {
  const work = mkdtempSync(join(tmpdir(), 'use-candidate-'));
  try {
    const feed = join(work, 'runic-candidate');
    mkdirSync(feed);
    copyFileSync(join(import.meta.dirname, 'candidate-feed', 'Use-Candidate.ps1'), join(feed, 'Use-Candidate.ps1'));
    writeFileSync(join(feed, 'candidate.json'), JSON.stringify({ sdk: { version: '0.24.0', run: { id: 1, headSha: 'abc' } }, machine: { port: 48731 } }));
    writeFileSync(join(feed, 'candidate.npmrc'), machineNpmrc(remote, 48731));
    writeFileSync(join(feed, 'bunfig.toml'), machineBunfig(remote));
    writeFileSync(join(feed, 'NuGet.config'), machineNugetConfig(remote));
    const project = (name, bunfig) => {
      const folder = join(work, name);
      mkdirSync(folder);
      writeFileSync(join(folder, 'package.json'), '{}');
      writeFileSync(join(folder, 'bunfig.toml'), bunfig);
      return folder;
    };
    const run = folder => spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', join(feed, 'Use-Candidate.ps1'), '-Path', folder], { encoding: 'utf8' });
    // A git clone with core.autocrlf=true.
    const clone = project('clone', machineBunfig(remote).replaceAll('\n', '\r\n'));
    const ok = run(clone);
    assert.equal(ok.status, 0, ok.stderr || ok.stdout);
    assert.equal(readFileSync(join(clone, 'bunfig.toml'), 'utf8'), machineBunfig(remote).replaceAll('\n', '\r\n'));
    assert.match(readFileSync(join(clone, '.npmrc'), 'utf8'), /@runic-artifex:registry=http:\/\/127\.0\.0\.1:48731\//);
    const other = run(project('other', '[install]\r\nexact = true\r\n'));
    assert.notEqual(other.status, 0);
    assert.match(other.stderr + other.stdout, /other[/\\]bunfig\.toml exists: add the/);
  } finally { rmSync(work, { recursive: true, force: true }); }
});

test('the npm registry writes tarball URLs on the origin the client asked, so a forwarded port works', async () => {
  const work = mkdtempSync(join(tmpdir(), 'candidate-registry-'));
  let registry;
  try {
    mkdirSync(join(work, 'package'));
    writeFileSync(join(work, 'package', 'package.json'), JSON.stringify({ name: '@runic-artifex/views', version: '0.24.0' }));
    const archive = join(work, 'views-0.24.0.tgz');
    assert.equal(spawnSync('tar', ['-czf', archive, '-C', work, 'package']).status, 0);
    const ready = join(work, 'ready');
    registry = spawn(process.execPath, [join(import.meta.dirname, '../../tests/templates/template-npm-registry.mjs'), ready, archive], { stdio: 'ignore' });
    for (let attempt = 0; attempt < 200 && !(existsSync(ready) && readFileSync(ready, 'utf8').trim()); attempt++) await new Promise(done => setTimeout(done, 50));
    const port = Number(new URL(readFileSync(ready, 'utf8').trim()).port);
    const tarball = host => new Promise((done, fail) => {
      request({ host: '127.0.0.1', port, path: '/@runic-artifex%2fviews', headers: { host } }, response => {
        let body = '';
        response.on('data', chunk => { body += chunk; });
        response.on('end', () => done(JSON.parse(body).versions['0.24.0'].dist.tarball));
      }).on('error', fail).end();
    });
    // ssh -R 127.0.0.1:48731 on the machine: the client asks for port 48731, not the registry's own.
    assert.equal(await tarball('127.0.0.1:48731'), 'http://127.0.0.1:48731/archives/views-0.24.0.tgz');
    assert.equal(await tarball(`127.0.0.1:${port}`), `http://127.0.0.1:${port}/archives/views-0.24.0.tgz`);
    assert.equal(await tarball('evil.example:80'), `http://127.0.0.1:${port}/archives/views-0.24.0.tgz`);
  } finally {
    registry?.kill();
    rmSync(work, { recursive: true, force: true });
  }
});

test('push copies the machine scripts, and Use-Candidate.ps1 reads the files push writes', () => {
  const folder = join(import.meta.dirname, 'candidate-feed');
  for (const file of ['Use-Candidate.ps1', 'bind-npm-lock.mjs']) assert.ok(existsSync(join(folder, file)), file);
  const script = readFileSync(join(folder, 'Use-Candidate.ps1'), 'utf8');
  for (const file of ['candidate.json', 'candidate.npmrc', 'bunfig.toml', 'NuGet.config', 'bind-npm-lock.mjs']) assert.ok(script.includes(`'${file}'`), file);
  assert.match(script, /--debug:custom-hive \$hive/);
  // bind-npm-lock.mjs runs with the machine's Node: built-in modules only.
  const imports = [...readFileSync(join(folder, 'bind-npm-lock.mjs'), 'utf8').matchAll(/from '([^']+)'/g)].map(match => match[1]);
  assert.ok(imports.every(name => name.startsWith('node:')), imports.join(', '));
});
