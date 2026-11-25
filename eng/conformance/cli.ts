#!/usr/bin/env bun
// The language-neutral conformance driver (eng/conformance/README.md). From the repository root:
//
//   bun run conformance <implementation> [--only <text>] [--set <set>] [--jobs <n>] [--verbose] [--no-build]
//                                        [--receipt <file>] [--write-status] [--corpus <dir>] [--timeout-scale <x>]
//   bun run conformance list                            the implementations of implementations.json
//   bun run conformance cases [--write]                 check, or write, each corpus's cases.json
//   bun run conformance lint                            the status files' own rules and their case lists
//   bun run conformance check <implementation> [--receipt <file>]   compare a receipt with the status file
//
// <implementation> names an entry of implementations.json: its corpus, adapter build and command, and status file. A
// run builds the adapter, runs every case of the corpus through it, prints each failure with its rerun flag, writes a
// receipt to artifacts/conformance and compares the outcomes with the status file. It exits 1 on a blocking finding.
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { Adapter, AdapterError } from "./core/adapter";
import { names } from "./core/names";
import { compare, formatStatus, lintStatus, statusFromReceipt, type Outcome, type Receipt, type StatusFile } from "./core/results";
import { formatCases, loadViewsCorpus, type Case } from "./views/corpus";
import { runScenario } from "./views/scenarios";
import { runVector } from "./views/vectors";

export const ROOT = resolve(import.meta.dir, "../..");
const ARTIFACTS = join(ROOT, "artifacts/conformance");

type CorpusModule = { root: string; load: (root: string) => Case[] };
// The corpus modules: one per corpus format the driver runs. A corpus's directory holds its cases.json and status/.
export const corpora: Record<string, CorpusModule> = {
  "views/4": { root: "specs/application/conformance", load: loadViewsCorpus },
};

type Implementation = {
  id: string;
  corpus: string;
  roles: string[];
  build?: string[];
  command: string[];
  env?: Record<string, string>;
  status: string;
};

const expand = (text: string) =>
  text.replace(/\$\{(\w+)\}/g, (_, name) => (name === "ROOT" ? ROOT : name === "CONFIGURATION" ? (process.env.CONFIGURATION ?? "Debug") : (process.env[name] ?? "")));

export function implementations(): Record<string, Implementation> {
  const raw = JSON.parse(readFileSync(join(import.meta.dir, "implementations.json"), "utf8"));
  const out: Record<string, Implementation> = {};
  for (const [id, entry] of Object.entries<any>(raw.implementations))
    out[id] = { id, ...entry, build: entry.build?.map(expand), command: entry.command.map(expand) };
  return out;
}

const args = process.argv.slice(2);
const flag = (name: string) => {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
};
const has = (name: string) => args.includes(name);
const tty = process.stdout.isTTY;
const color = (code: number) => (text: string) => (tty ? `\x1b[${code}m${text}\x1b[0m` : text);
const red = color(31), green = color(32), yellow = color(33), dim = color(2);

function corpusRoot(impl: Implementation) {
  return resolve(flag("--corpus") ?? join(ROOT, corpora[impl.corpus].root));
}

// A deviation names a row of a difference table as <file>#<anchor>, the file relative to the repository root.
export function anchorExists(reference: string): boolean {
  const [file, anchor] = reference.split("#");
  const path = join(ROOT, file);
  return existsSync(path) && readFileSync(path, "utf8").includes(`id="${anchor}"`);
}

function build(impl: Implementation) {
  if (!impl.build || has("--no-build")) return;
  console.log(dim(`building the ${impl.id} adapter: ${impl.build.join(" ")}`));
  const result = Bun.spawnSync(impl.build, { cwd: ROOT, env: { ...process.env, ...impl.env }, stdout: "pipe", stderr: "pipe" });
  if (result.exitCode !== 0) {
    process.stdout.write(result.stdout.toString() + result.stderr.toString());
    throw new Error(`the ${impl.id} adapter did not build (exit ${result.exitCode})`);
  }
}

