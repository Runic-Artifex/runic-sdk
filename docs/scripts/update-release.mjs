#!/usr/bin/env bun
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { writeFile } from 'node:fs/promises';

const [version, ...extra] = process.argv.slice(2);
assert(
  version && /^\d+\.\d+\.\d+(?:-[\w.-]+)?$/.test(version) && !extra.length,
  'Usage: bun run docs:release <published-version>',
);
const repository = 'Runic-Artifex/runic-sdk';
const tag = `v${version}`;
const gh = (args) => execFileSync('gh', args, { encoding: 'utf8' }).trim();
const release = JSON.parse(
  gh([
    'release',
    'view',
    tag,
    '--repo',
    repository,
    '--json',
    'isDraft,tagName,url',
  ]),
);
assert(
  !release.isDraft && release.tagName === tag,
  'Choose a published GitHub release',
);
const encoded = gh([
  'api',
  `repos/${repository}/contents/eng/workspace.json?ref=${tag}`,
  '--jq',
  '.content',
]);
const workspace = JSON.parse(Buffer.from(encoded, 'base64').toString());
assert.equal(
  workspace.version,
  version,
  'Release tag and package version must agree',
);
const packages = ['nuget', 'npm'].flatMap((ecosystem) =>
  workspace[ecosystem].map((entry) => {
    const path =
      entry.path ?? entry.project.slice(0, entry.project.lastIndexOf('/'));
    const product =
      Object.entries(workspace.components).find(
        ([id, component]) =>
          id !== 'examples' && component.paths.includes(path),
      )?.[0] ?? 'application';
    return {
      identity: entry.name,
      ecosystem,
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
assert.equal(
  new Set(packages.map((p) => p.identity)).size,
  packages.length,
  'Package identities must be unique',
);
const output = new URL('../src/lib/published-release.json', import.meta.url);
await writeFile(
  output,
  JSON.stringify({ version, url: release.url, packages }, null, 2) + '\n',
);
console.log(`Updated docs for ${tag}. Review and commit ${output.pathname}.`);
