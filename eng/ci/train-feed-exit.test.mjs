// The open job of train-feed-exit.yml, run as GitHub runs its shell: its own run: text against a checkout whose origin
// is a local bare repository and a stub gh (the GitHub API, the pull requests).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { root } from '../run.mjs';

const workflow = Bun.YAML.parse(readFileSync(resolve(root, '.github/workflows/train-feed-exit.yml'), 'utf8'));
const [apply, push] = ['Apply the checked patch', 'Push the branch and open or update the pull request']
  .map(name => workflow.jobs.open.steps.find(step => step.name === name).run);
const REPOSITORY = 'Runic-Artifex/runic-sdk';
const marker = `<!-- train-handoff: feed-exit translations=0.15.0@${'e'.repeat(40)} -->`;
const props = attribute => `<Project>\n  <PackageVersion Include="Runic.Translations" Version="0.15.0"${attribute} />\n</Project>\n`;

const STUB = `#!/usr/bin/env bash
# gh api <path> [--jq <filter>], gh pr list|create|edit, answered from $STUB_DIR.
set -e
if [ "$1" = api ]; then
  case "$2" in
    repos/*/git/ref/heads/main) cat "$STUB_DIR/main" ;;
    users/*) echo 42 ;;
    repos/*/branches/*) if [ -f "$STUB_DIR/branch" ]; then cat "$STUB_DIR/branch"; else echo 'gh: Not Found (HTTP 404)' >&2; exit 1; fi ;;
    repos/*/git/commits/*) git -C "$STUB_ORIGIN" rev-parse "\${2##*/}^{tree}" ;;
    *) echo "unexpected gh api $2" >&2; exit 1 ;;
  esac
elif [ "$1" = pr ] && [ "$2" = list ]; then
  cat "$STUB_DIR/prs.json" 2>/dev/null || echo '[]'
else
  echo "$*" >> "$STUB_DIR/calls"
fi
`;

function scenario({ files = { 'Directory.Packages.props': props('') }, prs = null, mainMoved = false, branch = null } = {}) {
  const directory = mkdtempSync(join(tmpdir(), 'feed-exit-open-'));
  const env = { ...process.env, GIT_CONFIG_GLOBAL: join(directory, 'gitconfig'), GIT_CONFIG_NOSYSTEM: '1' };
  writeFileSync(env.GIT_CONFIG_GLOBAL, '');
  const git = (cwd, ...args) => execFileSync('git', args, { cwd, env, encoding: 'utf8', stdio: 'pipe' }).trim();
  const origin = join(directory, 'origin.git'), work = join(directory, 'work'), checkout = join(directory, 'checkout');
  git(directory, 'init', '--quiet', '--bare', '--initial-branch=main', origin);
  git(directory, 'clone', '--quiet', origin, work);
  mkdirSync(join(work, '.github'));
  writeFileSync(join(work, 'Directory.Packages.props'), props(` TrainCommit="${'e'.repeat(40)}"`));
  writeFileSync(join(work, '.github', 'ci.yml'), 'on: push\n');
  git(work, 'add', '.');
  git(work, '-c', 'user.name=t', '-c', 'user.email=t@example.invalid', 'commit', '--quiet', '-m', 'base');
  git(work, 'push', '--quiet', 'origin', 'HEAD:main');
  const base = git(work, 'rev-parse', 'HEAD');
  // The exit job's artifact: the patch against base, base-sha, exit.json and pr-body.md.
  const temp = join(directory, 'runner-temp'), out = join(temp, 'feed-exit'), stub = join(directory, 'stub');
  mkdirSync(out, { recursive: true });
  mkdirSync(stub);
  for (const [file, text] of Object.entries(files)) writeFileSync(join(work, file), text);
  git(work, 'add', '--all');
  writeFileSync(join(out, 'feed-exit.patch'), `${execFileSync('git', ['diff', '--cached', '--binary'], { cwd: work, env, encoding: 'utf8' })}`);
  writeFileSync(join(out, 'base-sha'), `${base}\n`);
  writeFileSync(join(out, 'exit.json'), JSON.stringify({ title: 'Train feed exit: Translations 0.15.0 from nuget.org', marker }));
  writeFileSync(join(out, 'pr-body.md'), `${marker}\nThe body.\n`);
  // A branch a previous run pushed: the same patch on base.
  if (branch) {
    git(work, '-c', 'user.name=t', '-c', 'user.email=t@example.invalid', 'commit', '--quiet', '-m', 'earlier');
    git(work, 'push', '--quiet', 'origin', 'HEAD:refs/heads/train-bot/feed-exit');
    writeFileSync(join(stub, 'branch'), git(work, 'rev-parse', 'HEAD'));
  }
  writeFileSync(join(stub, 'main'), mainMoved ? 'f'.repeat(40) : base);
  if (prs) writeFileSync(join(stub, 'prs.json'), JSON.stringify(prs));
  writeFileSync(join(stub, 'gh'), STUB);
  chmodSync(join(stub, 'gh'), 0o755);
  // actions/checkout of base-sha, without credentials.
  git(directory, 'clone', '--quiet', origin, checkout);
  git(checkout, 'checkout', '--quiet', '--detach', base);
  const summary = join(directory, 'summary.md');
  const run = (script, extra) => spawnSync('bash', ['--noprofile', '--norc', '-eo', 'pipefail', '-c', script], {
    cwd: checkout, encoding: 'utf8',
    env: { ...env, ...workflow.env, ...extra, PATH: `${stub}:${process.env.PATH}`, STUB_DIR: stub, STUB_ORIGIN: origin, RUNNER_TEMP: temp,
      GITHUB_REPOSITORY: REPOSITORY, GITHUB_STEP_SUMMARY: summary, GH_TOKEN: 'token' },
  });
  const job = dryRun => {
    const first = run(apply, { BASE_SHA: base, DRY_RUN: String(dryRun) });
    if (first.status !== 0) return first;
    return run(push, { APP_SLUG: 'runic-artifex-train', DRY_RUN: String(dryRun) });
  };
  return {
    job, base, git, origin, checkout,
    calls: () => existsSync(join(stub, 'calls')) ? readFileSync(join(stub, 'calls'), 'utf8') : '',
    remote: () => spawnSync('git', ['rev-parse', '--verify', '--quiet', 'refs/heads/train-bot/feed-exit'], { cwd: origin, env, encoding: 'utf8' }).stdout.trim(),
    summary: () => readFileSync(summary, 'utf8'),
    done: () => rmSync(directory, { recursive: true, force: true }),
  };
}

