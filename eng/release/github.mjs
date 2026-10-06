import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { writeFileSync, readFileSync, mkdirSync, existsSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { REPOSITORY, VERSION, sha256 } from './artifacts.mjs';
// --dry-run checks that the tag and any existing release belong to this source, then stops before writing.
const dryRun = process.argv.includes('--dry-run');
const [packages, output] = process.argv.slice(2).filter(arg => arg !== '--dry-run');
assert(packages && output, 'Use github.mjs <packages> <output> [--dry-run]');
const source = execFileSync('git', ['rev-parse', 'HEAD'], {encoding: 'utf8'}).trim();
const tag = `v${VERSION}`;
const gh = args => execFileSync('gh', args, {encoding: 'utf8'}).trim();
const existing = spawnSync('gh', ['release', 'view', tag, '--repo', REPOSITORY, '--json', 'isDraft,targetCommitish'], {encoding: 'utf8'});
const release = existing.status === 0 ? JSON.parse(existing.stdout) : null;
if (release && !release.isDraft) {
  assert.equal(gh(['api', `repos/${REPOSITORY}/commits/${tag}`, '--jq', '.sha']), source, 'Existing release belongs to different source');
  console.log(`Release ${tag} already exists; preserving its notes and assets.`);
} else {
  if (release) assert.equal(release.targetCommitish, source, 'Existing draft belongs to different source');
  // A pre-existing tag must agree with this run even if no release exists yet.
  const tagged = spawnSync('gh', ['api', `repos/${REPOSITORY}/commits/${tag}`, '--jq', '.sha'], {encoding: 'utf8'});
  if (tagged.status === 0) assert.equal(tagged.stdout.trim(), source, 'Tag belongs to different source');
  if (dryRun) {
    console.log(`Would ${release ? 'resume the draft' : 'create the prerelease'} ${tag} at ${source}.`);
    process.exit(0);
  }
  const directory = resolve(output);
  mkdirSync(directory, {recursive: true});
  const archive = `runic-sdk-${VERSION}-packages.tar.gz`;
  execFileSync('tar', ['-czf', join(directory, archive), '-C', resolve(packages), 'nuget', 'npm']);
  writeFileSync(join(directory, 'SHA256SUMS'), `${sha256(readFileSync(join(directory, archive)))}  ${archive}\n`);
  const notes = resolve('eng', 'release', 'notes', `${VERSION}.md`);
  const noteArguments = existsSync(notes) ? ['--notes-file', notes]
    : ['--notes', `Install matching ${VERSION} packages from NuGet and npm. See the repository README for getting started.`];
  if (!release) gh(['release', 'create', tag, '--repo', REPOSITORY, '--target', source, '--title', `Runic SDK ${VERSION}`,
    '--generate-notes', ...noteArguments, '--prerelease', '--draft']);
  // Upload while still a draft, so retries can finish an interrupted upload
  // before GitHub makes an immutable public release.
  gh(['release', 'upload', tag, '--repo', REPOSITORY, '--clobber', join(directory, archive), join(directory, 'SHA256SUMS')]);
  gh(['release', 'edit', tag, '--repo', REPOSITORY, '--draft=false']);
  console.log(`Created ${tag}.`);
}
