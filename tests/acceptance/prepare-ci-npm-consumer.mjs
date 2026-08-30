import { cp, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';

const root = process.cwd();
const feed = process.env.RUNIC_CANDIDATE_NPM_FEED;
const backup = process.env.RUNNER_TEMP ? join(process.env.RUNNER_TEMP, 'runic-candidate-npm-inputs') : join(root, '.ci', 'npm-inputs');
if (!feed) throw new Error('RUNIC_CANDIDATE_NPM_FEED must name the local candidate archive directory');

const inputs = [
  'package-lock.json',
  'samples/03-SetupApplication/Frontend/package.json',
  'samples/04-SvelteKitSetupApplication/Frontend/package.json',
  'samples/05-RunicTranslationsSetup/Frontend/package.json',
];
await mkdir(backup, { recursive: true });
for (const input of inputs) {
  await mkdir(join(backup, dirname(input)), { recursive: true });
  await cp(join(root, input), join(backup, input));
}

const archives = Object.fromEntries(
  await Promise.all(
    (await readdir(feed)).filter((name) => name.endsWith('.tgz')).map(async (name) => {
      const archive = join(feed, name);
      const manifest = JSON.parse(execFileSync('tar', ['-xOf', archive, 'package/package.json'], { encoding: 'utf8' }));
      return [manifest.name, archive];
    }),
  ).then((entries) => entries.filter(([name]) => name.startsWith('@runic-artifex/'))),
);

const required = [
  '@runic-artifex/application-bridge',
  '@runic-artifex/desktop',
  '@runic-artifex/svelte',
  '@runic-artifex/sveltekit',
  '@runic-artifex/vite-plugin-runic',
  '@runic-artifex/vite-plugin-runic-translations',
];
for (const name of required) if (!archives[name]) throw new Error(`Missing local npm candidate '${name}'`);

for (const path of inputs.filter((input) => input.endsWith('/package.json'))) {
  const manifest = JSON.parse(await readFile(join(root, path), 'utf8'));
  for (const section of ['dependencies', 'devDependencies']) {
    for (const name of Object.keys(manifest[section] ?? {})) {
      if (archives[name]) manifest[section][name] = `file:${archives[name]}`;
    }
  }
  await writeFile(join(root, path), `${JSON.stringify(manifest, null, 2)}\n`);
}

await writeFile(join(backup, 'manifest.json'), `${JSON.stringify({ inputs }, null, 2)}\n`);
console.log(backup);
