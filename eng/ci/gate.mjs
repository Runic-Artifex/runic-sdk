import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

// The required `verify` check. Every job must succeed, except that a job the
// plan skipped may report skipped. A failed or cancelled plan has no skip list,
// so every skipped job then fails the gate.
export function incomplete(needs, skip = []) {
  return Object.entries(needs)
    .filter(([job, { result }]) => result !== "success" && !(result === "skipped" && skip.includes(job)))
    .map(([job, { result }]) => `${job}=${result}`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const skip = JSON.parse(process.env.SKIP || "[]");
  const failed = incomplete(JSON.parse(process.env.NEEDS), skip);
  if (failed.length) {
    console.error(`Verification is incomplete: ${failed.join(", ")}`);
    process.exit(1);
  }
  console.log(`Planned jobs passed; planned skips: ${skip.join(", ") || "none"}`);
}
