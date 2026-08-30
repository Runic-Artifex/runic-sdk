#!/usr/bin/env node
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { generatedSchema, stableJson } from './contract.mjs';

const schemaPath = path.join(path.dirname(fileURLToPath(import.meta.url)), 'baseline.schema.json');
const expected = `${JSON.stringify(stableJson(generatedSchema()), null, 2)}\n`;
const check = process.argv.includes('--check');
if (check) {
  const actual = await fs.readFile(schemaPath, 'utf8');
  if (actual !== expected) { process.stderr.write('baseline.schema.json is not generated from contract.mjs\n'); process.exitCode = 1; }
} else await fs.writeFile(schemaPath, expected);
