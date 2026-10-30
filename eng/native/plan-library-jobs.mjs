#!/usr/bin/env node
// The library jobs of .github/workflows/native-libraries.yml: one per runtime identifier whose cache entry is missing.
// HITS is "<rid>=<cache-hit> ..." from the lookup steps, TABLE the JSON legs (rid, os, target, zig). Writes `built`, the
// runtime identifiers to build, and `matrix` to GITHUB_OUTPUT (a placeholder leg when none is missing, so the matrix
// stays valid; the job's condition then skips it).
import { appendFileSync } from "node:fs";

const hits = Object.fromEntries(process.env.HITS.trim().split(/\s+/).map(entry => entry.split("=")));
const legs = JSON.parse(process.env.TABLE);
for (const leg of legs) if (!(leg.rid in hits)) throw new Error(`No cache lookup for ${leg.rid}.`);
const missing = legs.filter(leg => hits[leg.rid] !== "true");
const output = line => appendFileSync(process.env.GITHUB_OUTPUT, `${line}\n`);
output(`built=${JSON.stringify(missing.map(leg => leg.rid))}`);
output(`matrix=${JSON.stringify({ include: missing.length ? missing : [{ rid: "none" }] })}`);
console.log(missing.length ? `Building ${missing.map(leg => leg.rid).join(", ")}.` : "Every library is cached.");
