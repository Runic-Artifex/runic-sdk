import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, lstatSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
const workspaceAuthority = JSON.parse(readFileSync(new URL('../workspace.json', import.meta.url), 'utf8'));
export const VERSION = workspaceAuthority.version;
export const REPOSITORY = 'Runic-Artifex/runic-sdk';
export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
export const json = path => JSON.parse(readFileSync(path, 'utf8'));
export function authority(workspace) {
  assert.equal(workspace.version, VERSION, 'Synchronize workspace version before sealing');
  for (const registry of ['nuget', 'npm']) {
    assert(workspaceAuthority[registry].length > 0, `Empty ${registry} authority`);
    assert.deepEqual(workspace[registry].map(p => p.name).sort(), workspaceAuthority[registry].map(p => p.name).sort(), `Inventory differs from current ${registry} authority`);
  }
  const entries = ['nuget', 'npm'].flatMap(registry => workspace[registry].map(p => ({ registry, name: p.name })));
  assert.equal(new Set(entries.map(p => p.name)).size, entries.length, 'Duplicate inventory identity');
  return entries;
}
export function inspect(path, registry) {
  return JSON.parse(execFileSync(process.platform === 'win32' ? 'python' : 'python3', [fileURLToPath(new URL('./metadata.py', import.meta.url)), registry, path], {encoding:'utf8', maxBuffer: 16 * 1024 * 1024}));
}
export function dependencyOrder(packages) {
  const result = [], active = new Set(), done = new Set();
  function visit(p) {
    if (done.has(p.name)) return;
    assert(!active.has(p.name), `Dependency cycle: ${p.name}`); active.add(p.name);
    for (const name of Object.keys(p.dependencies)) { const dep = packages.find(q => q.name === name); if (dep) visit(dep); }
    active.delete(p.name); done.add(p.name); result.push(p);
  }
  packages.forEach(visit); return result;
}
export function scan(directory, inventory, source) {
  const packages = [];
  assert.deepEqual(readdirSync(directory).sort(), ['npm', 'nuget'], 'Artifact directory must contain only npm/ and nuget/');
  for (const registry of ['nuget','npm']) {
    for (const file of readdirSync(resolve(directory, registry)).sort()) {
      const path = resolve(directory, registry, file);
      assert(lstatSync(path).isFile() && !lstatSync(path).isSymbolicLink(), 'Artifact must be a regular file');
      assert(file.endsWith(registry === 'npm' ? '.tgz' : '.nupkg'), `Stale extra artifact ${file}`);
      const metadata = inspect(path, registry);
      assert(inventory.some(p => p.registry === registry && p.name === metadata.name), `Unexpected package ${metadata.name}`);
      assert.equal(metadata.version, VERSION, `Mixed version ${metadata.name}`);
      const repository = metadata.repository.replace(/^git\+/, '').replace(/\.git$/, '').replace(/\/$/, '');
      assert.equal(repository, `https://github.com/${REPOSITORY}`, `Obsolete repository ${metadata.name}`);
      assert.equal(metadata.source, source, `Missing/stale source revision ${metadata.name}`);
      for (const [name, range] of Object.entries(metadata.dependencies)) {
        assert(!/workspace:|file:|link:/.test(range), `Unpublishable dependency ${name}`);
        if (inventory.some(p => p.name === name)) {
          const exact = registry === 'nuget' ? `[${VERSION}]` : VERSION;
          assert.equal(range, exact, `Internal ${registry} dependency must pin candidate ${name}: ${range}; expected ${exact}`);
        }
      }
      packages.push({registry, file: `${registry}/${file}`, sha256: sha256(readFileSync(path)), ...metadata});
    }
  }
  assert.deepEqual(packages.map(p=>`${p.registry}:${p.name}`).sort(), inventory.map(p=>`${p.registry}:${p.name}`).sort(), 'Missing/duplicate inventory');
  return dependencyOrder(packages);
}
export function validateCandidate(candidate) {
  assert.equal(candidate.schema, 'runic.preview/1'); assert.equal(candidate.version, VERSION);
  assert.equal(candidate.repository, REPOSITORY); assert.match(candidate.source, /^[a-f0-9]{40}$/);
  assert.match(candidate.ciRunId, /^[1-9][0-9]*$/);
  const {digest, ...body} = candidate;
  assert.equal(digest, sha256(JSON.stringify(body)), 'Candidate manifest was changed');
}
