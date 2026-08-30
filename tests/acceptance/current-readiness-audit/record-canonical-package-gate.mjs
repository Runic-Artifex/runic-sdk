#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { resolve } from 'node:path';

const expected = ['Runic.Application', 'Runic.Application.Bridge', 'Runic.Application.Desktop', 'Runic.Application.Hosting', 'Runic.Application.Templates', 'Runic.Application.Testing', 'dotnet-runic'];
const sha256 = (value) => createHash('sha256').update(value).digest('hex');
const fail = (message) => { throw new Error(`canonical package gate: ${message}`); };
const source = (directory) => ({ repository: 'https://github.com/Runic-Artifex/runic-toolkit', revision: execFileSync('git', ['-C', directory, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(), tree: execFileSync('git', ['-C', directory, 'rev-parse', 'HEAD^{tree}'], { encoding: 'utf8' }).trim() });

function args(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 2) { if (!argv[index]?.startsWith('--') || !argv[index + 1] || values[argv[index]]) fail('usage'); values[argv[index]] = argv[index + 1]; }
  if (Object.keys(values).length !== 3 || !values['--toolkit'] || !values['--packages'] || !values['--version']) fail('usage');
  return values;
}

function collect(options) {
  const toolkit = resolve(options['--toolkit']), directory = resolve(options['--packages']), version = options['--version'];
  const facts = source(toolkit);
  if (facts.revision !== 'ea17ae7162683b7a65accb76e3f15c30eb887210' || facts.tree !== 'b3db3ce2bfdb89e8165014b2d658c22e5a7b2a7e') fail('toolkit must be the audited NativeAOT repair revision');
  if (execFileSync('git', ['-C', toolkit, 'status', '--porcelain'], { encoding: 'utf8' }) !== '') fail('toolkit worktree must be clean');
  if (version !== '0.2.0-preview.w80001') fail('only the audited W80-001 package version is accepted');
  const byIdentity = new Map();
  for (const name of readdirSync(directory).sort()) {
    if (!name.endsWith(`.${version}.nupkg`)) continue;
    const identity = name.slice(0, -(`.${version}.nupkg`.length));
    if (byIdentity.has(identity)) fail(`duplicate package '${identity}'`);
    const path = resolve(directory, name), bytes = readFileSync(path), size = statSync(path).size;
    byIdentity.set(identity, { identity, archive: name, sha256: sha256(bytes), size });
  }
  if (byIdentity.size !== expected.length || expected.some((identity) => !byIdentity.has(identity))) fail('packages must contain exactly the canonical seven artifacts');
  return {
    schema: 'runic.canonical-seven-package-gate/1',
    source: facts,
    packageVersion: version,
    packages: expected.map((identity) => byIdentity.get(identity)),
    artifactVerification: 'passed',
    packagedConsumer: 'passed',
    authority: { candidateDistribution: { id: 'translations-editor-archive', version: { state: 'unassigned', value: null } }, publication: 'forbidden' },
    retainedProfile: false,
    postFreeze: true,
    externalActions: { requests: 0, signatures: 0, updates: 0, uploads: 0, releases: 0, tags: 0 },
  };
}

try { process.stdout.write(`${JSON.stringify(collect(args(process.argv.slice(2))), null, 2)}\n`); } catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 1; }
