// eng/ci/runner-wait.py against a stub gh: the macOS cap (D077.26(1)), the alarm and train-feed-exit.yml's wait.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { root } from '../run.mjs';

// runic-sdk has one queued run with `$STUB_DIR/macos` macOS jobs (a count that the stub lowers by one after each run
// listing, when `$STUB_DIR/drain` exists) and one Linux job; the other repositories have nothing.
const STUB = `#!/usr/bin/env bash
case "$2" in
  repos/Runic-Artifex/runic-sdk/actions/runs\\?status=queued*) echo '{"workflow_runs":[{"id":1,"head_branch":"pr"}]}' ;;
  */actions/runs/1/jobs*)
    count=$(cat "$STUB_DIR/macos")
    jobs='{"labels":["ubuntu-24.04"],"status":"queued"}'
    for _ in $(seq "$count"); do jobs="$jobs,{\\"labels\\":[\\"macos-26\\"],\\"status\\":\\"queued\\"}"; done
    if [ -f "$STUB_DIR/drain" ] && [ "$count" -gt 0 ]; then echo $((count - 1)) > "$STUB_DIR/macos"; fi
    echo "{\\"jobs\\":[$jobs]}" ;;
  *created=*) echo '{"workflow_runs":[]}'; echo created >> "$STUB_DIR/alarm" ;;
  *) echo '{"workflow_runs":[]}' ;;
esac
`;

function runnerWait(args, { macos = 2, drain = false } = {}) {
  const stub = mkdtempSync(join(tmpdir(), 'runner-wait-'));
  try {
    writeFileSync(join(stub, 'gh'), STUB);
    chmodSync(join(stub, 'gh'), 0o755);
    writeFileSync(join(stub, 'macos'), String(macos));
    if (drain) writeFileSync(join(stub, 'drain'), '');
    const result = spawnSync('python3', ['-I', resolve(root, 'eng/ci/runner-wait.py'), ...args], {
      encoding: 'utf8', env: { ...process.env, PATH: `${stub}:${process.env.PATH}`, STUB_DIR: stub } });
    return { ...result, alarmChecked: spawnSync('test', ['-f', join(stub, 'alarm')]).status === 0 };
  } finally {
    rmSync(stub, { recursive: true, force: true });
  }
}

test('the coordinator\'s check counts macOS jobs against the cap of 5 and reports the waits', () => {
  const room = runnerWait(['--macos-jobs', '3']);
  assert.equal(room.status, 0, room.stderr);
  assert.match(room.stdout, /^macOS jobs queued or running: 2 of 5 \(runic-sdk:pr \(2\)\)\nlinux    no jobs in the last 2 h\n/);
  assert.match(room.stdout, /Room for 3 more macOS job\(s\)\.\n$/);
  assert.ok(room.alarmChecked);
  const full = runnerWait(['--macos-jobs', '4']);
  assert.equal(full.status, 1);
  assert.match(full.stdout, /macOS cap: 2 \+ 4 would exceed 5; wait before opening this PR\./);
});

test('--cap-only checks the cap alone, and --wait-minutes polls until the jobs fit', () => {
  const cap = runnerWait(['--cap-only', '--macos-jobs', '3']);
  assert.equal(cap.status, 0, cap.stderr);
  assert.equal(cap.stdout, 'macOS jobs queued or running: 2 of 5 (runic-sdk:pr (2))\nRoom for 3 more macOS job(s).\n');
  assert.equal(cap.alarmChecked, false);
  // 4 macOS jobs running, 3 to add: two polls later only 2 are left.
  const waited = runnerWait(['--cap-only', '--macos-jobs', '3', '--wait-minutes', '1', '--interval', '0'], { macos: 4, drain: true });
  assert.equal(waited.status, 0, waited.stderr);
  assert.match(waited.stdout, /^macOS jobs queued or running: 4 of 5; 3 more would not fit\. Asking again in 0 s\.\nmacOS jobs queued or running: 3 of 5;/);
  assert.match(waited.stdout, /macOS jobs queued or running: 2 of 5 \(runic-sdk:pr \(2\)\)\nRoom for 3 more macOS job\(s\)\.\n$/);
  const timeout = runnerWait(['--cap-only', '--macos-jobs', '3', '--wait-minutes', '0.001', '--interval', '0.05'], { macos: 4 });
  assert.equal(timeout.status, 1);
  assert.match(timeout.stdout, /macOS cap: 4 \+ 3 would exceed 5 after waiting 0\.001 min; wait before opening this PR\./);
});
