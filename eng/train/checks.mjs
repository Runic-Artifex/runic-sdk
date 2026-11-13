// The package-level checks of the train gate: framework key coverage (S2) and version ranges and pins (S6).
// Pure functions over package metadata; train-compat.mjs supplies the packages.
import { repoOf } from './nuget.mjs';
import { lineUpperBound, parseRange, parseVersion, rangeContains, releaseOf, sameLine } from './versions.mjs';

// ---- S2: key coverage -------------------------------------------------------------------------------------------

// faults.RCLI4001 carries its prompt diagnostic's message (text-keys.md), so the catalog has no message for it.
export const keysWithoutMessage = new Set(['faults.RCLI4001']);

// Command Line's machine-readable key list (D061.19). Accepted shapes: an array, or an object with `keys`; each
// item a key string or an object with `key` (or `id`/`name`) and optionally `removed`/`removedIn` and
// `message: false` or `message: null` (what Command Line 0.12's writer emits) for a key without a catalog message.
export function keysFromJson(text) {
  const document = JSON.parse(text);
  const items = Array.isArray(document) ? document : document.keys;
  if (!Array.isArray(items)) throw new Error('text-keys.json has no key array');
  const required = new Set(), removed = new Set(), withoutMessage = new Set();
  for (const item of items) {
    const key = typeof item === 'string' ? item : item?.key ?? item?.id ?? item?.name;
    if (typeof key !== 'string' || !key) throw new Error(`text-keys.json has an item without a key: ${JSON.stringify(item)}`);
    if (typeof item === 'object' && (item.removed === true || typeof item.removedIn === 'string' || typeof item.removed === 'string')) removed.add(key);
    else if (typeof item === 'object' && (item.message === false || item.message === null)) withoutMessage.add(key);
    else required.add(key);
  }
  return { required, removed, withoutMessage };
}

// The key table of docs/guides/command-line/text-keys.md: rows whose first cell is a code span.
export function keysFromMarkdown(text) {
  return { required: new Set([...text.matchAll(/^\| *`([a-zA-Z0-9.-]+)`/gm)].map(match => match[1])), removed: new Set(), withoutMessage: new Set() };
}

