import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { REPOSITORY, sha256 } from './artifacts.mjs';
import { companionContext } from './gates.mjs';

const filename = 'native-soak.json';
const maximumBytes = 64 * 1024 * 1024;
const objectId = /^[a-f0-9]{40}$/;
function githubJson(endpoint) {
  const result = spawnSync('gh', ['api', '--hostname', 'github.com', endpoint], {
    encoding: 'utf8', maxBuffer: 96 * 1024 * 1024, timeout: 60000,
  });
  // Never include subprocess output/error text: it may contain credentials or raw evidence.
  assert.ok(!result.error && result.status === 0, 'Companion GitHub API request failed');
  try { return JSON.parse(result.stdout); }
  catch { throw new Error('Companion GitHub API returned invalid JSON'); }
}

// The commit and output directory are trusted workflow inputs. Evidence may
// specify only the fixed filename and expected bytes, never an endpoint or path.
export function fetchCompanion(evidence, evidenceCommit, directory, api = githubJson) {
  assert.equal(evidence.schema, 'runic.preview-evidence/1');
  assert.ok(Array.isArray(evidence.receipts));
  const issues = evidence.receipts.filter(receipt => receipt.outcome === 'known-issue');
  if (!issues.length) {
    assert.ok(evidenceCommit === '' || evidenceCommit === undefined, 'Unexpected companion commit without a known issue');
    return { fetched: false };
  }
  assert.equal(issues.length, 1, 'Exactly one companion-backed known issue is supported');
  const receipt = issues[0];
  assert.equal(receipt.gate, 'soak-thirty-minutes');
  assert.equal(receipt.waiver?.schema, 'runic.soak-known-issue/1');
  assert.equal(receipt.waiver.rawReceiptFile, filename);
  assert.match(receipt.waiver.rawReceiptSha256, /^[a-f0-9]{64}$/);
  assert.match(evidenceCommit, objectId, 'An explicit immutable evidence commit is required');
  const prefix = `repos/${REPOSITORY}/git`;
  const commit = api(`${prefix}/commits/${evidenceCommit}`);
  assert.equal(commit.sha, evidenceCommit);
  assert.match(commit.tree?.sha, objectId);
  const tree = api(`${prefix}/trees/${commit.tree.sha}`);
  assert.equal(tree.sha, commit.tree.sha);
  assert.equal(tree.truncated, false);
  assert.ok(Array.isArray(tree.tree) && tree.tree.length === 1, 'Evidence tree must contain only the fixed data file');
  const entry = tree.tree[0];
  assert.equal(entry.path, filename);
  assert.equal(entry.mode, '100644');
  assert.equal(entry.type, 'blob');
  assert.match(entry.sha, objectId);
  assert.ok(Number.isSafeInteger(entry.size) && entry.size > 0 && entry.size <= maximumBytes);
  const blob = api(`${prefix}/blobs/${entry.sha}`);
  assert.equal(blob.sha, entry.sha);
  assert.equal(blob.encoding, 'base64');
  assert.equal(blob.size, entry.size);
  assert.equal(typeof blob.content, 'string');
  const encoded = blob.content.replace(/[\r\n]/g, '');
  assert.ok(encoded.length <= Math.ceil(maximumBytes / 3) * 4);
  const bytes = Buffer.from(encoded, 'base64');
  assert.equal(bytes.toString('base64'), encoded, 'Noncanonical base64 evidence');
  assert.equal(bytes.length, entry.size);
  assert.equal(createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex'), entry.sha);
  const digest = sha256(bytes);
  assert.equal(digest, receipt.waiver.rawReceiptSha256, 'Companion bytes differ from reviewed receipt');
  // Verify transport before touching disk. Semantic/source/final-binary checks
  // remain mandatory in the subsequent gates command, using these exact bytes.
  const transport = { schema:'runic.companion-transport/1', repository:REPOSITORY,
    evidenceCommit, tree:tree.sha, blob:entry.sha, file:filename, sha256:digest, bytes:bytes.length };
  mkdirSync(directory, {mode:0o700}); // Refuse existing directories and symlinks.
  companionContext(directory); // Also reject symlinks in parent components.
  writeFileSync(join(directory, filename), bytes, {flag:'wx', mode:0o600});
  writeFileSync(join(directory, 'transport.json'), JSON.stringify(transport, null, 2)+'\n', {flag:'wx', mode:0o600});
  return {fetched:true, ...transport};
}
