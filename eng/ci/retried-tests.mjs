// The recorded single retry's report (eng/ci/README.md, "Recorded retries"; B1 of D077.25). A [RecordedRetry] test that
// failed appends one JSON line to the file RUNIC_RETRIED_TESTS names (tests/support/RecordedRetry.cs) before its retry.
// At the end of the job this prints a "Flaky test retried" warning for each line and lists them in the job summary;
// .github/actions/flaky-tests then uploads the file, and eng/ci/flakes.mjs counts each line as a sighting.
//
//   node eng/ci/retried-tests.mjs report <file>
import { appendFileSync, existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

// The records of a file's text; a line that is not a record (a torn write) is skipped, not fatal.
export function parseRetries(text) {
  return text.split("\n").filter(line => line.trim()).flatMap(line => {
    try {
      const record = JSON.parse(line);
      return typeof record?.test === "string" && typeof record?.exception === "string" ? [record] : [];
    } catch {
      return [];
    }
  });
}

// A workflow command's data and property values escape %, CR and LF; properties also : and ,.
const data = text => String(text).replace(/%/g, "%25").replace(/\r/g, "%0D").replace(/\n/g, "%0A");

export function warning(record) {
  return `::warning title=Flaky test retried::${data(`${record.test} (${record.assembly ?? "?"}, ${record.leg ?? "?"}) failed`
    + ` and was retried once (${record.reason ?? "?"}): ${record.exception.replace(/\.$/, "")}. A retry counts as a flake sighting (eng/ci/flakes.mjs).`)}`;
}

export function summary(records) {
  const cell = text => String(text ?? "").replace(/\|/g, "\\|").replace(/\n/g, " ");
  return ["### Flaky tests retried", "", "Each failed once and was retried once; `eng/ci/flakes.mjs` counts it as a flake sighting.", "",
    "| Test | Assembly | Leg | Reason | First failure |", "| --- | --- | --- | --- | --- |",
    ...records.map(record => `| ${cell(record.test)} | ${cell(record.assembly)} | ${cell(record.leg)} | ${cell(record.reason)} | ${cell(record.exception)} |`),
    ""].join("\n");
}

// Reports the file's retries; returns them.
export function report(file, { env = process.env, log = console.log } = {}) {
  const records = file && existsSync(file) ? parseRetries(readFileSync(file, "utf8")) : [];
  for (const record of records) log(warning(record));
  if (records.length && env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, summary(records) + "\n");
  if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `retried=${records.length > 0}\n`);
  if (!records.length) log("No test was retried.");
  return records;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [command, file] = process.argv.slice(2);
  if (command !== "report") throw new Error("usage: node eng/ci/retried-tests.mjs report <file>");
  report(file);
}