// The dotted message names of an RMF2 catalog: `group { name = ... }` nests, two spaces per level.
export function rmf2MessageNames(text) {
  const names = new Set(), stack = [];
  for (const line of text.split(/\r?\n/)) {
    const trimmed = line.trim();
    let match;
    if ((match = /^([A-Za-z0-9_]+)\s*\{$/.exec(trimmed))) { stack.push(match[1]); continue; }
    if (trimmed === '}' && stack.length) { stack.pop(); continue; }
    if ((match = /^([A-Za-z0-9_]+)\s*=/.exec(trimmed)) && line.length - line.trimStart().length === 2 * stack.length)
      names.add([...stack, match[1]].join('.'));
  }
  return names;
}

// Keys are kebab-case (hints.usage, diagnostics.prompt-required); catalog messages are camelCase.
const normalizeKey = key => key.replace(/-/g, '').toLowerCase();

export function keyCoverage({ required, removed, withoutMessage = new Set() }, messages) {
  const byKey = new Map([...messages].map(name => [normalizeKey(name), name]));
  const exempt = new Set([...keysWithoutMessage, ...withoutMessage].map(normalizeKey));
  const known = new Set([...required, ...withoutMessage, ...keysWithoutMessage].map(normalizeKey));
  const missing = [...required].filter(key => !exempt.has(normalizeKey(key)) && !byKey.has(normalizeKey(key))).sort();
  const stale = [...removed].filter(key => byKey.has(normalizeKey(key))).sort();
  const unknown = [...messages].filter(name => !known.has(normalizeKey(name)) && ![...removed].some(key => normalizeKey(key) === normalizeKey(name))).sort();
  return { missing, stale, unknown };
}

// ---- S6: ranges and pins ----------------------------------------------------------------------------------------

// Dependencies inside one repository that still carry the Translations minor range (D061.16, R4).
export const boundedWithinRepo = new Set([
  'Runic.Translations.CommandLine>Runic.Translations',
  'Runic.Translations.Hosting>Runic.Translations',
  'Runic.Translations.Wpf>Runic.Translations',
]);

// Cross-repository dependencies that are deliberately open (COMPATIBILITY.md Q6): Views.Translations follows the
// Translations minor through Runic.Translations, and Hosting is consumed at the application's own version.
export const openByDesign = new Set(['Runic.Application.Views.Translations>Runic.Translations.Hosting']);

// One train version per repository: every package carries the release of the repository's anchor package. A package
// on its own prerelease line (Views.CsWebUi 0.17.1-preview.1) belongs to the same release.
export function lintLockstep(sources) {
  const failures = [];
  for (const source of Object.values(sources)) {
    const release = releaseOf(source.version);
    for (const item of source.packages)
      if (releaseOf(item.version) !== release)
        failures.push({ check: 'lockstep', message: `${item.id} ${item.version} is not part of ${source.repository} ${source.version}` });
  }
  return failures;
}

// The version of a package in this combination: the candidate's own, or its repository's train version.
const trainVersion = (sources, id) => {
  const repo = repoOf(id);
  return sources[repo]?.packages.find(item => item.id.toLowerCase() === id.toLowerCase())?.version ?? sources[repo]?.version;
};

// Every cross-repository dependency, and the bounded ones within Translations, is [F, U): F on the line of the
// version this train ships, U = that line's upper bound in the -0.0 form (D061.13), never -0 (R3).
export function lintRanges(sources) {
  const failures = [];
  for (const source of Object.values(sources))
    for (const item of source.packages) {
      const seen = new Set();
      for (const dependency of item.nuspec.dependencies) {
        const target = repoOf(dependency.id);
        const pair = `${item.id}>${dependency.id}`;
        if (!target || seen.has(`${pair} ${dependency.range}`)) continue;
        seen.add(`${pair} ${dependency.range}`);
        if (openByDesign.has(pair) || (target === source.repo && !boundedWithinRepo.has(pair))) continue;
        const where = `${item.id} ${item.version} -> ${dependency.id} ${dependency.range || '(any)'}`;
        let range;
        try { range = parseRange(dependency.range); } catch (error) { failures.push({ check: 'range', message: `${where}: ${error.message}` }); continue; }
        const version = trainVersion(sources, dependency.id);
        if (!range.min || !range.minInclusive || !range.max || range.maxInclusive) {
          failures.push({ check: 'range-form', message: `${where}: not [floor, upper) (R2); expected [${version}, ${lineUpperBound(version)})` });
          continue;
        }
        const upper = parseVersion(range.max);
        if (upper.prerelease.length === 1 && upper.prerelease[0] === '0')
          failures.push({ check: 'range-form', message: `${where}: upper bound uses -0, which nuget.org rejects (R3); use ${lineUpperBound(range.min)}` });
        else if (range.max !== lineUpperBound(range.min))
          failures.push({ check: 'range-form', message: `${where}: upper bound is not ${lineUpperBound(range.min)} (D061.13: the next line's -0.0 everywhere)` });
        if (!rangeContains(range, version) || !sameLine(range.min, version))
          failures.push({ check: 'range', message: `${where}: excludes ${dependency.id} ${version} of this train, or is not on its line (R1 lockstep)` });
      }
    }
  return failures;
}

// The literal pins of runic-sdk's template (R5): Runic.Translations, .Build and the dotnet-runic-translations tool.
export function lintTemplatePins(files, translationsVersion) {
  const failures = [], found = [];
  for (const { name, text } of files) {
    for (const match of text.matchAll(/<PackageReference\s+Include="(Runic\.Translations(?:\.[A-Za-z]+)?)"\s+Version="([^"]+)"/g))
      found.push({ name, id: match[1], version: match[2] });
    for (const match of text.matchAll(/"dotnet-runic-translations"\s*:\s*\{\s*"version"\s*:\s*"([^"]+)"/g))
      found.push({ name, id: 'dotnet-runic-translations', version: match[1] });
  }
  for (const pin of found)
    if (pin.version !== translationsVersion)
      failures.push({ check: 'template-pin', message: `Runic.Application.Templates ${pin.name}: ${pin.id} ${pin.version}, but this train's Translations is ${translationsVersion} (R5)` });
  return { failures, pins: found };
}

// A tool that carries Runic.CommandLine privately carries this train's Command Line (D061.15, R1).
export function lintToolPins(tools, cliVersion) {
  const failures = [];
  for (const { id, version, bundled } of tools) {
    if (!bundled.length) continue;
    if (bundled.length !== 1 || bundled[0] !== cliVersion)
      failures.push({ check: 'tool-pin', message: `${id} ${version} bundles Runic.CommandLine ${bundled.join(', ')}, but this train's Command Line is ${cliVersion}` });
  }
  return failures;
}