async function run(impl: Implementation): Promise<number> {
  const module = corpora[impl.corpus];
  if (!module) throw new Error(`unknown corpus ${impl.corpus}`);
  const root = corpusRoot(impl);
  const corpus = module.load(root);
  let cases = corpus;
  const only = flag("--only"), set = flag("--set");
  if (only) cases = cases.filter((c) => c.id.includes(only));
  if (set) cases = cases.filter((c) => c.set === set || c.set === `scenarios.${set}`);
  build(impl);
  const scale = Number(flag("--timeout-scale") ?? process.env.RAX_TEST_TIMEOUT_SCALE ?? 1);
  const timing = { receiveMs: Math.round(5000 * scale) };
  const jobs = Math.max(1, Number(flag("--jobs") ?? 1));
  const verbose = has("--verbose");
  mkdirSync(ARTIFACTS, { recursive: true });
  const logFile = join(ARTIFACTS, `${impl.id}.adapter.log`);
  writeFileSync(logFile, "");
  const log = (line: string) => appendFileSync(logFile, line + "\n");
  const spec = { command: impl.command, cwd: ROOT, env: impl.env };

  const started = Date.now();
  const results: Record<string, Outcome> = {};
  let describe: any;
  const queue = [...cases];
  let done = 0;
  const worker = async () => {
    let adapter = new Adapter(spec, log);
    describe = await adapter.start();
    const supports = new Set<string>([...(describe.features ?? []), ...(describe.capabilities ?? [])]);
    const roles = new Set<string>(impl.roles);
    for (let c = queue.shift(); c; c = queue.shift()) {
      const t0 = performance.now();
      let outcome: Outcome;
      adapter.clearTail();
      try {
        outcome = c.kind === "vector" ? await runVector(adapter, c.body, roles) : await runScenario(adapter, c.body, supports, timing, roles);
      } catch (e) {
        const message = e instanceof Error ? e.message : String(e);
        outcome = { outcome: "fail", detail: `harness: ${message}${e instanceof AdapterError ? "\n" + adapter.tail() : ""}` };
        if (!adapter.alive) {
          adapter = new Adapter(spec, log);
          await adapter.start();
        }
      }
      outcome.ms = Math.round(performance.now() - t0);
      results[c.id] = outcome;
      done++;
      if (verbose || outcome.outcome === "fail") printOutcome(c, outcome);
      else if (tty) process.stdout.write(`\r${dim(`${done}/${cases.length}`)}`);
    }
    await adapter.stop();
  };
  await Promise.all(Array.from({ length: jobs }, worker));
  if (tty) process.stdout.write("\r");

  const receipt: Receipt = {
    format: names.receipt,
    corpus: { id: impl.corpus, root: relative(ROOT, root) || ".", commit: gitHead() },
    implementation: { id: impl.id, name: describe?.implementation?.name, version: describe?.implementation?.version },
    adapter: { protocol: describe?.protocol, command: impl.command.map((part) => part.replace(ROOT, ".")) },
    run: { started: new Date(started).toISOString(), durationMs: Date.now() - started, filter: only ?? set },
    results: Object.fromEntries(cases.map((c) => [c.id, results[c.id]])),
  };
  const receiptPath = flag("--receipt") ?? join(ARTIFACTS, `${impl.id}.receipt.json`);
  writeFileSync(receiptPath, JSON.stringify(receipt, null, 1) + "\n");
  summary(cases, results);
  console.log(dim(`receipt: ${relative(ROOT, receiptPath)}   adapter log: ${relative(ROOT, logFile)}   ${((Date.now() - started) / 1000).toFixed(1)} s`));

  const statusPath = join(root, impl.status);
  if (has("--write-status")) {
    const previous: StatusFile | undefined = existsSync(statusPath) ? JSON.parse(readFileSync(statusPath, "utf8")) : undefined;
    const status = statusFromReceipt(receipt, impl.roles, previous?.implementation.maturity ?? "development", previous);
    if (only || set) for (const [id, entry] of Object.entries(previous?.cases ?? {})) status.cases[id] ??= entry;
    status.cases = Object.fromEntries(corpus.filter((c) => status.cases[c.id]).map((c) => [c.id, status.cases[c.id]]));
    mkdirSync(dirname(statusPath), { recursive: true });
    writeFileSync(statusPath, formatStatus(status));
    console.log(`wrote ${relative(ROOT, statusPath)}`);
    return 0;
  }
  const staleCases = casesProblem(impl.corpus, root, corpus);
  if (staleCases) console.log(yellow(staleCases));
  return check(impl, statusPath, receipt, corpus.map((c) => c.id), Boolean(only || set)) || (staleCases ? 1 : 0);
}

