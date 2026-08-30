import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import fixtures from './fixtures.mjs';

for (const [name, value] of Object.entries(fixtures)) {
  const expected = JSON.stringify(value) + '\n';
  const actual = await readFile(join(import.meta.dirname, 'fixtures', name), 'utf8');
  if (actual !== expected) throw new Error('Conformance fixture is stale: ' + name);
}
