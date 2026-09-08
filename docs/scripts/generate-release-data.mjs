#!/usr/bin/env bun
import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import prettier from 'prettier';
import { readToolchain } from '../../eng/toolchain.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const output = resolve(root, 'docs/src/lib/generated/release-data.ts');
const arguments_ = process.argv.slice(2);
if (arguments_.some((arg) => arg !== '--check'))
  throw new Error('Usage: generate-release-data.mjs [--check]');
const source = await readFile(resolve(root, 'eng/workspace.json'), 'utf8');
const workspace = JSON.parse(source);
const names = new Set();
const packages = ['nuget', 'npm'].flatMap((ecosystem) =>
  workspace[ecosystem].map((entry) => {
    if (!entry.name || names.has(entry.name))
      throw new Error(`Invalid or duplicate package: ${entry.name}`);
    names.add(entry.name);
    const path = entry.path ?? dirname(entry.project);
    const product =
      Object.entries(workspace.components).find(
        ([id, component]) =>
          id !== 'examples' && component.paths.includes(path),
      )?.[0] ?? 'application';
    return {
      identity: entry.name,
      ecosystem,
      version: workspace.version,
      product,
      installKind:
        ecosystem === 'npm'
          ? 'npm-package'
          : entry.name.startsWith('dotnet-')
            ? 'dotnet-tool'
            : entry.name.endsWith('.Templates')
              ? 'dotnet-template'
              : 'nuget-package',
    };
  }),
);
// A candidate version is not publication evidence. Enable published instructions only
// when a verified public release record is introduced explicitly.
const data = {
  currentCandidate: {
    version: workspace.version,
    publication: 'unpublished',
    source: 'eng/workspace.json',
    workspaceSha256: createHash('sha256').update(source).digest('hex'),
    toolchain: readToolchain(root),
    packages,
  },
};
const config = await prettier.resolveConfig(output);
const expected = await prettier.format(
  `// Generated from eng/workspace.json. Do not edit.\nexport const releaseData = ${JSON.stringify(data, null, 2)} as const;\n`,
  { ...config, filepath: output },
);
if (arguments_.includes('--check')) {
  if ((await readFile(output, 'utf8')) !== expected)
    throw new Error(
      'Generated release data is stale; run generate:release-data',
    );
} else {
  await mkdir(dirname(output), { recursive: true });
  await writeFile(output, expected);
}
