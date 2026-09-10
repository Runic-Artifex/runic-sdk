// Offline integrity check. Updating the pin and hashes is an explicit source change.
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
const root = new URL('../../packages/dotnet/Runic.Platform.Linux.Portal/Protocol/', import.meta.url);
const manifest = JSON.parse(await readFile(new URL('upstream.json', root), 'utf8'));
if (!/^[0-9a-f]{40}$/.test(manifest.revision)) throw new Error('Pin an exact upstream commit.');
for (const file of manifest.files) {
  const bytes = await readFile(new URL(file.file, root));
  const actual = createHash('sha256').update(bytes).digest('hex');
  if (actual !== file.sha256) throw new Error(`Protocol input changed: ${file.file}. Review upstream and update its pin/hash explicitly.`);
}
console.log(`PASS ${manifest.files.length} pinned portal XML inputs (${manifest.revision}).`);