function check(impl: Implementation, statusPath: string, receipt: Receipt, ids: string[], filtered: boolean): number {
  if (!existsSync(statusPath)) {
    console.log(yellow(`no status file ${relative(ROOT, statusPath)}: run with --write-status to start one`));
    return 1;
  }
  const status: StatusFile = JSON.parse(readFileSync(statusPath, "utf8"));
  const lint = lintStatus(status, anchorExists);
  const findings = compare(receipt, status, ids, filtered);
  for (const problem of lint) console.log(yellow(`status: ${problem}`));
  const byKind = new Map<string, number>();
  for (const f of findings) byKind.set(f.kind, (byKind.get(f.kind) ?? 0) + 1);
  for (const f of findings) {
    const tag = f.kind === "regression" ? red("REGRESSION") : f.kind === "fixed" ? green("FIXED") : yellow(f.kind.toUpperCase());
    console.log(`${tag} ${f.id}: ${f.text.split("\n")[0]}`);
  }
  // FIXED blocks only in CI (the ratchet); locally it asks for the status to be updated. Lint problems of a
  // development implementation, such as TODO issues, do not block its local runs.
  const blocking = findings.filter((f) => f.kind !== "fixed" || process.env.CI).length + (status.implementation.maturity !== "development" ? lint.length : 0);
  const label = relative(ROOT, statusPath);
  console.log(
    blocking
      ? red(`status check: ${[...byKind].map(([k, n]) => `${n} ${k}`).concat(lint.length ? [`${lint.length} status-file problems`] : []).join(", ")}`)
      : green(`status check: the run matches ${label}${byKind.get("fixed") ? ` (${byKind.get("fixed")} fixed: update the status)` : ""}`),
  );
  return blocking ? 1 : 0;
}

function casesPath(root: string) {
  return join(root, "cases.json");
}

// A problem with the corpus's committed case list, or null when it is current.
function casesProblem(corpus: string, root: string, cases: Case[]): string | null {
  const path = casesPath(root);
  if (!existsSync(path)) return `${relative(ROOT, path)} is missing: run bun run conformance cases --write`;
  return readFileSync(path, "utf8") === formatCases(names.cases, corpus, cases)
    ? null
    : `${relative(ROOT, path)} is out of date: run bun run conformance cases --write`;
}

function cases(): number {
  let stale = 0;
  for (const [id, module] of Object.entries(corpora)) {
    const root = resolve(flag("--corpus") ?? join(ROOT, module.root));
    const list = module.load(root);
    if (has("--write")) {
      writeFileSync(casesPath(root), formatCases(names.cases, id, list));
      console.log(`wrote ${relative(ROOT, casesPath(root))} (${list.length} cases)`);
      continue;
    }
    const problem = casesProblem(id, root, list);
    console.log(problem ? yellow(problem) : green(`${relative(ROOT, casesPath(root))} is current (${list.length} cases)`));
    if (problem) stale++;
  }
  return stale ? 1 : 0;
}

// Every status file of every implementation, without running anything: its own rules, and a status for exactly the
// cases of its corpus.
function lint(): number {
  let problems = 0;
  for (const impl of Object.values(implementations())) {
    const root = corpusRoot(impl);
    const path = join(root, impl.status);
    const label = relative(ROOT, path);
    if (!existsSync(path)) {
      console.log(yellow(`${label}: missing`));
      problems++;
      continue;
    }
    const status: StatusFile = JSON.parse(readFileSync(path, "utf8"));
    const ids = corpora[impl.corpus].load(root).map((c) => c.id);
    const found = [
      ...lintStatus(status, anchorExists),
      ...ids.filter((id) => !status.cases[id]).map((id) => `${id}: no status for this case`),
      ...Object.keys(status.cases).filter((id) => !ids.includes(id)).map((id) => `${id}: the corpus has no such case`),
    ];
    if (status.implementation.id !== impl.id) found.push(`implementation.id is ${status.implementation.id}, not ${impl.id}`);
    for (const problem of found) console.log(yellow(`${label}: ${problem}`));
    if (!found.length) console.log(green(`${label}: ${ids.length} cases, no problems`));
    problems += found.length;
  }
  return problems ? 1 : 0;
}

