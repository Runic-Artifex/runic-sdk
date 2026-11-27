// The Desktop server vectors (specs/desktop/server) as a list of cases: one per vector case, in file and case order. A
// case's set is its file's id, such as `server.http`, and every case has the role `server`.
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { caseHash, sameFormat, type Case } from "../core";

export const VECTOR_FORMAT = "rax.desktop.server-vector/1";
export const LEVELS = ["must", "should", "may"] as const;

// The step kinds of the format (specs/desktop/server/README.md, "Steps"); each step has exactly one of them.
export const STEP_KINDS = [
  "connect",
  "send",
  "halfClose",
  "close",
  "response",
  "frame",
  "closeOrEnd",
  "end",
  "host",
  "hostSend",
  "hostClose",
  "stop",
] as const;

export function loadServerCorpus(root: string): Case[] {
  const cases: Case[] = [];
  for (const file of readdirSync(root).filter((f) => f.endsWith(".json")).sort()) {
    const vector = JSON.parse(readFileSync(join(root, file), "utf8"));
    // The directory also holds cases.json.
    if (!sameFormat(vector.format, VECTOR_FORMAT)) continue;
    if (vector.id !== `server.${file.replace(/\.json$/, "")}`) throw new Error(`${file}: its id must be server.${file.replace(/\.json$/, "")}`);
    for (const c of vector.cases) {
      if (!c.id.startsWith(`${vector.id}.`)) throw new Error(`${file}: case ${c.id} does not start with ${vector.id}.`);
      if (!LEVELS.includes(c.level)) throw new Error(`${file}: case ${c.id} has no level (${LEVELS.join(", ")})`);
      if (typeof c.rule !== "string" || !c.rule) throw new Error(`${file}: case ${c.id} names no rule`);
      for (const [index, step] of c.steps.entries()) {
        const kinds = STEP_KINDS.filter((kind) => kind in step);
        if (kinds.length !== 1) throw new Error(`${file}: case ${c.id} step ${index + 1} has ${kinds.length ? kinds.join(" and ") : "no step kind"}`);
      }
      cases.push({ id: c.id, kind: "vector", set: vector.id, file, roles: ["server"], requires: c.requires ?? [], sha256: caseHash(c), body: c });
    }
  }
  const seen = new Set<string>();
  for (const c of cases) {
    if (seen.has(c.id)) throw new Error(`duplicate case id ${c.id}`);
    seen.add(c.id);
  }
  return cases;
}
