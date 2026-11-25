// The Views protocol 4 corpus (specs/application/conformance) as a list of cases: one per vector case and per
// scenario, in a stable order. Vectors have no roles in the corpus yet (R6 F2), so the driver gives read-request to
// the producer, read-push and read-reply to the client, and canonicalize and decode to both.
import { createHash } from "node:crypto";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

export type Case = { id: string; kind: "vector" | "scenario"; set: string; file: string; roles: string[]; requires: string[]; sha256: string; body: any };

const jsonFiles = (dir: string) => readdirSync(dir).filter((f) => f.endsWith(".json")).sort();

export function loadViewsCorpus(root: string): Case[] {
  const cases: Case[] = [];
  for (const area of ["canonical", "values", "messages"]) {
    const dir = join(root, "vectors", area);
    for (const f of jsonFiles(dir)) {
      const file = join("vectors", area, f);
      const vector = JSON.parse(readFileSync(join(root, file), "utf8"));
      for (const c of vector.cases) {
        const role = c.operation === "read-push" || c.operation === "read-reply" ? ["client"] : c.operation === "read-request" ? ["producer"] : ["producer", "client"];
        cases.push({ id: c.id, kind: "vector", set: vector.id, file, roles: role, requires: [], sha256: hash(c), body: c });
      }
    }
  }
  for (const set of readdirSync(join(root, "scenarios")).sort()) {
    for (const f of jsonFiles(join(root, "scenarios", set))) {
      const file = join("scenarios", set, f);
      const text = readFileSync(join(root, file), "utf8");
      const s = JSON.parse(text);
      cases.push({ id: s.id, kind: "scenario", set: `scenarios.${set}`, file, roles: s.roles ?? [], requires: s.requires ?? [], sha256: hash(s), body: s });
    }
  }
  const seen = new Set<string>();
  for (const c of cases) {
    if (seen.has(c.id)) throw new Error(`duplicate case id ${c.id}`);
    seen.add(c.id);
  }
  return cases;
}

// The hash of a case's content, independent of formatting, so a semantic edit shows in review (R2 section 3).
function hash(value: unknown): string {
  const canonical = (v: any): string =>
    Array.isArray(v) ? `[${v.map(canonical).join(",")}]` : v && typeof v === "object" ? `{${Object.keys(v).sort().map((k) => JSON.stringify(k) + ":" + canonical(v[k])).join(",")}}` : JSON.stringify(v);
  return createHash("sha256").update(canonical(value)).digest("hex");
}

// The committed case list (cases.json): one case per line, without its body, so a semantic edit to a case shows as a
// changed sha256 in review (R6 section 4, rule 3).
export function formatCases(format: string, corpus: string, cases: Case[]): string {
  const lines = cases.map(({ body, ...c }, i) => `    ${JSON.stringify(c)}${i < cases.length - 1 ? "," : ""}`);
  return ["{", `  "format": ${JSON.stringify(format)},`, `  "corpus": ${JSON.stringify(corpus)},`, `  "cases": [`, ...lines, "  ]", "}", ""].join("\n");
}