function printOutcome(c: Case, o: Outcome) {
  const mark = o.outcome === "pass" ? green("pass") : o.outcome === "fail" ? red("FAIL") : o.outcome === "unsupported" ? yellow("unsupported") : dim("excluded");
  console.log(`${mark} ${c.id} ${dim(`${o.ms} ms`)}`);
  if (o.outcome === "fail" && o.detail) {
    console.log(o.detail.split("\n").map((line) => "    " + line).join("\n"));
    console.log(dim(`    file: ${c.file}   rerun: --only ${c.id}`));
  }
}

function summary(cases: Case[], results: Record<string, Outcome>) {
  const rows = new Map<string, Record<string, number>>();
  for (const c of cases) {
    const row = rows.get(c.set) ?? rows.set(c.set, { pass: 0, fail: 0, unsupported: 0, excluded: 0 }).get(c.set)!;
    row[results[c.id]?.outcome ?? "fail"]++;
  }
  const total = { pass: 0, fail: 0, unsupported: 0, excluded: 0 };
  const cells = (r: Record<string, number>) =>
    `${String(r.pass).padStart(5)} ${(r.fail ? red : String)(String(r.fail).padStart(5))} ${String(r.unsupported).padStart(5)} ${String(r.excluded).padStart(5)}`;
  console.log(`${"set".padEnd(40)} ${"pass".padStart(5)} ${"fail".padStart(5)} ${"unsup".padStart(5)} ${"excl".padStart(5)}`);
  for (const [set, row] of rows) {
    for (const k of Object.keys(total) as (keyof typeof total)[]) total[k] += row[k];
    console.log(`${set.padEnd(40)} ${cells(row)}`);
  }
  console.log(`${"total".padEnd(40)} ${cells(total)}`);
}

function gitHead(): string | undefined {
  const result = Bun.spawnSync(["git", "-C", ROOT, "rev-parse", "HEAD"]);
  return result.exitCode === 0 ? result.stdout.toString().trim() : undefined;
}

const usage = `usage: bun run conformance <implementation> [--only <text>] [--set <set>] [--jobs <n>] [--verbose] [--no-build]
                                          [--receipt <file>] [--write-status] [--corpus <dir>] [--timeout-scale <x>]
       bun run conformance list | cases [--write] | lint | check <implementation> [--receipt <file>]`;

async function main(): Promise<number> {
  const [command, target] = args;
  const all = implementations();
  switch (command) {
    case undefined:
    case "--help":
    case "-h":
      console.log(usage);
      return command ? 0 : 2;
    case "list":
      for (const i of Object.values(all)) console.log(`${i.id.padEnd(16)} ${i.corpus.padEnd(10)} ${i.roles.join(",").padEnd(10)} ${i.command.join(" ").replace(ROOT, ".")}`);
      return 0;
    case "cases":
      return cases();
    case "lint":
      return lint();
    case "check": {
      const impl = all[target];
      if (!impl) throw new Error(`unknown implementation ${target}; see bun run conformance list`);
      const receipt: Receipt = JSON.parse(readFileSync(flag("--receipt") ?? join(ARTIFACTS, `${impl.id}.receipt.json`), "utf8"));
      const root = corpusRoot(impl);
      return check(impl, join(root, impl.status), receipt, corpora[impl.corpus].load(root).map((c) => c.id), Boolean(receipt.run.filter));
    }
    default: {
      const id = command === "run" ? target : command;
      const impl = all[id];
      if (!impl) {
        console.log(`unknown implementation ${id}\n${usage}`);
        return 2;
      }
      return run(impl);
    }
  }
}

if (import.meta.main) process.exit(await main());
