import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';

const schemaRoot = path.resolve('public/schemas/translations');
const publishedRoot = path.resolve('build/schemas/translations');
const canonicalRoot = 'https://runic-artifex.eu/schemas/translations/';

test('translation schemas have canonical owned identifiers', async () => {
  const names = (await readdir(schemaRoot))
    .filter((name) => name.endsWith('.schema.json'))
    .sort();
  assert.equal(names.length, 13);

  for (const name of names) {
    const source = await readFile(path.join(schemaRoot, name), 'utf8');
    const schema = JSON.parse(source);
    assert.equal(schema.$id, canonicalRoot + name, name);
    assert.equal(
      schema.$schema,
      'https://json-schema.org/draft/2020-12/schema',
      name,
    );
    assert.equal(
      await readFile(path.join(publishedRoot, name), 'utf8'),
      source,
      name,
    );
  }
});
