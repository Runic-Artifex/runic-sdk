import { test } from 'node:test';
import assert from 'node:assert/strict';
import { deflateRawSync } from 'node:zlib';
import { coveringRun, github, PLAN_ARTIFACT } from './covered.mjs';
import { planFor, affected, planRecord, reusedPlan } from './plan.mjs';

const repository = 'Runic-Artifex/runic-sdk', sha = 'a'.repeat(40);
const url = id => `https://github.com/${repository}/actions/runs/${id}`;
const run = (id, extra = {}) => ({ id, event: 'merge_group', head_sha: sha, head_branch: `gh-readonly-queue/main/pr-7-${'b'.repeat(40)}`,
  path: '.github/workflows/ci.yml', repository: { full_name: repository }, head_repository: { full_name: repository }, html_url: url(id), ...extra });
const tested = planFor(affected(['packages/web/svelte/src/index.ts']));
const verify = conclusion => ({ jobs: [{ name: 'build', conclusion: 'success' }, { name: 'verify', conclusion }] });

// A fake GitHub: `runs` the listing, `jobs[id]` each run's jobs (verify passed by default), `plans[id]` each run's record.
const fake = ({ runs = [run(9)], jobs = {}, plans = { 9: planRecord(tested) }, extra = {} } = {}) => {
  const paths = [];
  const api = async path => {
    paths.push(path);
    if (path.startsWith('actions/workflows/ci.yml/runs?')) return { workflow_runs: runs };
    const match = /^actions\/runs\/(\d+)(\/jobs\?per_page=100)?$/.exec(path);
    if (match?.[2]) return jobs[match[1]] ?? verify('success');
    if (match) return extra[match[1]];
    throw new Error(`unexpected ${path}`);
  };
  return { repository, sha, api, planOf: async item => plans[item.id] ?? null, paths };
};

test('a passed merge queue run of the commit covers the push with its recorded plan', async () => {
  const lookup = fake();
  assert.deepEqual(await coveringRun(lookup), { url: url(9), plan: tested });
  assert.equal(lookup.paths[0], `actions/workflows/ci.yml/runs?event=merge_group&head_sha=${sha}&per_page=20`);
  // The newest passed run is taken; a failed one is passed over.
  const two = fake({ runs: [run(9), run(12)], jobs: { 12: verify('failure') } });
  assert.equal((await coveringRun(two)).url, url(9));
  await assert.rejects(coveringRun({ ...fake(), sha: 'main' }), /full commit SHA/);
});

test('only a ci.yml merge queue run of this repository and commit whose verify passed, with a plan record, covers a push', async () => {
  for (const [why, lookup] of [
    ['verify failed', fake({ jobs: { 9: verify('failure') } })],
    ['verify skipped', fake({ jobs: { 9: verify('skipped') } })],
    ['no verify job', fake({ jobs: { 9: { jobs: [{ name: 'build', conclusion: 'success' }] } } })],
    ['a push run', fake({ runs: [run(9, { event: 'push' })] })],
    ['a pull request run', fake({ runs: [run(9, { event: 'pull_request' })] })],
    ['another commit', fake({ runs: [run(9, { head_sha: 'c'.repeat(40) })] })],
    ['another workflow', fake({ runs: [run(9, { path: '.github/workflows/nix.yml' })] })],
    ['another repository', fake({ runs: [run(9, { head_repository: { full_name: 'fork/runic-sdk' } })] })],
    ['no plan record (a run before plan records)', fake({ plans: {} })],
    ['a record of another schema', fake({ plans: { 9: { ...planRecord(tested), schema: 2 } } })],
    ['an invalid plan', fake({ plans: { 9: planRecord({ ...tested, skip: ['everything'] }) } })],
    ['no runs', fake({ runs: [] })],
  ]) assert.equal(await coveringRun(lookup), null, why);
});

test('an entry that reused a pull request run is covered by that run\'s plan', async () => {
  const pull = { id: 5, event: 'pull_request', path: '.github/workflows/ci.yml', head_sha: 'd'.repeat(40), repository: { full_name: repository },
    head_repository: { full_name: repository }, html_url: url(5) };
  const reused = (extra = {}, more = {}) => fake({ plans: { 9: planRecord(reusedPlan(url(5)), url(5)), 5: planRecord(tested), ...more.plans },
    extra: { 5: { ...pull, ...extra } }, jobs: more.jobs });
  assert.deepEqual(await coveringRun(reused()), { url: `${url(9)} (which reused ${url(5)})`, plan: tested });
  for (const [why, lookup] of [
    ['a fork', reused({ head_repository: { full_name: 'fork/runic-sdk' } })],
    ['a push run', reused({ event: 'push' })],
    ['its verify failed', reused({}, { jobs: { 5: verify('failure') } })],
    ['no record', reused({}, { plans: { 5: null } })],
    ['a record that reused again', reused({}, { plans: { 5: planRecord(reusedPlan(url(4)), url(4)) } })],
  ]) assert.equal(await coveringRun(lookup), null, why);
  assert.equal(await coveringRun(fake({ plans: { 9: planRecord(reusedPlan('elsewhere'), 'elsewhere') } })), null, 'no run id');
});

// One stored or deflated entry, as upload-artifact zips the ci-plan file.
function zip(name, data) {
  const body = deflateRawSync(data), file = Buffer.from(name);
  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(8, 8);
  local.writeUInt32LE(body.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(file.length, 26);
  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt16LE(8, 10);
  central.writeUInt32LE(body.length, 20); central.writeUInt32LE(data.length, 24); central.writeUInt16LE(file.length, 28);
  const start = local.length + file.length + body.length;
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(1, 8); end.writeUInt16LE(1, 10);
  end.writeUInt32LE(central.length + file.length, 12); end.writeUInt32LE(start, 16);
  return Buffer.concat([local, file, body, central, file, end]);
}

test('the CLI client reads the run\'s ci-plan artifact', async () => {
  const record = planRecord(tested);
  const requests = [];
  const fetchImpl = async (target, init) => {
    requests.push({ url: target, auth: init.headers.authorization });
    const { pathname, searchParams } = new URL(target);
    if (pathname.endsWith('/actions/runs/9/artifacts'))
      return Response.json({ artifacts: [{ id: 70, name: searchParams.get('name'), expired: false, workflow_run: { id: 9 } },
        { id: 71, name: PLAN_ARTIFACT, expired: true, workflow_run: { id: 9 } }] });
    if (pathname.endsWith('/actions/artifacts/70/zip')) return new Response(zip('plan.json', Buffer.from(JSON.stringify(record))));
    return new Response('', { status: 404 });
  };
  const { planOf, api } = github(repository, 't', fetchImpl);
  assert.deepEqual(await planOf(run(9)), record);
  assert.equal(new URL(requests[0].url).searchParams.get('name'), 'ci-plan');
  assert.ok(requests.every(item => item.auth === 'Bearer t'));
  await assert.rejects(api('actions/runs/1'), /failed: 404/);
  // Another run's artifact, or none, is no record.
  assert.equal(await github(repository, 't', async () => Response.json({ artifacts: [{ id: 70, name: PLAN_ARTIFACT, expired: false,
    workflow_run: { id: 8 } }] })).planOf(run(9)), null);
});
