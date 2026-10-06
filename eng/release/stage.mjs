import { existsSync, mkdirSync, mkdtempSync, readdirSync, renameSync, rmSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';

// A candidate is built in a sibling directory on the same filesystem and
// renamed over the target only after every package exists. An interrupted run
// leaves the previous target untouched; the next run removes its leftovers.
const stagingPrefix = name => `.${name}-staging-`;
const previousPrefix = name => `.${name}-previous-`;

export function recoverStaging(target) {
  const parent = dirname(target), name = basename(target);
  if (!existsSync(parent)) return;
  for (const entry of readdirSync(parent)) {
    const path = join(parent, entry);
    if (entry.startsWith(stagingPrefix(name))) rmSync(path, { recursive: true, force: true });
    // Only possible when a promotion stopped between its two renames.
    else if (entry.startsWith(previousPrefix(name))) {
      if (existsSync(target)) rmSync(path, { recursive: true, force: true });
      else renameSync(path, target);
    }
  }
}

export function createStaging(target) {
  recoverStaging(target);
  mkdirSync(dirname(target), { recursive: true });
  return mkdtempSync(join(dirname(target), stagingPrefix(basename(target))));
}

export function promoteStaging(staging, target) {
  const previous = join(dirname(target), `${previousPrefix(basename(target))}${process.pid}`);
  const replacing = existsSync(target);
  if (replacing) renameSync(target, previous);
  try {
    renameSync(staging, target);
  } catch (error) {
    if (replacing) renameSync(previous, target);
    throw error;
  }
  if (replacing) rmSync(previous, { recursive: true, force: true });
}

// Runs build(staging) and promotes its output, or removes it on failure.
export function stageAndPromote(target, build) {
  const staging = createStaging(target);
  try {
    build(staging);
    promoteStaging(staging, target);
  } finally {
    rmSync(staging, { recursive: true, force: true });
  }
}
