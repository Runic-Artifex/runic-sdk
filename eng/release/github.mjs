import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { writeFileSync, readFileSync, mkdirSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { REPOSITORY, VERSION, sha256 } from './artifacts.mjs';
const [packages, output] = process.argv.slice(2);
assert(packages && output, 'Use github.mjs <packages> <output>');
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
  const directory = resolve(output);
  mkdirSync(directory, {recursive: true});
  const archive = `runic-sdk-${VERSION}-packages.tar.gz`;
  execFileSync('tar', ['-czf', join(directory, archive), '-C', resolve(packages), 'nuget', 'npm']);
  writeFileSync(join(directory, 'SHA256SUMS'), `${sha256(readFileSync(join(directory, archive)))}  ${archive}\n`);
  // A pre-existing tag must agree with this run even if no release exists yet.
  const tagged = spawnSync('gh', ['api', `repos/${REPOSITORY}/commits/${tag}`, '--jq', '.sha'], {encoding: 'utf8'});
  if (tagged.status === 0) assert.equal(tagged.stdout.trim(), source, 'Tag belongs to different source');
  if (!release) gh(['release', 'create', tag, '--repo', REPOSITORY, '--target', source, '--title', `Runic SDK ${VERSION}`,
    '--generate-notes', '--notes', `Install matching ${VERSION} packages from NuGet and npm. See the repository README for getting started.`,
    '--prerelease', '--draft']);
  // Upload while still a draft, so retries can finish an interrupted upload
  // before GitHub makes an immutable public release.
  gh(['release', 'upload', tag, '--repo', REPOSITORY, '--clobber', join(directory, archive), join(directory, 'SHA256SUMS')]);
  gh(['release', 'edit', tag, '--repo', REPOSITORY, '--draft=false']);
  console.log(`Created ${tag}.`);
}
