import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { authority, scan, json, sha256, validateCandidate, VERSION, REPOSITORY } from './artifacts.mjs';
import { registryMatches } from './registry.mjs';
import { runChecked } from './process.mjs';
const root = fileURLToPath(new URL('../..', import.meta.url));
const [command, ...args] = process.argv.slice(2);
function verify(directory, candidate) {
  validateCandidate(candidate);
  assert.equal(execFileSync('git', ['rev-parse', 'HEAD'], {cwd: root, encoding: 'utf8'}).trim(), candidate.source);
  assert.deepEqual(scan(directory, authority(json(join(root, 'eng/workspace.json'))), candidate.source), candidate.packages);
}
if (command === 'prepare') {
  // CI's needs dependency supplies test completion. This inventory records the
  // downloaded packages, without a second acceptance policy or receipt system.
  const [directory, source, ciRunId, output] = args;
  assert.match(source, /^[a-f0-9]{40}$/); assert.match(ciRunId, /^[1-9][0-9]*$/);
  const body = {schema: 'runic.preview/1', version: VERSION, repository: REPOSITORY,
    source, ciRunId, packages: scan(directory, authority(json(join(root, 'eng/workspace.json'))), source)};
  const candidate = {...body, digest: sha256(JSON.stringify(body))};
  verify(directory, candidate);
  writeFileSync(output, JSON.stringify(candidate, null, 2) + '\n');
} else if (command === 'verify') verify(args[0], json(args[1]));
else if (command === 'registry') {
  const candidate = json(args[0]); validateCandidate(candidate);
  for (const p of candidate.packages)
    assert(await registryMatches(p, {waitForAvailability: true}), `Not available: ${p.name}`);
} else if (command === 'publish') {
  const [directory, manifest] = args, candidate = json(manifest);
  verify(directory, candidate);
  assert.equal(process.env.GITHUB_REPOSITORY, REPOSITORY, 'Publish from the release repository');
  assert.equal(process.env.GITHUB_SHA, candidate.source, 'Publish the tested workflow source');
  assert.equal(process.env.GITHUB_RUN_ID, candidate.ciRunId, 'Publish artifacts from this workflow run');
  assert.equal(process.env.GITHUB_REF, 'refs/heads/main', 'Publish from main');
  assert(process.env.ACTIONS_ID_TOKEN_REQUEST_URL, 'OIDC unavailable');
  // Partial publication may be resumed, but an existing version is never replaced.
  const pending = [];
  for (const p of candidate.packages) if (!await registryMatches(p)) pending.push(p);
  for (const p of pending) {
    const path = resolve(directory, p.file);
    if (p.registry === 'npm') runChecked('npm', ['publish', path, '--tag', 'preview', '--access', 'public', '--provenance', '--registry', 'https://registry.npmjs.org']);
    else {
      assert(process.env.NUGET_API_KEY, 'NuGet OIDC login missing');
      runChecked('dotnet', ['nuget', 'push', path, '--source', 'https://api.nuget.org/v3/index.json', '--api-key', process.env.NUGET_API_KEY]);
    }
  }
} else throw new Error('Use prepare <packages> <source> <run> <manifest>, verify <packages> <manifest>, registry <manifest>, or publish <packages> <manifest>');
