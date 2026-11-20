// The patch path (D077.23; eng/train/README.md, "Patch releases"): an upstream patch release (Translations or Command
// Line x.y.z with z > 0) passes the gate against a runic-sdk whose template still pins an earlier patch of the same
// line. Instead of requiring runic-sdk main to pin the candidate first (R5), the gate binds the candidate into the
// template application it generates, in its own scratch directory: the Runic.Translations pins of the project files,
// the dotnet-runic-translations tool of .config/dotnet-tools.json and the Translations npm packages of the frontend's
// package.json and package-lock.json, as runic-sdk's template tests bind the npm integrities of its own candidates.
// Every scenario still runs, with the candidate. A Command Line patch has no template pin: the tools that bundle
// Runic.CommandLine privately keep an earlier patch of its line, which S6 accepts. runic-sdk adopts the patch in an
// ordinary pull request afterwards, where its own CI checks R5 again. Row P-sdk (runic-sdk's own publish) keeps R5
// as it is: its template must pin what its Directory.Packages.props builds against.
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { compareVersions, parseVersion, sameLine } from './versions.mjs';
import { npmRepoOf } from './nuget.mjs';

// The rows that publish the upstream release under test; runic-sdk's own publish (P-sdk) is not one.
export const PATCH_ROWS_EXCLUDED = ['P-sdk'];

const isPatch = version => {
  try { const parsed = parseVersion(version); return parsed.patch > 0 && parsed.prerelease.length === 0; } catch { return false; }
};

// What the gate binds for `row`: for Translations, the earlier patches of its line that the template pins
// (`templatePins`, lintTemplatePins' pins) and the candidate they become; for Command Line, the earlier patches of its
// line that the tools bundle (`bundled`, versions) and are accepted. Null for a repository without a patch to bind:
// not a patch, pins already equal to it, a pin on another line or a later one (R5 then fails as before), or row P-sdk.
export function patchBinding({ row, versions, templatePins = [], bundled = [] }) {
  if (PATCH_ROWS_EXCLUDED.includes(row)) return { translations: null, cli: null };
  const earlier = (found, candidate) => {
    if (!isPatch(candidate)) return null;
    const distinct = [...new Set(found)];
    if (!distinct.length || !distinct.every(version => { try { return sameLine(version, candidate) && compareVersions(version, candidate) <= 0; } catch { return false; } }))
      return null;
    const from = distinct.filter(version => version !== candidate).sort(compareVersions);
    return from.length ? { from, to: candidate } : null;
  };
  return {
    translations: earlier(templatePins.map(pin => pin.version), versions.translations),
    cli: earlier(bundled, versions.cli),
  };
}

export const describeBinding = binding => [
  binding.translations && `the template's Translations pins ${binding.translations.from.join(', ')} are bound to the candidate ${binding.translations.to} in the gate's scratch template applications`,
  binding.cli && `tools that bundle Runic.CommandLine ${binding.cli.from.join(', ')} keep it; the candidate ${binding.cli.to} is a patch of that line`,
].filter(Boolean);

const translationsNpm = name => npmRepoOf(name) === 'translations';

// Rewrites a generated template application (`output`) to the candidate: every project file's Runic.Translations*
// PackageReference and the dotnet-runic-translations tool on a `from` version, and the Translations npm packages of
// Frontend/package.json and package-lock.json. `integrityOf(name, version)` gives the candidate archive's integrity.
// Returns the files it changed. A lock entry it cannot bind fails, so a template change cannot silently test the pin.
export async function bindTemplateApplication(output, { from, to }, integrityOf) {
  const changed = [];
  const update = (file, text) => { if (text !== readFileSync(file, 'utf8')) { writeFileSync(file, text); changed.push(file); } };
  const old = new Set(from);
  for (const file of projectFiles(output)) {
    const text = readFileSync(file, 'utf8');
    update(file, text.replace(/(<PackageReference\s+Include="Runic\.Translations(?:\.[A-Za-z]+)?"\s+Version=")([^"]+)(")/g,
      (match, prefix, version, suffix) => old.has(version) ? `${prefix}${to}${suffix}` : match));
  }
  const tools = join(output, '.config/dotnet-tools.json');
  if (existsSync(tools)) {
    const text = readFileSync(tools, 'utf8');
    update(tools, text.replace(/("dotnet-runic-translations"\s*:\s*\{\s*"version"\s*:\s*")([^"]+)(")/g,
      (match, prefix, version, suffix) => old.has(version) ? `${prefix}${to}${suffix}` : match));
  }
  const manifest = join(output, 'Frontend/package.json');
  if (existsSync(manifest)) {
    const text = readFileSync(manifest, 'utf8');
    update(manifest, text.replace(/("(@runic-artifex\/[^"]+)"\s*:\s*")([^"]+)(")/g,
      (match, prefix, name, version, suffix) => translationsNpm(name) && old.has(version) ? `${prefix}${to}${suffix}` : match));
  }
  const lockFile = join(output, 'Frontend/package-lock.json');
  if (existsSync(lockFile)) {
    const lock = JSON.parse(readFileSync(lockFile, 'utf8'));
    for (const [path, entry] of Object.entries(lock.packages ?? {})) {
      for (const field of ['dependencies', 'devDependencies', 'optionalDependencies'])
        for (const [name, version] of Object.entries(entry[field] ?? {}))
          if (translationsNpm(name) && old.has(version)) entry[field][name] = to;
      const name = path.slice(path.lastIndexOf('node_modules/') + 'node_modules/'.length);
      if (!path.includes('node_modules/') || !translationsNpm(name) || !old.has(entry.version)) continue;
      const integrity = await integrityOf(name, to);
      if (!integrity) throw new Error(`No integrity for ${name} ${to}: the patch path cannot bind ${lockFile}`);
      Object.assign(entry, { version: to, integrity,
        resolved: `https://registry.npmjs.org/${name}/-/${name.slice(name.lastIndexOf('/') + 1)}-${to}.tgz` });
    }
    update(lockFile, `${JSON.stringify(lock, null, 2)}\n`);
  }
  return changed;
}

function projectFiles(directory) {
  return readdirSync(directory).flatMap(name => {
    const path = join(directory, name);
    if (['node_modules', 'bin', 'obj', '.git'].includes(name)) return [];
    if (statSync(path).isDirectory()) return projectFiles(path);
    return /\.(cs|fs)proj$|^Directory\.(Packages|Build)\.props$/.test(name) ? [path] : [];
  });
}

// The integrity of a Translations npm package: the candidate archive's when Translations is a CI candidate, else
// the published package's from npm (a published patch, as in the nightly N-pub row).
export function integritySource(source, { fetchImpl = fetch } = {}) {
  const archives = new Map((source.npm ?? []).map(item => [`${item.name}@${item.version}`, item.file]));
  return async (name, version) => {
    const archive = archives.get(`${name}@${version}`);
    if (archive) return `sha512-${createHash('sha512').update(readFileSync(archive)).digest('base64')}`;
    const response = await fetchImpl(`https://registry.npmjs.org/${name}/${version}`, { headers: { accept: 'application/json' } });
    return response.ok ? (await response.json()).dist?.integrity ?? null : null;
  };
}
