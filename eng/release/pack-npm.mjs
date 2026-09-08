import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';

// Invoke only from the serialized pack stage. Preserve the source manifest byte-for-byte.
// Bun includes gitHead from this manifest; unlike npm it does not add the field itself.
export function packNpm(directory, destination, source) {
  assert.match(source, /^[a-f0-9]{40}$/);
  const manifest = join(directory, 'package.json');
  const original = readFileSync(manifest);
  try {
    writeFileSync(manifest, JSON.stringify({...JSON.parse(original), gitHead:source}, null, 2)+'\n');
    execFileSync('bun',['pm','pack','--destination',destination],{cwd:directory,stdio:'inherit'});
  } finally { writeFileSync(manifest,original); }
}
