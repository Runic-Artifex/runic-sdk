#!/usr/bin/env node
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";

const authority = process.env.RUNIC_W60_AUTHORITY_MANIFEST && resolve(process.env.RUNIC_W60_AUTHORITY_MANIFEST);
const candidateSet = process.env.RUNIC_W60_EDITOR_CANDIDATE_SET && resolve(process.env.RUNIC_W60_EDITOR_CANDIDATE_SET);
const citations = process.env.RUNIC_W60_PRODUCT_EVIDENCE ? process.env.RUNIC_W60_PRODUCT_EVIDENCE.split(",").filter(Boolean) : [];
const schema = "runic.unsigned-candidate-set-consumer/1";
const repeatSchema = "runic.unsigned-candidate-set-consumer-repeat/1";
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);

const run = (command, args, cwd) => new Promise((done) => {
  const child = spawn(command, args, { cwd, env: { ...process.env }, stdio: ["ignore", "pipe", "pipe"] });
  const output = [];
  child.stdout.on("data", (value) => output.push(value));
  child.stderr.on("data", (value) => output.push(value));
  child.on("error", (error) => done({ exitCode: null, output: String(error) }));
  child.on("close", (exitCode) => done({ exitCode, output: Buffer.concat(output).toString("utf8") }));
});

export function verifyReceipt(receipt, expected) {
  const errors = [];
  if (receipt?.schema !== repeatSchema || !Array.isArray(receipt?.journeys) || receipt.journeys.length !== 2) errors.push("two candidate-set journeys required");
  for (const journey of receipt?.journeys ?? []) {
    if (journey?.schema !== schema || !same(journey?.isolation, { workingDirectory: "temporary-empty" }) || journey?.noProductProjectReference !== true) errors.push("consumer isolation evidence mismatch");
    const linked = journey?.candidateSet;
    if (linked?.schema !== "runic.unsigned-candidate-set/1" || linked?.publication !== "forbidden" || linked?.releaseAuthority?.distribution?.id !== "translations-editor-archive" || !same(linked.releaseAuthority.distribution.version, { state: "unassigned", value: null }) || !Array.isArray(linked?.platforms) || linked.platforms.length !== 3) errors.push("unsigned candidate-set contract mismatch");
    if (expected && !same(linked, expected)) errors.push("candidate-set provenance differs from supplied local inputs");
  }
  if (receipt?.journeys?.length === 2 && !same(receipt.journeys[0], receipt.journeys[1])) errors.push("candidate-set journeys are not deterministic");
  return { ok: errors.length === 0, errors };
}

async function linkedCandidate(cwd) {
  if (!authority || !candidateSet) throw new Error("RUNIC_W60_AUTHORITY_MANIFEST and RUNIC_W60_EDITOR_CANDIDATE_SET are required.");
  const linker = join(dirname(authority), "eng", "link-unsigned-candidate-set.mjs");
  const result = await run("node", [linker, "run-twice", authority, candidateSet, ...citations], cwd);
  if (result.exitCode !== 0) throw new Error(`candidate-set linker failed:\n${result.output.slice(-4096)}`);
  const repeated = JSON.parse(result.output);
  if (repeated?.schema !== "runic.unsigned-candidate-set-repeat/1" || !same(repeated?.journeys?.[0], repeated?.journeys?.[1])) throw new Error("candidate-set linker did not produce deterministic local evidence");
  return repeated.journeys[0];
}

async function one() {
  const directory = await mkdtemp(join(tmpdir(), "runic-w60-unsigned-candidate-set-"));
  try {
    const candidate = await linkedCandidate(directory);
    return { schema, isolation: { workingDirectory: "temporary-empty" }, noProductProjectReference: true, candidateSet: candidate };
  } finally { await rm(directory, { recursive: true, force: true }); }
}

export async function runTwice() {
  const first = await one(), second = await one();
  const receipt = { schema: repeatSchema, journeys: [first, second] };
  const report = verifyReceipt(receipt, first.candidateSet);
  if (!report.ok) throw new Error(report.errors.join("\n"));
  return receipt;
}

if (import.meta.main) {
  const [command, path] = process.argv.slice(2);
  if (command === "run-twice") process.stdout.write(`${JSON.stringify(await runTwice(), null, 2)}\n`);
  else if (command === "verify-twice" && path) {
    const actual = JSON.parse(await readFile(path, "utf8"));
    const expected = await one();
    const report = verifyReceipt(actual, expected.candidateSet);
    if (!report.ok) throw new Error(report.errors.join("\n"));
  } else throw new Error("Usage: verify.mjs run-twice | verify-twice <receipt>");
}
