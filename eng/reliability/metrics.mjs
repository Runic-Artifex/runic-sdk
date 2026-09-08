import assert from 'node:assert/strict';
export const quantile = (values, q) => [...values].sort((a,b) => a-b)[Math.ceil(values.length*q)-1];
export function validate(receipt) {
  assert.equal(receipt.schema, 'runic.reliability/1');
  assert.equal(receipt.status, 'passed');
  assert.match(receipt.sourceRevision, /^[a-f0-9]{40}$/);
  assert.ok(receipt.artifacts && Object.keys(receipt.artifacts).length > 0);
  for (const hash of Object.values(receipt.artifacts)) assert.match(hash, /^[a-f0-9]{64}$/);
  assert.ok(receipt.environment?.machine && receipt.environment?.memoryMetric);
  assert.ok(receipt.workload && receipt.profile);
  assert.ok(receipt.samples?.length >= 10, 'At least ten startup samples required');
  for (const s of receipt.samples) {
    for (const key of ['startupMs','peakTreeBytes','durationMs']) assert.ok(Number.isFinite(s[key]) && s[key] > 0, `Invalid ${key}`);
    assert.equal(s.exitCode, 0); assert.equal(s.remainingProcesses, 0);
  }
  return receipt;
}
export function compare(baseline, candidate, provider = false) {
  validate(baseline); validate(candidate);
  assert.deepEqual(candidate.environment, baseline.environment, 'Measurements must use same machine and environment');
  assert.equal(candidate.workload, baseline.workload);
  if (!provider) assert.equal(candidate.profile, baseline.profile);
  const metrics = Object.fromEntries(['startupMs','peakTreeBytes'].map(key => {
    const b = baseline.samples.map(x => x[key]), c = candidate.samples.map(x => x[key]);
    const medianRatio = quantile(c,.5)/quantile(b,.5), upperQuartileRatio = quantile(c,.75)/quantile(b,.75);
    return [key, { medianRatio, upperQuartileRatio, regression: medianRatio > 1.2 && upperQuartileRatio > 1.2 }];
  }));
  return { schema:'runic.reliability-comparison/1', kind:provider?'provider-delta':'equivalent-configuration', baseline:baseline.sourceRevision, candidate:candidate.sourceRevision, metrics,
    passed: provider ? null : !Object.values(metrics).some(x => x.regression) };
}
export function checkCycle(s) {
    for (const key of ['window','reconnect','cancellation']) assert.equal(s.operations?.[key], true, `Missing actual ${key} operation`);
    for (const key of ['leases','transactions','presentations','pendingOperations']) assert.equal(s.resources?.[key],0, `Unreleased ${key}`);
    assert.equal(s.remainingProcesses,0, 'Accumulating processes');
    assert.ok(Number.isFinite(s.treeBytes) && s.treeBytes > 0);
}
export function checkSoak(samples, elapsedMs) {
  assert.ok(elapsedMs >= 7200000, 'Two-hour duration not reached');
  assert.ok(samples.length >= 60, 'Insufficient completed cycles');
  samples.forEach(checkCycle);
  const n=Math.max(10,Math.floor(samples.length/4));
  const first=quantile(samples.slice(0,n).map(x=>x.treeBytes),.5);
  const last=quantile(samples.slice(-n).map(x=>x.treeBytes),.5);
  const quarters = Array.from({length:4},(_,i)=>quantile(samples.slice(Math.floor(i*samples.length/4),Math.floor((i+1)*samples.length/4)).map(x=>x.treeBytes),.5));
  assert.ok(!quarters.slice(1).every((value,i)=>value>quarters[i]), 'Memory medians grow in every quarter');
  assert.ok(last <= first*1.2, 'Memory increase exceeds 20%');
  return { passed:true, cycles:samples.length, elapsedMs, firstMedianBytes:first,lastMedianBytes:last, quarterMediansBytes:quarters };
}
