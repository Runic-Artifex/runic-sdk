// Binds a frontend's package-lock.json to the candidate npm registry (Use-Candidate.ps1; eng/ci/candidate-feed.mjs in
// runic-sdk). npm refuses a locked @runic-artifex package whose `resolved` is registry.npmjs.org once the scope has
// another registry (EALLOWREMOTE), so each such entry points at the forwarded registry instead, which serves the
// candidates and passes Runic packages released elsewhere (Translations) through to npm. A candidate's locked version
// and integrity must be the candidate's: the template's locks were written against the bytes the publish ships. An entry
// that pins no bytes (no integrity, as examples/split-ci leaves its Runic entries) is bound to the candidate's, so npm
// checks the candidate's bytes. Refused: another version than the candidate's, another integrity than the candidate's,
// and a Runic package that is no candidate but locks no integrity (nothing would check the bytes the registry hands out).
// pnpm and Bun locks name no registry and need nothing. Node built-ins only: it runs on the test machine.
//
//   node bind-npm-lock.mjs <package-lock.json>
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export function bindNpmLock(text, candidates, registry) {
  const lock = JSON.parse(text);
  const byName = new Map(candidates.map(candidate => [candidate.name, candidate]));
  let bound = 0;
  for (const [path, entry] of Object.entries(lock.packages ?? {})) {
    const at = path.lastIndexOf('node_modules/');
    if (at < 0 || entry.link) continue;
    const name = path.slice(at + 'node_modules/'.length);
    if (!name.startsWith('@runic-artifex/')) continue;
    const candidate = byName.get(name);
    if (candidate) {
      if (entry.version !== candidate.version) throw new Error(`package-lock.json locks ${name} ${entry.version}, but the candidate is ${candidate.version}`);
      if (entry.integrity === undefined) entry.integrity = candidate.integrity;
      else if (entry.integrity !== candidate.integrity) throw new Error(`package-lock.json locks other bytes of ${name} ${entry.version} than the candidate's`);
    } else if (entry.integrity === undefined) {
      throw new Error(`package-lock.json locks no integrity for ${name} ${entry.version}, which is not a candidate: pin it (npm install) or bind it by hand`);
    }
    entry.resolved = `${registry}/${name}/-/${name.slice(name.indexOf('/') + 1)}-${entry.version}.tgz`;
    bound++;
  }
  return { text: `${JSON.stringify(lock, null, 2)}\n`, bound };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const lockPath = process.argv[2];
  if (!lockPath) { console.error('Usage: node bind-npm-lock.mjs <package-lock.json>'); process.exit(2); }
  try {
    const candidate = JSON.parse(readFileSync(join(import.meta.dirname, 'candidate.json'), 'utf8'));
    const { text, bound } = bindNpmLock(readFileSync(lockPath, 'utf8'), candidate.sdk.npm, `http://127.0.0.1:${candidate.machine.port}`);
    writeFileSync(lockPath, text);
    console.log(`${lockPath}: ${bound} @runic-artifex entries resolve from the candidate registry.`);
  } catch (error) {
    console.error(`${lockPath}: ${error.message}`);
    process.exit(1);
  }
}
