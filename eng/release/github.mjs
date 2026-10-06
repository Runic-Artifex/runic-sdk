import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { writeFileSync, readFileSync, readdirSync, mkdirSync, existsSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';
import { REPOSITORY, VERSION, sha256 } from './artifacts.mjs';
import { tagCommit, releaseFor } from './tags.mjs';
// describe <packages> <release>: writes the release assets locally (no network): the package
//   bundle, a CycloneDX SBOM of the packages and SHA256SUMS. Both are deterministic for a commit.
// release <release> [--dry-run]: checks that the tag and any existing release belong to this
//   source, then (not in a dry run) creates or resumes the prerelease with those assets.
const dryRun = process.argv.includes('--dry-run');
const [command, ...args] = process.argv.slice(2).filter(arg => arg !== '--dry-run');
const source = execFileSync('git', ['rev-parse', 'HEAD'], {encoding: 'utf8'}).trim();
const tag = `v${VERSION}`;
const archive = `runic-sdk-${VERSION}-packages.tar.gz`, sbom = `runic-sdk-${VERSION}.cdx.json`, sums = 'SHA256SUMS';
const assets = [archive, sbom, sums];
if (command === 'describe' && args.length === 2 && !dryRun) {
  const [packages, output] = args.map(path => resolve(path));
  const epoch = execFileSync('git', ['show', '-s', '--format=%ct', source], {encoding: 'utf8'}).trim();
  mkdirSync(output, {recursive: true});
  // Sorted entries, commit time and fixed owners: the same packages always give the same bundle.
  const tar = execFileSync('tar', ['--sort=name', `--mtime=@${epoch}`, '--owner=0', '--group=0', '--numeric-owner',
    '--mode=u=rwX,go=rX', '--format=gnu', '-cf', '-', '-C', packages, 'nuget', 'npm'], {maxBuffer: 2 ** 31 - 1});
  writeFileSync(join(output, archive), gzipSync(tar, {level: 9}));
  const files = ['nuget', 'npm'].flatMap(registry => readdirSync(join(packages, registry)).sort().map(file => join(packages, registry, file)));
  execFileSync(process.platform === 'win32' ? 'python' : 'python3', [fileURLToPath(new URL('./sbom.py', import.meta.url)),
    '--repository', REPOSITORY, '--version', VERSION, '--source', source, '--epoch', epoch, '--output', join(output, sbom), ...files], {stdio: 'inherit'});
  writeFileSync(join(output, sums), [archive, sbom].map(name => `${sha256(readFileSync(join(output, name)))}  ${name}\n`).join(''));
  console.log(`Described ${tag}: ${assets.join(', ')}`);
  process.exit(0);
}
assert(command === 'release' && args.length === 1, 'Use github.mjs describe <packages> <release> or github.mjs release <release> [--dry-run]');
const directory = resolve(args[0]);
const gh = args => execFileSync('gh', args, {encoding: 'utf8'}).trim();
const release = releaseFor(REPOSITORY, tag, 'isDraft,targetCommitish');
// A pre-existing tag must agree with this run even if no release exists yet.
const tagged = tagCommit(REPOSITORY, tag);
if (release && !release.isDraft) {
  assert.equal(tagged, source, 'Existing release belongs to different source');
  console.log(`Release ${tag} already exists; preserving its notes and assets.`);
} else {
  if (release) assert.equal(release.targetCommitish, source, 'Existing draft belongs to different source');
  if (tagged !== undefined) assert.equal(tagged, source, 'Tag belongs to different source');
  for (const name of assets) assert(existsSync(join(directory, name)), `Missing ${name}; run github.mjs describe first`);
  if (dryRun) {
    console.log(`Would ${release ? 'resume the draft' : 'create the prerelease'} ${tag} at ${source} with ${assets.join(', ')}.`);
    process.exit(0);
  }
  const notes = resolve('eng', 'release', 'notes', `${VERSION}.md`);
  const noteArguments = existsSync(notes) ? ['--notes-file', notes]
    : ['--notes', `Install matching ${VERSION} packages from NuGet and npm. See the repository README for getting started.`];
  if (!release) gh(['release', 'create', tag, '--repo', REPOSITORY, '--target', source, '--title', `Runic SDK ${VERSION}`,
    '--generate-notes', ...noteArguments, '--prerelease', '--draft']);
  // Upload while still a draft, so retries can finish an interrupted upload
  // before GitHub makes an immutable public release.
  gh(['release', 'upload', tag, '--repo', REPOSITORY, '--clobber', ...assets.map(name => join(directory, name))]);
  gh(['release', 'edit', tag, '--repo', REPOSITORY, '--draft=false']);
  console.log(`Created ${tag}.`);
}
