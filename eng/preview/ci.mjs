import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { YAML } from 'bun';
import { webTests } from '../ci/plan.mjs';
import { REPOSITORY, validateCandidate } from './artifacts.mjs';
const api = path => JSON.parse(execFileSync('gh',['api',`repos/${REPOSITORY}/${path}`],{encoding:'utf8'}));
export function expectedCIJobs(workflow, webMatrix) {
  const names=[];
  for(const [id,job] of Object.entries(workflow.jobs)) {
    const matrix=job.strategy?.matrix;
    if(!matrix) { names.push(job.name ?? id); continue; }
    assert(job.name, `Matrix job ${id} requires an explicit stable name`);
    let rows;
    if(typeof matrix==='string') {
      assert.equal(id,'web','Unknown dynamic release matrix');
      assert.equal(matrix,'${{ fromJSON(needs.build.outputs.web) }}'); rows=webMatrix;
    } else if(matrix.include) { assert.deepEqual(Object.keys(matrix),['include'],'Unsupported mixed matrix'); rows=matrix.include; }
    else {
      rows=[{}];
      for(const [key,values] of Object.entries(matrix)) { assert(Array.isArray(values),'Unsupported matrix expansion'); rows=rows.flatMap(row=>values.map(value=>({...row,[key]:value}))); }
    }
    assert(rows.length>0,`Empty release matrix ${id}`);
    for(const row of rows) {
      const name=job.name.replace(/\$\{\{\s*matrix\.(\w+)\s*\}\}/g,(_,key)=>{assert(key in row,`Missing matrix ${key}`);return String(row[key]);});
      assert(!name.includes('${{'),'Unsupported job name expression'); names.push(name);
    }
  }
  assert.equal(new Set(names).size,names.length,'Duplicate CI job identities'); return names.sort();
}
export function verifyRun(candidate, run, jobs, artifacts, authority) {
  validateCandidate(candidate);
  assert.equal(authority.defaultBranch,'main','Unexpected repository default branch');
  assert.equal(run.head_branch,authority.defaultBranch,'CI must run on the current default branch');
  assert.equal(authority.headSha,candidate.source,'Candidate is not current remote default-branch HEAD');
  assert.equal(String(run.id), candidate.ciRunId); assert.equal(run.head_sha,candidate.source);
  assert.equal(run.repository.full_name,REPOSITORY); assert.equal(run.head_repository.full_name,REPOSITORY);
  assert.equal(run.path,'.github/workflows/ci.yml'); assert.equal(run.status,'completed'); assert.equal(run.conclusion,'success');
  assert.equal(run.event,'push', 'Release authority requires full push CI');
  assert(jobs.length > 0 && jobs.every(j=>j.conclusion==='success'), 'Skipped/failed CI job');
  assert(authority.expectedJobs.includes('verify'));
  assert.deepEqual(jobs.map(j=>j.name).sort(),[...authority.expectedJobs].sort(),'Missing, duplicate, or unexpected CI matrix jobs');
  const a=artifacts.filter(a=>a.name===`runic-sdk-${candidate.ciRunId}`);
  assert.equal(a.length,1); assert.equal(a[0].expired,false);
  return a[0];
}
export function finalCI(candidate) {
  const base=`actions/runs/${candidate.ciRunId}`;
  const run=api(base), repository=api('');
  const branch=api(`branches/${encodeURIComponent(repository.default_branch)}`);
  const expectedJobs=expectedCIJobs(YAML.parse(readFileSync(new URL('../../.github/workflows/ci.yml',import.meta.url),'utf8')),webTests());
  const all = (path,key) => { const rows=[]; for(let page=1;;page++) { const response=api(`${path}?per_page=100&page=${page}`); rows.push(...response[key]); if(response[key].length<100) return rows; } };
  return verifyRun(candidate,run,all(`${base}/jobs`,'jobs'),all(`${base}/artifacts`,'artifacts'),{defaultBranch:repository.default_branch,headSha:branch.commit.sha,expectedJobs});
}
