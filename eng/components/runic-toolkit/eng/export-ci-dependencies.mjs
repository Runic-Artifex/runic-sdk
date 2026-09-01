#!/usr/bin/env node

import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const dependencySet = JSON.parse(
  readFileSync(resolve(import.meta.dirname, "runic.ci-dependencies.json"), "utf8"),
);
const versionPattern = /^1\.0\.0-ci\.sha[0-9a-f]{16}$/u;

if (dependencySet.schemaVersion !== 1 || !Array.isArray(dependencySet.packages)) {
  throw new Error("The CI dependency set must use schema version 1.");
}

const environments = new Set();
for (const dependency of dependencySet.packages) {
  if (!versionPattern.test(dependency.version)) {
    throw new Error(`Invalid immutable candidate version for ${dependency.identity}.`);
  }
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/u.test(dependency.environment) || environments.has(dependency.environment)) {
    throw new Error(`Invalid or duplicate environment name for ${dependency.identity}.`);
  }
  environments.add(dependency.environment);
  process.stdout.write(`${dependency.environment}=${dependency.version}\n`);
}
