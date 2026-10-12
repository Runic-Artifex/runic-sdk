import { test, expect } from 'bun:test';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { zipEntries } from '../nupkg.mjs';
import { releaseLinks } from './readme-links.mjs';

const root = fileURLToPath(new URL('../..', import.meta.url));
const repository = 'Runic-Artifex/runic-sdk';
const version = '0.2.0-preview.1';
const xml = value => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;');

// Characters that MSBuild treats specially in properties, items and paths, and a
// shell continuation at the end of a line, with LF and CRLF line ends.
const hostile = [
  '# Package', '', '```sh',
  'dotnet runic size --runtime linux-x64 \\', '  --report measurements/linux.json \\', '  --verbose', '```', '',
  'Paths C:\\temp\\x and \\\\server\\share, an escape "\\u00FC\\n", a; b ; c, 100% %3B %5C, $(Property) @(Items) *.cs',
  '[guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guide.md#setup)',
  'a last line that ends in a backslash \\',
];

// Packs a project whose README is `source` with the repository's release-links.targets
// and returns the README in the package with its folder relative to the repository
// root, which the packed relative links name.
function packedReadme(source) {
  const directory = mkdtempSync(join(tmpdir(), 'runic-readme-pack-'));
  try {
    mkdirSync(join(directory, 'Package'));
    writeFileSync(join(directory, 'Directory.Build.props'),
      `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageVersion>${version}</PackageVersion><RepositoryUrl>https://github.com/${repository}</RepositoryUrl></PropertyGroup></Project>`);
    writeFileSync(join(directory, 'Directory.Build.targets'), `<Project><Import Project="${xml(resolve(root, 'eng/build/release-links.targets'))}" /></Project>`);
    // An empty feed keeps the restore offline: the project has no packages.
    writeFileSync(join(directory, 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>');
    writeFileSync(join(directory, 'Package/Package.csproj'),
      '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><PackageId>Readme.Package</PackageId><PackageReadmeFile>README.md</PackageReadmeFile></PropertyGroup><ItemGroup><None Include="README.md" Pack="true" PackagePath="\\" /></ItemGroup></Project>');
    writeFileSync(join(directory, 'Package/README.md'), source);
    const packed = spawnSync('dotnet', ['pack', join(directory, 'Package/Package.csproj'), '-c', 'Release', '-o', join(directory, 'packages'), '--disable-build-servers', '-p:UseSharedCompilation=false'], { encoding: 'utf8', timeout: 100000 });
    expect(packed.status, packed.stdout + packed.stderr).toBe(0);
    const readme = zipEntries(join(directory, `packages/Readme.Package.${version}.nupkg`)).find(entry => entry.name === 'README.md');
    return { text: readme.read().toString('utf8'), directory: relative(root, join(directory, 'Package')).split(sep).join('/') };
  } finally { rmSync(directory, { recursive: true, force: true }); }
}

// The packed README is the source with its links stamped, and nothing else.
function expectStamped(source) {
  const { text, directory } = packedReadme(source);
  // readme-links.mjs trims a leading dot of the directory, which only a README
  // outside the repository, as here, starts with. Compare through a placeholder.
  const stamped = releaseLinks(source, { repository, tag: `v${version}`, directory: 'readme-directory' });
  expect(text).toBe(stamped.replaceAll('/readme-directory/', `/${directory}/`));
  // The reported defect, stated directly: lines that end in a backslash survive.
  const continuations = lines => lines.filter(line => line.endsWith('\\'));
  const lines = value => value.split(/\r?\n/);
  expect(continuations(lines(text))).toEqual(continuations(lines(source)));
  expect(text.split('\\').length).toBe(source.split('\\').length);
}

test('a packed README keeps backslashes, shell continuations and MSBuild-special text', { timeout: 120000 }, () => {
  expectStamped(hostile.join('\n') + '\n');
});

test('a packed README keeps CRLF line ends and the missing final newline', { timeout: 120000 }, () => {
  expectStamped(hostile.join('\r\n'));
});

test('the dotnet-runic README packs as its source with stamped links', { timeout: 120000 }, () => {
  const source = readFileSync(join(root, 'tools/dotnet-runic/README.md'), 'utf8');
  expect(source).toMatch(/ \\\r?\n/);
  expectStamped(source);
});
