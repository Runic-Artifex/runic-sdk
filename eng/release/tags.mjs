import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
const gh = args => spawnSync('gh', args, {encoding: 'utf8'});
const failure = (what, result) => new Error(`${what} failed: ${(result.stderr ?? '').trim() || `exit ${result.status ?? 'unavailable'}`}`);
// The commit a tag points at, or undefined only when GitHub answers 404 for the tag
// ref. git/ref/tags/<tag> matches exactly that tag (commits/<ref> would also match a
// branch); annotated tags are followed to their commit. Any other error fails.
export function tagCommit(repository, tag, run = gh) {
  let path = `repos/${repository}/git/ref/tags/${tag}`;
  for (let depth = 0; depth < 5; depth++) {
    const result = run(['api', path, '--jq', '.object.type + " " + .object.sha']);
    if (result.status !== 0) {
      if (depth === 0 && /HTTP 404/.test(result.stderr ?? '')) return undefined;
      throw failure(`Tag lookup for ${tag}`, result);
    }
    const [type, sha] = result.stdout.trim().split(' ');
    assert.match(sha ?? '', /^[a-f0-9]{40}$/, `Tag ${tag} lookup returned no object`);
    if (type === 'commit') return sha;
    assert.equal(type, 'tag', `Tag ${tag} points at a ${type}, not a commit`);
    path = `repos/${repository}/git/tags/${sha}`;
  }
  throw new Error(`Tag ${tag} nests too many annotated tags`);
}
// The release for a tag (drafts included), or undefined only when it does not exist.
export function releaseFor(repository, tag, fields, run = gh) {
  const result = run(['release', 'view', tag, '--repo', repository, '--json', fields]);
  if (result.status === 0) return JSON.parse(result.stdout);
  if (/release not found|HTTP 404/.test(result.stderr ?? '')) return undefined;
  throw failure(`Release lookup for ${tag}`, result);
}
