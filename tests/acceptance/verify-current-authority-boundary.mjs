#!/usr/bin/env node
import { readFile, readdir } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = new URL('../', import.meta.url);
const rootPath = fileURLToPath(root);
const allowlist = JSON.parse(await readFile(new URL('./current-evidence-allowlist.json', import.meta.url), 'utf8'));
if (allowlist.schema !== 'runic.examples-historical-evidence-allowlist/1' || typeof allowlist.purpose !== 'string' || !Array.isArray(allowlist.paths) || new Set(allowlist.paths).size !== allowlist.paths.length || allowlist.paths.some((path) => typeof path !== 'string' || !path.startsWith('eng/current-') || path.includes('..'))) throw new Error('Historical evidence allowlist is malformed');
const permitted = new Set(allowlist.paths);
const historical = [
  /\bRunicTranslations(?:\.|\b)/,
  /\bRunicToolkit(?:\.|\b)/,
  /RunicTranslations\.Editor/,
  /artifacts\/candidate-feed/,
  /\b\d+\.\d+\.\d+-preview\.(?:[a-f0-9]{7,}|w\d+)/,
];
const retiredSourceIdentity = /\b(?:namespace|using)\s+RunicToolkit\.|<RootNamespace>RunicToolkit\./;

async function files(directory, pattern = /\.(?:mjs|json|md)$/) {
  const result = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.name === '.git' || entry.name === 'node_modules' || entry.name === 'v0.2-baselines') continue;
    const path = join(directory, entry.name);
    if (entry.isDirectory()) result.push(...await files(path, pattern));
    else if (entry.isFile() && pattern.test(entry.name)) result.push(path);
  }
  return result;
}

const violations = [];
for (const path of await files(join(rootPath, 'eng'))) {
  const name = relative(rootPath, path).replaceAll('\\', '/');
  if (name === 'eng/verify-current-authority-boundary.mjs') continue;
  const text = await readFile(path, 'utf8');
  if (historical.some((pattern) => pattern.test(text)) && !permitted.has(name)) violations.push(name);
}
for (const path of permitted) {
  try {
    const text = await readFile(join(rootPath, path), 'utf8');
    if (!historical.some((pattern) => pattern.test(text))) violations.push(`${path} (does not contain retained historical evidence)`);
  } catch { violations.push(`${path} (missing allowlisted evidence)`); }
}
for (const directory of ['samples', 'integrations']) {
  for (const path of await files(join(rootPath, directory), /\.(?:cs|csproj)$/)) {
    if (retiredSourceIdentity.test(await readFile(path, 'utf8'))) violations.push(relative(rootPath, path));
  }
}
if (violations.length) throw new Error(`Retired identities escaped their explicit evidence boundary: ${[...new Set(violations)].sort().join(', ')}`);
