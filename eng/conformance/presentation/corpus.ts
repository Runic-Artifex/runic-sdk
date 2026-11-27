// The Runic Desktop presentation contract's portable cases (specs/desktop/conformance): its behavioral scenarios and
// codec vectors as one list, scenarios first, each in path order. A case's set is its `area`, its role `host` (the
// presentation host under test), and its requirements a scenario's `requires`. A vector names its wire `profile`.
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { caseHash, sameFormat, type Case } from "../core";

export const SCENARIO_FORMAT = "rax.desktop.conformance-scenario/1";
export const VECTOR_FORMAT = "rax.desktop.codec-vector/1";

export function loadPresentationCorpus(root: string): Case[] {
  const cases: Case[] = [];
  for (const [dir, kind, format] of [["scenarios", "scenario", SCENARIO_FORMAT], ["vectors", "vector", VECTOR_FORMAT]] as const) {
    for (const name of readdirSync(join(root, dir)).filter((n) => n.endsWith(".json")).sort()) {
      const path = join(root, dir, name);
      const file = relative(root, path);
      const body = JSON.parse(readFileSync(path, "utf8"));
      if (!sameFormat(body.format, format)) throw new Error(`${file}: format ${body.format}`);
      const stem = name.replace(/\.json$/, "");
      if (!body.id?.startsWith(`${stem}.v`)) throw new Error(`${file}: its id must be ${stem}.v<n>`);
      cases.push({ id: body.id, kind, set: body.area, file, roles: ["host"], requires: body.requires ?? [], sha256: caseHash(body), body });
    }
  }
  return cases;
}
