// Every diagnostic ID that shipped code reports has an entry in docs/diagnostics.md,
// and the project that reports it links to that entry at the release tag.
import { test, expect } from 'bun:test';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';

const root = resolve(import.meta.dir, '..');
const catalog = readFileSync(join(root, 'docs/diagnostics.md'), 'utf8');
const experimental = readFileSync(join(root, 'docs/experimental.md'), 'utf8');
// A diagnostic ID is a quoted run of at least three capitals and three digits,
// such as "RUNICBRIDGE003", "RAPPDEV1005" or Code="RUNICBRIDGE014".
const idPattern = /["']([A-Z]{3,}\d{3,})["']/g;
// Verification harnesses that do not ship in a package.
const unshipped = new Set(['tools/Runic.Administration.Verify']);
const skippedDirectories = new Set(['bin', 'obj', 'node_modules', 'dist', '.cache', 'content']);

function* sources(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      if (!skippedDirectories.has(entry.name) && !unshipped.has(relative(root, path))) yield* sources(path);
    } else if (/\.(cs|targets|props)$/.test(entry.name)) yield path;
  }
}

function projectOf(file) {
  for (let directory = dirname(file); directory !== root; directory = dirname(directory)) {
    const project = readdirSync(directory).find(name => name.endsWith('.csproj'));
    if (project) return join(directory, project);
  }
  throw new Error(`${relative(root, file)} belongs to no project`);
}