test('a dry run applies and checks the patch, then pushes and opens nothing', () => {
  const run = scenario();
  try {
    const result = run.job(true);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Dry run: would push [0-9a-f]{40} to train-bot\/feed-exit \(now absent\), then open a pull request\./);
    assert.equal(run.remote(), '');
    assert.equal(run.calls(), '');
    assert.match(run.summary(), /Pull requests on train-bot\/feed-exit: none/);
  } finally {
    run.done();
  }
});

test('a real run pushes the branch as the App\'s bot and opens the pull request', () => {
  const run = scenario();
  try {
    const result = run.job(false);
    assert.equal(result.status, 0, result.stderr);
    const pushed = run.remote();
    assert.notEqual(pushed, '');
    assert.equal(run.git(run.origin, 'show', '-s', '--format=%an <%ae>|%P|%s', pushed),
      `runic-artifex-train[bot] <42+runic-artifex-train[bot]@users.noreply.github.com>|${run.base}|Train feed exit: Translations 0.15.0 from nuget.org`);
    assert.equal(run.git(run.origin, 'show', `${pushed}:Directory.Packages.props`), props('').trim());
    assert.match(run.calls(), /^pr create --repo Runic-Artifex\/runic-sdk --base main --head train-bot\/feed-exit --title Train feed exit: Translations 0\.15\.0 from nuget\.org --body-file /);
  } finally {
    run.done();
  }
});

test('an open pull request whose branch has the same tree only gets its body refreshed', () => {
  const run = scenario({ branch: true, prs: [{ number: 5, state: 'OPEN', body: marker, url: 'u5' }] });
  try {
    const before = run.remote();
    const result = run.job(false);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Leave train-bot\/feed-exit at [0-9a-f]{40} \(the same tree\), then update #5\./);
    assert.equal(run.remote(), before);
    assert.match(run.calls(), /^pr edit 5 --repo Runic-Artifex\/runic-sdk --title /);
  } finally {
    run.done();
  }
});

test('a pull request the coordinator closed, a moved main and a path outside the allowlist stop the job', () => {
  const closed = scenario({ prs: [{ number: 4, state: 'CLOSED', body: marker, url: 'https://github.com/x/pull/4' }, { number: 3, state: 'MERGED', body: 'older', url: 'u3' }] });
  try {
    const result = closed.job(false);
    assert.equal(result.status, 1);
    assert.match(result.stdout, /https:\/\/github\.com\/x\/pull\/4 has this exit and was closed by the coordinator; reopen it or delete train-bot\/feed-exit/);
    assert.equal(closed.remote(), '');
  } finally {
    closed.done();
  }
  const moved = scenario({ mainMoved: true });
  try {
    const result = moved.job(false);
    assert.equal(result.status, 1);
    assert.match(result.stdout, /main moved from [0-9a-f]{40} to f{40} since the exit was computed; dispatch again/);
    // A dry run (a rehearsal of a past commit) does not ask.
    assert.equal(moved.job(true).status, 0);
  } finally {
    moved.done();
  }
  const outside = scenario({ files: { 'Directory.Packages.props': props(''), '.github/ci.yml': 'on: pull_request_target\n' } });
  try {
    const result = outside.job(true);
    assert.equal(result.status, 1);
    assert.match(result.stdout, /The patch is empty or changes paths or modes outside the allowlist: \.github\/ci\.yml/);
  } finally {
    outside.done();
  }
});
