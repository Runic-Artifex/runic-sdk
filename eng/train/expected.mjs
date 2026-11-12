// The allow-list of known incompatibilities (expected-failures.json). An entry applies while its package is below
// the named version in this run's combination, so it can never excuse a failure of a fixed release.
import { readFileSync } from 'node:fs';
import { compareVersions } from './versions.mjs';

export function loadExpected(path = new URL('./expected-failures.json', import.meta.url)) {
  const { entries } = JSON.parse(readFileSync(path, 'utf8'));
  for (const entry of entries) {
    if (!/^I\d+$/.test(entry.id) || !entry.title || !entry.remove || !entry.applies?.package || !entry.applies?.below || !entry.failures?.length)
      throw new Error(`expected-failures.json: entry ${entry.id ?? '?'} needs id, title, remove, applies.{package,below} and failures`);
    for (const failure of entry.failures) {
      if (!/^S\d$/.test(failure.scenario) || !failure.match) throw new Error(`expected-failures.json ${entry.id}: each failure needs scenario and match`);
      failure.pattern = new RegExp(failure.match);
    }
  }
  return entries;
}

// The entries that apply to a combination; `versionOf(id)` is the version of a package in it, or null.
export function activeEntries(entries, versionOf) {
  return entries.filter(entry => {
    const version = versionOf(entry.applies.package);
    return version !== null && version !== undefined && compareVersions(version, entry.applies.below) < 0;
  });
}

export function matchExpected(active, failure) {
  return active.find(entry => entry.failures.some(rule => rule.scenario === failure.scenario
    && (!rule.check || rule.check === failure.check) && rule.pattern.test(failure.message))) ?? null;
}

// Splits failures into unexpected and expected ones, and names the active entries that matched nothing: an entry
// that no longer reproduces should be removed.
export function classify(failures, active) {
  const matched = new Set();
  const classified = failures.map(failure => {
    const entry = matchExpected(active, failure);
    if (entry) matched.add(entry.id);
    return { ...failure, expected: entry?.id ?? null };
  });
  return { failures: classified, stale: active.filter(entry => !matched.has(entry.id)).map(entry => entry.id) };
}
