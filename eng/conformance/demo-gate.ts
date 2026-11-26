#!/usr/bin/env bun
// Shows the status rules (README.md, "Status files") on a temporary copy of the Views corpus with three edits (R6
// section 1.3):
//   1. an extra member in one expected reply of replies.call-that-changes-nothing.v1: REGRESSION for a producer;
//   2. a new scenario, presentations.demo-new-case.v1: NEW-CASE for every implementation;
//   3. the first value vector of values.uuids removed: STALE-STATUS for every implementation.
// Every run must exit 1. Usage: bun eng/conformance/demo-gate.ts [<implementation>...] (default: all of them).
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { ROOT, corpora, implementations } from "./cli";

const copy = mkdtempSync(join(tmpdir(), "rax-conformance-demo-"));
const corpus = join(copy, "corpus");
let failed = false;
try {
  cpSync(join(ROOT, corpora["views/4"].root), corpus, { recursive: true });
  const read = (path: string) => JSON.parse(readFileSync(join(corpus, path), "utf8"));
  const write = (path: string, value: unknown) => writeFileSync(join(corpus, path), JSON.stringify(value, null, 2));

  const replies = read("scenarios/replies/call-that-changes-nothing.json");
  const reply = replies.steps.find((step: any) => step.replyTo && step.receive?.ok === true);
  reply.receive.demoEdited = true;
  write("scenarios/replies/call-that-changes-nothing.json", replies);

  const added = read("scenarios/presentations/closing-a-reply-acknowledges-the-close.json");
  added.id = "presentations.demo-new-case.v1";
  write("scenarios/presentations/demo-new-case.json", added);

  const uuids = read("vectors/values/uuids.json");
  const removed = uuids.cases.shift().id;
  write("vectors/values/uuids.json", uuids);
  console.log(`edits: an extra member in ${replies.id}, the new scenario ${added.id}, the removed vector ${removed}`);

  const ids = process.argv.slice(2);
  for (const id of ids.length ? ids : Object.keys(implementations())) {
    const result = Bun.spawnSync(["bun", join(import.meta.dir, "cli.ts"), id, "--corpus", corpus, "--receipt", join(copy, `${id}.receipt.json`)], {
      cwd: ROOT,
      stdout: "pipe",
      stderr: "pipe",
    });
    const lines = result.stdout.toString().split("\n").filter((line) => /REGRESSION|NEW-CASE|STALE-STATUS|MISMATCH|status check/.test(line));
    console.log(`=== ${id}\n${lines.join("\n")}\nexit ${result.exitCode}`);
    if (result.exitCode !== 1) failed = true;
  }
} finally {
  rmSync(copy, { recursive: true, force: true });
}
if (failed) {
  console.log("an implementation's run did not exit 1");
  process.exit(1);
}
