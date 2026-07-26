import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { spawnSync } from 'node:child_process';

const command = process.argv[2];
if (!command) {
  throw new Error('A workspace command is required.');
}

const packageRoots = [
  join(import.meta.dirname, '..', 'web', 'packages'),
  join(import.meta.dirname, '..', 'samples', 'Todo.Frontends'),
].filter(existsSync);
if (packageRoots.length === 0) {
  console.log(`No web workspaces yet; ${command} skipped.`);
  process.exit(0);
}

const packages = packageRoots
  .flatMap(packagesRoot => readdirSync(packagesRoot, { withFileTypes: true })
    .filter(entry => entry.isDirectory())
    .map(entry => join(packagesRoot, entry.name, 'package.json')))
  .filter(existsSync)
  .map(path => JSON.parse(readFileSync(path, 'utf8')));

const runnablePackages = packages.filter(manifest => manifest.scripts?.[command]);
if (runnablePackages.length === 0) {
  console.log(`No web workspace defines ${command}; skipped.`);
  process.exit(0);
}

const packageNames = new Set(packages.map(manifest => manifest.name));
const pending = new Map(runnablePackages.map(manifest => [manifest.name, manifest]));
const completed = new Set();
const orderedPackages = [];

while (pending.size > 0) {
  const ready = [...pending.values()]
    .filter(manifest => {
      const dependencies = {
        ...manifest.dependencies,
        ...manifest.devDependencies,
        ...manifest.peerDependencies,
      };
      return Object.keys(dependencies)
        .filter(name => packageNames.has(name) && pending.has(name))
        .every(name => completed.has(name));
    })
    .sort((left, right) => left.name.localeCompare(right.name));

  if (ready.length === 0) {
    throw new Error(`Workspace dependency cycle detected: ${[...pending.keys()].join(', ')}`);
  }

  for (const manifest of ready) {
    orderedPackages.push(manifest);
    pending.delete(manifest.name);
    completed.add(manifest.name);
  }
}

const npmCli = process.platform === 'win32'
  ? [
      process.env.npm_execpath,
      join(dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js'),
    ].find(candidate => candidate && existsSync(candidate))
  : undefined;

if (process.platform === 'win32' && !npmCli) {
  throw new Error('Unable to locate npm-cli.js. Run this command through npm or use a Node installation that includes npm.');
}

const npmCommand = npmCli ? (process.env.npm_node_execpath ?? process.execPath) : 'npm';
for (const manifest of orderedPackages) {
  const result = spawnSync(
    npmCommand,
    [
      ...(npmCli ? [npmCli] : []),
      'run',
      command,
      '--workspace',
      manifest.name,
      '--if-present',
    ],
    { stdio: 'inherit' },
  );

  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}
