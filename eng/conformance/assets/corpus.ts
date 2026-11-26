// The Runic Assets archive vectors (specs/assets/vectors) as a list of cases: one per vector case, in file and case
// order. A case's set is its file's id, such as `assets.crafted`, and every case has the role `reader`.
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { caseHash, sameFormat, type Case } from "../core";

export const VECTOR_FORMAT = "runic.assets.vector/1";

export function loadAssetsCorpus(root: string): Case[] {
  const cases: Case[] = [];
  for (const file of readdirSync(root).filter((f) => f.endsWith(".json")).sort()) {
    const vector = JSON.parse(readFileSync(join(root, file), "utf8"));
    // The directory also holds the vector schema and cases.json.
    if (!sameFormat(vector.format, VECTOR_FORMAT)) continue;
    for (const c of vector.cases) {
      if (!c.id.startsWith(`${vector.id}.`)) throw new Error(`${file}: case ${c.id} does not start with ${vector.id}.`);
      cases.push({ id: c.id, kind: "vector", set: vector.id, file, roles: ["reader"], requires: [], sha256: caseHash(c), body: c });
    }
  }
  const seen = new Set<string>();
  for (const c of cases) {
    if (seen.has(c.id)) throw new Error(`duplicate case id ${c.id}`);
    seen.add(c.id);
  }
  return cases;
}
