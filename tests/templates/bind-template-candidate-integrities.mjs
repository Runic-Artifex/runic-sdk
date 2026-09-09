import {readFileSync, writeFileSync} from 'node:fs';
import {readNpmCandidates, verifyTemplateLock} from '../../eng/release/template-locks.mjs';

const [lockPath, ...archives] = process.argv.slice(2);
if (!lockPath || !archives.length) throw new Error('Usage: bind-template-candidate-integrities.mjs <lockfile> <npm-archive>...');
const text = readFileSync(lockPath, 'utf8');
const candidates = readNpmCandidates(archives);
verifyTemplateLock(text, lockPath, candidates);
// npm 12 requires explicit local tarball URLs for the acceptance registry. Hashes,
// versions and dependency declarations have already passed unchanged; only this
// transport URL may differ from what customers receive in the template package.
const registry = process.env.RUNIC_TEMPLATE_NPM_REGISTRY;
if (registry && lockPath.endsWith('package-lock.json')) {
  const lock = JSON.parse(text);
  for (const [path, entry] of Object.entries(lock.packages)) {
    const name = path.slice(path.lastIndexOf('node_modules/') + 'node_modules/'.length);
    if (path.includes('node_modules/') && candidates.has(name)) entry.resolved = `${registry}/${name}/-/${name.slice(name.lastIndexOf('/') + 1)}-${entry.version}.tgz`;
  }
  writeFileSync(lockPath, `${JSON.stringify(lock, null, 2)}\n`);
}
