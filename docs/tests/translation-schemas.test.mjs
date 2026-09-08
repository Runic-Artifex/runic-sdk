import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';

const schemaRoot = path.resolve('public/schemas/translations');
const publishedRoot = path.resolve('build/schemas/translations');
const specificationRoot = path.resolve('../specs/translations/schemas');
const canonicalRoot = 'https://runic-artifex.eu/schemas/translations/';

test('translation schemas have canonical owned identifiers', async () => {
  const names = (await readdir(schemaRoot))
    .filter((name) => name.endsWith('.schema.json'))
    .sort();
  const specificationNames = (await readdir(specificationRoot))
    .filter((name) => name.endsWith('.schema.json'))
    .sort();
  assert.deepEqual(names, specificationNames);

  for (const name of names) {
    const source = await readFile(path.join(schemaRoot, name), 'utf8');
    const schema = JSON.parse(source);
    assert.equal(source, await readFile(path.join(specificationRoot, name), 'utf8'), name);
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
