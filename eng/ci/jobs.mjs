import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { root } from "../run.mjs";

// ci.yml runs the Linux job bodies as `bash eng/ci/jobs/<script> <step>...` (eng/ci/jobs/steps.sh).
// Contract tests that read a step's commands see the script step's commands instead of the call.
const call = /bash eng\/ci\/jobs\/([a-z-]+\.sh)((?: [a-z0-9-]+)*)/g;

export function jobScriptSteps(script, base = root) {
  const text = readFileSync(resolve(base, "eng/ci/jobs", script), "utf8");
  return /^steps=\(([^)]*)\)/m.exec(text)[1].split(/\s+/).filter(Boolean);
}

export function jobScriptStep(script, step, base = root) {
  const lines = readFileSync(resolve(base, "eng/ci/jobs", script), "utf8").split("\n");
  const start = lines.findIndex(line => line.startsWith(`step_${step.replaceAll("-", "_")}() {`));
  if (start < 0) throw new Error(`${script} has no step ${step}`);
  const single = /\{ (.*); \}$/.exec(lines[start]);
  if (single) return single[1];
  const end = lines.indexOf("}", start);
  return lines.slice(start + 1, end).map(line => line.replace(/^  /, "")).join("\n");
}

export const expandJobScripts = (run, base = root) => run.replace(call, (_, script, steps) =>
  (steps.trim() ? steps.trim().split(" ") : jobScriptSteps(script, base)).map(step => jobScriptStep(script, step, base)).join("\n"));

// A copy of a parsed workflow whose job-script calls are replaced by the steps' commands.
export function expandWorkflow(workflow, base = root) {
  const copy = structuredClone(workflow);
  for (const job of Object.values(copy.jobs ?? {}))
    for (const step of job.steps ?? [])
      if (typeof step.run === "string") step.run = expandJobScripts(step.run, base);
  return copy;
}
