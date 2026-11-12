// The three train repositories, their package ids, nuspecs and the isolated NuGet configuration of a gate run.
import { writeFileSync } from 'node:fs';
import { zipEntries } from '../nupkg.mjs';

export const repos = Object.freeze({
  cli: {
    key: 'cli', title: 'Command Line', repository: 'Runic-Artifex/runic-cli-sdk', anchor: 'Runic.CommandLine',
    artifact: () => 'command-line-packages', patterns: ['Runic.CommandLine', 'Runic.CommandLine.*'], npm: [],
  },
  translations: {
    key: 'translations', title: 'Translations', repository: 'Runic-Artifex/runic-translations-sdk', anchor: 'Runic.Translations',
    artifact: () => 'runic-translations-packages', patterns: ['Runic.Translations', 'Runic.Translations.*', 'dotnet-runic-translations'],
    npm: ['@runic-artifex/translations-svelte', '@runic-artifex/translations-sveltekit', '@runic-artifex/vite-plugin-runic-translations'],
  },
  sdk: {
    key: 'sdk', title: 'runic-sdk', repository: 'Runic-Artifex/runic-sdk', anchor: 'Runic.Application.Views',
    artifact: runId => `runic-sdk-${runId}`, patterns: ['Runic.*', 'dotnet-runic'], npm: ['@runic-artifex/*'],
  },
});
export const repoKeys = Object.keys(repos);

const patternMatches = (pattern, id) => pattern.endsWith('*')
  ? id.toLowerCase().startsWith(pattern.slice(0, -1).toLowerCase()) : id.toLowerCase() === pattern.toLowerCase();

// The repository that owns a package id, by NuGet source mapping's rule: the longest matching pattern wins.
export function repoOf(id) {
  let best = null;
  for (const repo of Object.values(repos))
    for (const pattern of repo.patterns)
      if (patternMatches(pattern, id) && (!best || pattern.length > best.pattern.length)) best = { repo: repo.key, pattern };
  return best?.repo ?? null;
}

export function npmRepoOf(name) {
  if (repos.translations.npm.includes(name)) return 'translations';
  return name.startsWith('@runic-artifex/') ? 'sdk' : null;
}

const unescapeXml = text => text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
const attribute = (element, name) => {
  const match = new RegExp(`\\s${name}\\s*=\\s*"([^"]*)"`).exec(element);
  return match ? unescapeXml(match[1]) : null;
};

// id, version and every dependency of a nuspec, with the target framework group it belongs to.
export function parseNuspec(text) {
  const metadata = /<metadata[^>]*>([\s\S]*?)<\/metadata>/.exec(text)?.[1] ?? '';
  const tag = name => unescapeXml(new RegExp(`<${name}>([^<]*)</${name}>`).exec(metadata)?.[1]?.trim() ?? '');
  const dependencies = [];
  const block = /<dependencies>([\s\S]*?)<\/dependencies>/.exec(metadata)?.[1] ?? '';
  const groups = [...block.matchAll(/<group\b([^>]*?)(?:\/>|>([\s\S]*?)<\/group>)/g)];
  const collect = (body, framework) => {
    for (const [element] of body.matchAll(/<dependency\b[^>]*>/g))
      dependencies.push({ id: attribute(element, 'id'), range: attribute(element, 'version') ?? '', framework });
  };
  if (groups.length) for (const [, attributes, body = ''] of groups) collect(body, attribute(attributes, 'targetFramework'));
  else collect(block, null);
  return { id: tag('id'), version: tag('version'), dependencies };
}

const entries = file => zipEntries(file).map(entry => ({ ...entry, name: decodeURIComponent(entry.name) }));

export function nupkgNuspec(file) {
  const nuspec = entries(file).find(entry => !entry.name.includes('/') && entry.name.endsWith('.nuspec'));
  if (!nuspec) throw new Error(`${file} has no nuspec`);
  return nuspec.read().toString('utf8');
}

// Files of a package whose path matches the expression, as { name, text }.
export function nupkgTexts(file, expression) {
  return entries(file).filter(entry => expression.test(entry.name)).map(entry => ({ name: entry.name, text: entry.read().toString('utf8') }));
}

// The versions of a package that a tool or build package carries privately, from the deps.json files it ships.
export function bundledVersions(file, id) {
  const pattern = new RegExp(`"${id.replace(/\./g, '\\.')}/([^"]+)"`, 'g');
  return [...new Set(nupkgTexts(file, /\.deps\.json$/).flatMap(({ text }) => [...text.matchAll(pattern)].map(match => match[1])))].sort();
}

const escapeXml = text => String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

// A nuget.config with one local feed per repository whose candidate is a CI run, and package source mapping, so
// that a candidate can never be replaced by a nuget.org package with the same id and version. `feeds` maps a
// repository key to a local folder, or to null when its packages come from nuget.org.
export function nugetConfig(feeds) {
  const sources = [['nuget.org', 'https://api.nuget.org/v3/index.json']];
  const mapping = new Map([['nuget.org', ['*']]]);
  for (const key of repoKeys) {
    const folder = feeds[key];
    const source = folder ? `train-${key}` : 'nuget.org';
    if (folder) { sources.push([source, folder]); mapping.set(source, []); }
    mapping.get(source).push(...repos[key].patterns);
  }
  return [
    '<?xml version="1.0" encoding="utf-8"?>',
    '<configuration>',
    '  <packageSources>',
    '    <clear />',
    ...sources.map(([key, value]) => `    <add key="${escapeXml(key)}" value="${escapeXml(value)}" />`),
    '  </packageSources>',
    '  <packageSourceMapping>',
    ...[...mapping].flatMap(([key, patterns]) => [
      `    <packageSource key="${escapeXml(key)}">`,
      ...patterns.map(pattern => `      <package pattern="${escapeXml(pattern)}" />`),
      '    </packageSource>',
    ]),
    '  </packageSourceMapping>',
    '</configuration>',
    '',
  ].join('\n');
}

export const writeNugetConfig = (path, feeds) => writeFileSync(path, nugetConfig(feeds));
