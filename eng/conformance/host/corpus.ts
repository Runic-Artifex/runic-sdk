// The Runic host protocol scenarios (specs/desktop/host-conformance) as a list of cases: one per scenario file, in path
// order. A case's set is its directory, such as `handshake`, its roles are the scenario's `roles` (`guest` or `host`)
// and its requirements the scenario's `requires`, the runic-host/1 features it needs.
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { caseHash, sameFormat, type Case } from "../core";

export const SCENARIO_FORMAT = "rax.host.scenario/1";

function files(dir: string): string[] {
  return readdirSync(dir)
    .sort()
    .flatMap((name) => {
      const path = join(dir, name);
      return statSync(path).isDirectory() ? files(path) : name.endsWith(".json") ? [path] : [];
    });
}

export function loadHostCorpus(root: string): Case[] {
  const scenarios = join(root, "scenarios");
  const cases: Case[] = [];
  for (const path of files(scenarios)) {
    const scenario = JSON.parse(readFileSync(path, "utf8"));
    const file = relative(root, path);
    if (!sameFormat(scenario.format, SCENARIO_FORMAT)) throw new Error(`${file}: format ${scenario.format}`);
    const [set, name] = relative(scenarios, path).replace(/\.json$/, "").split("/");
    if (!scenario.id?.startsWith(`${set}.${name}.v`)) throw new Error(`${file}: its id must be ${set}.${name}.v<n>`);
    cases.push({ id: scenario.id, kind: "scenario", set, file, roles: scenario.roles, requires: scenario.requires ?? [], sha256: caseHash(scenario), body: scenario });
  }
  return cases;
}
