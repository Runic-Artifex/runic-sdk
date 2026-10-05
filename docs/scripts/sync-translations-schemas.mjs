#!/usr/bin/env bun
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFile, mkdir, readFile, readdir, rm } from 'node:fs/promises';
import path from 'node:path';

import source from '../sources/translations-schemas.json' with { type: 'json' };

const [sourceDirectory, revision, ...extra] = process.argv.slice(2);
assert(
  sourceDirectory && revision && !extra.length,
  'Usage: bun docs/scripts/sync-translations-schemas.mjs <schema-source-directory> <pinned-revision>',
);

const sourceRoot = path.resolve(sourceDirectory);
const mirrorRoot = path.resolve('docs/public/schemas/translations');
assert.equal(
  revision,
  source.revision,
  'Source revision must match docs/sources/translations-schemas.json',
);

const names = async (root) =>
  (await readdir(root)).filter((name) => name.endsWith('.schema.json')).sort();
const schemaNames = await names(sourceRoot);
assert.ok(schemaNames.length, 'Source contains no translation schemas');

const digest = createHash('sha256');
for (const name of schemaNames) {
  digest.update(name);
  digest.update('\0');
  digest.update(await readFile(path.join(sourceRoot, name)));
  digest.update('\0');
}
assert.equal(
  `sha256:${digest.digest('hex')}`,
  source.contentDigest,
  'Source schemas do not match the pinned digest',
);

await rm(mirrorRoot, { recursive: true, force: true });
await mkdir(mirrorRoot, { recursive: true });
for (const name of schemaNames)
  await copyFile(path.join(sourceRoot, name), path.join(mirrorRoot, name));

console.log(
  `Synchronized ${schemaNames.length} translation schemas from ${source.repository}@${source.revision}.`,
);