// IDs of [Experimental] attributes have their entries in docs/experimental.md,
// which eng/experimental.test.mjs checks.
const reports = new Map(); // ID -> files that report it
const projects = new Map(); // project -> {experimental, other}
for (const file of [...sources(join(root, 'packages/dotnet')), ...sources(join(root, 'tools'))]) {
  const text = readFileSync(file, 'utf8');
  const project = projectOf(file);
  const usage = projects.get(project) ?? { experimental: new Set(), other: new Set(), targets: new Set() };
  projects.set(project, usage);
  for (const [line] of text.matchAll(/^.*$/gm)) {
    for (const [, id] of line.matchAll(idPattern)) {
      if (/\[Experimental\(/.test(line)) { usage.experimental.add(id); continue; }
      reports.set(id, [...(reports.get(id) ?? []), relative(root, file)]);
      if (/\.(targets|props)$/.test(file)) usage.targets.add(id);
      else usage.other.add(id);
    }
  }
}
// The npm packages report IDs at the start of an error message, such as 'RUNICP007: devtools: true requires …'.
for (const directory of readdirSync(join(root, 'packages/web'))) {
  const source = join(root, 'packages/web', directory, 'src');
  if (!existsSync(source)) continue;
  for (const entry of readdirSync(source, { recursive: true }).filter(name => /\.(ts|svelte)$/.test(name))) {
    const file = join(source, entry);
    for (const [, id] of readFileSync(file, 'utf8').matchAll(/["'`]([A-Z]{3,}\d{3,})\b/g))
      reports.set(id, [...(reports.get(id) ?? []), relative(root, file)]);
  }
}
const property = (project, name) =>
  readFileSync(project, 'utf8').match(new RegExp(`<${name}>([^<]*)</${name}>`))?.[1];

test('the scan finds every diagnostic family', () => {
  const families = new Set([...reports.keys()].map(id => id.replace(/\d+$/, '')));
  for (const family of ['RUNICBRIDGE', 'RAPPCLI', 'RAPPDEV', 'RAS', 'RAPPSIZE', 'RCLI', 'RCREATE'])
    expect(families).toContain(family);
});

// D20: the release policy lists every family that shipped code or docs/experimental.md uses.
test('the release policy lists every diagnostic family', () => {
  const policy = readFileSync(join(root, 'eng/release/README.md'), 'utf8');
  const listed = new Set([...policy.matchAll(/^ *\| ((?:`[A-Z]+`(?:, )?)+) \|/gm)]
    .flatMap(match => [...match[1].matchAll(/`([A-Z]+)`/g)].map(prefix => prefix[1])));
  const ids = [...reports.keys(), ...[...experimental.matchAll(/`([A-Z]{3,}\d{3,})`/g)].map(match => match[1])];
  const families = [...new Set(ids.map(id => id.replace(/\d+$/, '')))];
  expect(families).toContain('RUNICP');
  expect(families.filter(family => !listed.has(family))).toEqual([]);
});

test('every reported diagnostic ID has a catalog entry', () => {
  // Experimental API IDs, also declared as constants, are described in docs/experimental.md.
  const missing = [...reports].filter(([id]) => !catalog.includes(`\n### ${id}\n`) && !experimental.includes(`${id}`))
    .map(([id, files]) => `${id} (${files[0]})`);
  expect(missing).toEqual([]);
});

test('every catalog entry is a reported diagnostic ID', () => {
  const stale = [...catalog.matchAll(/^### (\S+)$/gm)].map(match => match[1]).filter(id => !reports.has(id));
  expect(stale).toEqual([]);
});

test('reporting projects link to the catalog at the release tag', () => {
  const errors = [];
  for (const [project, usage] of projects) {
    const name = relative(root, project);
    if (usage.other.size && property(project, 'RunicGenerateReleaseLinks') !== 'true')
      errors.push(`${name} reports ${[...usage.other][0]} and must set RunicGenerateReleaseLinks`);
    if (usage.targets.size && property(project, 'RunicPackDiagnosticsHelpLinks') !== 'true')
      errors.push(`${name} reports ${[...usage.targets][0]} from MSBuild and must set RunicPackDiagnosticsHelpLinks`);
  }
  expect(errors).toEqual([]);
});

test('MSBuild diagnostics of packages carry a help link', () => {
  const unlinked = [];
  for (const file of sources(join(root, 'packages/dotnet'))) {
    if (!/\.(targets|props)$/.test(file)) continue;
    // Attributes are matched as quoted values, since message text may contain '>' (item transforms).
    for (const [task] of readFileSync(file, 'utf8').matchAll(/<(?:Error|Warning)\b(?:\s+[\w.]+="[^"]*")*\s*\/?>/g))
      if (/\bCode="[^"]+"/.test(task) && !/HelpLink="\$\(_RunicDiagnosticsHelpLinkBase\)[a-z0-9]+"/.test(task)) unlinked.push(`${relative(root, file)}: ${task}`);
  }
  expect(unlinked).toEqual([]);
});

test('the catalog sits where the help links point', () => {
  const targets = readFileSync(join(root, 'eng/build/release-links.targets'), 'utf8');
  expect(targets).toContain('<RunicDiagnosticsCatalog Condition="\'$(RunicDiagnosticsCatalog)\' == \'\'">docs/diagnostics.md</RunicDiagnosticsCatalog>');
  expect(targets).toContain('<_RunicDiagnosticsHelpLinkBase>$(RunicReleaseSourceUrl)$(RunicDiagnosticsCatalog)#</_RunicDiagnosticsHelpLinkBase>');
  expect(existsSync(join(root, 'docs/diagnostics.md'))).toBe(true);
});

// ViewsCodegen-09: one RUNICBRIDGE catalog. Its overview table lists every entry once with the severity that the
// generator (Diagnostics.Warn) or the build targets (<Warning Code=...>) report, and the guide states the same warnings.
test('the Bridge overview table matches the entries and the reported severities', () => {
  const generator = ['tools/Runic.Application.Views.Codegen/Program.cs', 'tools/Runic.Application.Views.Codegen/BridgeLoading.cs']
    .map(file => readFileSync(join(root, file), 'utf8')).join('\n');
  const constants = new Map([...generator.matchAll(/internal const string (\w+) = "(RUNICBRIDGE\d+)"/g)].map(match => [match[1], match[2]]));
  const targets = readFileSync(join(root, 'packages/dotnet/Runic.Application.Views/build/Runic.Application.Views.targets'), 'utf8');
  const warnings = new Set([
    ...[...generator.matchAll(/Diagnostics\.Warn\(BridgeDiagnosticCodes\.(\w+)/g)].map(match => constants.get(match[1])),
    ...[...targets.matchAll(/<Warning\b[^>]*\bCode="(RUNICBRIDGE\d+)"/g)].map(match => match[1]),
  ]);
  expect(warnings.size).toBeGreaterThan(0);
  const rows = [...catalog.matchAll(/^\| \[`(RUNICBRIDGE\d+)`\]\(#(runicbridge\d+)\) \| (Error|Warning) \|/gm)];
  const entries = [...catalog.matchAll(/^### (RUNICBRIDGE\d+)$/gm)].map(match => match[1]);
  expect(rows.map(row => row[1])).toEqual(entries);
  for (const [, id, anchor] of rows) expect(anchor).toBe(id.toLowerCase());
  expect(rows.filter(row => row[3] === 'Warning').map(row => row[1]).sort()).toEqual([...warnings].sort());
  const guide = readFileSync(join(root, 'packages/dotnet/Runic.Application.Views/README.md'), 'utf8');
  const stated = guide.match(/^`(RUNICBRIDGE\d+)`((?:, `\d+`)*) and `(\d+)` are warnings;/m);
  expect(stated).not.toBeNull();
  const listed = [stated[1], ...[...stated[2].matchAll(/`(\d+)`/g)].map(match => `RUNICBRIDGE${match[1]}`), `RUNICBRIDGE${stated[3]}`];
  expect(listed.sort()).toEqual([...warnings].sort());
  expect(guide).not.toMatch(/^\| `RUNICBRIDGE\d+`/m);
});

// I-028: docs/runicbridge-diagnostics.json is the machine-readable RUNICBRIDGE list that the generator writes from
// BridgeDiagnosticCodes.All (BridgeCodegen --list-diagnostics; Runic.Application.Testing.Tests checks that the file
// is its current output). It lists every catalog entry in order, with the overview table's severity and its
// Problem column, without code spans, as the title, and every code that BridgeDiagnosticCodes declares.
test('the RUNICBRIDGE list matches the catalog and BridgeDiagnosticCodes', () => {
  const list = JSON.parse(readFileSync(join(root, 'docs/runicbridge-diagnostics.json'), 'utf8'));
  expect(list.schemaVersion).toBe(1);
  for (const entry of list.diagnostics) expect(Object.keys(entry).sort()).toEqual(['id', 'severity', 'title']);
  const rows = [...catalog.matchAll(/^\| \[`(RUNICBRIDGE\d+)`\]\(#runicbridge\d+\) \| (Error|Warning) \| [^|]+ \| ([^|]+) \|$/gm)]
    .map(([, id, severity, problem]) => ({ id, severity: severity.toLowerCase(), title: problem.trim().replaceAll('`', '') }));
  expect(rows.length).toBe([...catalog.matchAll(/^### (RUNICBRIDGE\d+)$/gm)].length);
  expect(list.diagnostics).toEqual(rows);
  const generator = readFileSync(join(root, 'tools/Runic.Application.Views.Codegen/Program.cs'), 'utf8');
  const declared = [...generator.matchAll(/internal const string \w+ = "(RUNICBRIDGE\d+)"/g)].map(match => match[1]).sort();
  expect(list.diagnostics.map(entry => entry.id)).toEqual(declared);
});
