// Unit tests of the conformance driver (eng/conformance): the status comparison and lint, message writing and
// matching, the generators, the corpus loader and the adapter client. They run no implementation; running a corpus
// against one is `bun run conformance <implementation>`, and its CI gate is a later slice (R6 section 4).
import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { root } from "./run.mjs";
import { Adapter, AdapterError } from "./conformance/core/adapter.ts";
import { names, sameFormat } from "./conformance/core/names.ts";
import { compare, formatStatus, lintStatus, statusFromReceipt } from "./conformance/core/results.ts";
import { createDriver } from "./conformance/core/driver.ts";
import { formatCases as coreFormatCases } from "./conformance/core/cases.ts";
import { formatCases, loadViewsCorpus } from "./conformance/views/corpus.ts";
import { Messages, ScenarioFailure, expand, valueEqual } from "./conformance/views/messages.ts";
import { anchorExists } from "./conformance/cli.ts";

const receipt = (results) => ({
  format: names.receipt,
  corpus: { id: "views/4", root: "." },
  implementation: { id: "demo" },
  adapter: { protocol: names.adapterProtocol, command: [] },
  run: { started: "", durationMs: 0 },
  results: Object.fromEntries(Object.entries(results).map(([id, outcome]) => [id, typeof outcome === "string" ? { outcome } : outcome])),
});
const status = (cases, maturity = "stable") => ({
  format: names.status,
  corpus: { id: "views/4" },
  implementation: { id: "demo", roles: ["producer"], maturity },
  cases,
});
const kinds = (findings) => findings.map((f) => `${f.kind} ${f.id}`).sort();

test("compare reports regressions, new cases, stale statuses, mismatches and fixed cases", () => {
  const findings = compare(
    receipt({ a: "fail", b: "pass", c: "pass", d: "excluded", e: "pass", f: "fail", g: "unsupported" }),
    status({
      a: { status: "pass" },
      c: { status: "known-failure", issue: "#1" },
      d: { status: "unsupported", requires: ["x/1"] },
      e: { status: "unsupported", requires: ["x/1"] },
      f: { status: "excluded", reason: "role:client" },
      g: { status: "known-failure", issue: "#1" },
      gone: { status: "pass" },
    }),
    ["a", "b", "c", "d", "e", "f", "g"],
  );
  assert.deepEqual(kinds(findings), [
    "fixed c", "fixed e", "mismatch d", "mismatch f", "mismatch g", "new-case b", "regression a", "stale-status gone",
  ]);
});

test("a deviation may pass or fail, but nothing else", () => {
  const entry = { status: "deviation", deviation: "specs/application/language-mappings.md#date-time-offset" };
  assert.deepEqual(compare(receipt({ a: "pass", b: "fail", c: "unsupported" }), status({ a: entry, b: entry, c: entry }), ["a", "b", "c"]).map((f) => `${f.kind} ${f.id}`), ["mismatch c"]);
});

test("a filtered run checks only the cases it ran", () => {
  const findings = compare(receipt({ a: "pass" }), status({ a: { status: "pass" }, b: { status: "pass" }, gone: { status: "pass" } }), ["a", "b", "c"], true);
  assert.deepEqual(findings, []);
});

test("the lint enforces the status rules of R7 T1-13", () => {
  const problems = lintStatus(
    status({
      a: { status: "known-failure", issue: "#12" },
      b: { status: "known-failure" },
      c: { status: "unsupported" },
      d: { status: "excluded" },
      e: { status: "deviation" },
      f: { status: "deviation", deviation: "specs/application/language-mappings.md#no-such-row" },
      g: { status: "unsupported", issue: "TODO" },
      h: { status: "unsupported", issue: "Runic-Artifex/runic-sdk#1350" },
      i: { status: "deviation", deviation: "specs/application/language-mappings.md#date-time-offset" },
    }),
    anchorExists,
  );
  assert.deepEqual(problems.map((p) => p.split(":")[0]).sort(), ["a", "b", "b", "c", "d", "e", "f", "g"]);
  // A development implementation may carry known failures and TODO issues while it is on a local branch (D075.16).
  assert.deepEqual(lintStatus(status({ a: { status: "known-failure", issue: "TODO" } }, "development")), []);
  assert.match(lintStatus(status({}, "beta")).join(), /maturity/);
});

test("--write-status keeps deviations and known failures that still fail", () => {
  const previous = status({
    a: { status: "deviation", deviation: "x.md#a" },
    b: { status: "known-failure", issue: "#5" },
    c: { status: "known-failure", issue: "#5" },
  });
  const written = statusFromReceipt(receipt({ a: "fail", b: "fail", c: "pass", d: "fail", e: { outcome: "unsupported", requires: ["x/1"] } }), ["producer"], "preview", previous);
  assert.deepEqual(written.cases, {
    a: previous.cases.a,
    b: previous.cases.b,
    c: { status: "pass" },
    d: { status: "known-failure", issue: "TODO", reason: "" },
    e: { status: "unsupported", requires: ["x/1"] },
  });
  assert.deepEqual(JSON.parse(formatStatus(written)), written);
});

test("format names accept both spellings (D079.27)", () => {
  assert.ok(sameFormat("runic.conformance-status/1", "rax.conformance-status/1"));
  assert.ok(!sameFormat("rax.conformance-receipt/1", "rax.conformance-status/1"));
});

test("message matching maps revisions in order and epochs one to one", () => {
  const messages = new Messages({}, async () => null);
  messages.match({ kind: "frame", revision: 1, epoch: 1 }, { kind: "frame", revision: 7, epoch: 40 }, "notes");
  messages.match({ kind: "frame", revision: 2, epoch: 1 }, { kind: "frame", revision: 9, epoch: 40 }, "notes");
  assert.throws(() => messages.match({ revision: 3 }, { revision: 8 }, "notes"), ScenarioFailure);
  assert.throws(() => messages.match({ revision: 1 }, { revision: 8 }, "notes"), ScenarioFailure);
  assert.throws(() => messages.match({ epoch: 2 }, { epoch: 40 }, "notes"), ScenarioFailure);
  // Epochs are per route.
  messages.match({ epoch: 1 }, { epoch: 3 }, "other");
});

test("in an unordered group, revisions first seen on different routes may map in either order", () => {
  const messages = new Messages({}, async () => null);
  messages.match({ revision: 0 }, { revision: 0 }, "notes");
  messages.beginGroup();
  // The producer published the second route's frame first.
  messages.match({ kind: "frame", route: "detail", revision: 2 }, { kind: "frame", route: "detail", revision: 1 }, null);
  messages.match({ kind: "frame", route: "notes", revision: 1 }, { kind: "frame", route: "notes", revision: 2 }, null);
  messages.endGroup();
  messages.match({ revision: 3 }, { revision: 5 }, "notes");
  assert.throws(() => messages.match({ revision: 4 }, { revision: 2 }, "notes"), ScenarioFailure);
  // They still differ, and the same route stays ordered.
  const shared = new Messages({}, async () => null);
  shared.beginGroup();
  shared.match({ kind: "frame", route: "a", revision: 1 }, { kind: "frame", route: "a", revision: 4 }, null);
  assert.throws(() => shared.match({ kind: "frame", route: "b", revision: 2 }, { kind: "frame", route: "b", revision: 4 }, null), ScenarioFailure);
  assert.throws(() => shared.match({ kind: "frame", route: "a", revision: 2 }, { kind: "frame", route: "a", revision: 3 }, null), ScenarioFailure);
});

test("message matching compares members exactly, $any by type, and frame changes per field", () => {
  const messages = new Messages({}, async () => null);
  assert.equal(messages.tryMatch({ a: { $any: "string", default: "x" } }, { a: "y" }, null), null);
  assert.match(messages.tryMatch({ a: { $any: "integer" } }, { a: 1.5 }, null), /expected/);
  assert.match(messages.tryMatch({ a: 1 }, { a: 1, b: 2 }, null), /unexpected \[b\]/);
  const frame = (changes) => ({ kind: "frame", changes });
  assert.equal(messages.tryMatch(frame([{ field: "a", v: 1 }, { field: "b", v: 2 }]), frame([{ field: "b", v: 2 }, { field: "a", v: 1 }]), null), null);
  assert.match(messages.tryMatch(frame([{ field: "a", v: 1 }, { field: "a", v: 2 }]), frame([{ field: "a", v: 2 }, { field: "a", v: 1 }]), null), /expected/);
});

test("ids: wire ids are written as declared, provided ids come from the adapter once", async () => {
  let asked = 0;
  const messages = new Messages({ w: { kind: "wire", value: "w-1" }, p: { kind: "provided" }, c: { kind: "connection" } }, async () => (asked++, "p-1"));
  assert.equal(await messages.write({ a: { $id: "w" }, b: { $id: "p" }, c: { $id: "p" }, d: { $json: "[1]" } }), '{"a":"w-1","b":"p-1","c":"p-1","d":[1]}');
  assert.equal(asked, 1);
  await assert.rejects(messages.write({ a: { $id: "c" } }), ScenarioFailure);
  await assert.rejects(messages.write({ a: { $id: "undeclared" } }), ScenarioFailure);
  assert.equal(messages.tryMatch({ a: { $id: "p" } }, { a: "p-2" }, null) !== null, true);
});

test("the generators expand rows and adds", () => {
  assert.deepEqual(expand([{ $rows: [3, 2] }, 9]), [{ id: 3, label: "row 3" }, { id: 4, label: "row 4" }, 9]);
  assert.deepEqual(expand({ changes: [{ $adds: [1, 1, 8] }] }), {
    changes: [{ field: "rows", kind: "add", index: 1, oldIndex: -1, keys: ["1"], items: [{ id: 1, label: "row 1..." }] }],
  });
  assert.ok(valueEqual({ a: [1, { b: 2 }] }, { a: [1, { b: 2 }] }) && !valueEqual({ a: 1 }, { a: 1, b: 1 }));
});

test("the Views corpus loads with unique ids, and its case list is stable", () => {
  const corpus = resolve(root, "specs/application/conformance");
  const cases = loadViewsCorpus(corpus);
  assert.ok(cases.length > 500);
  assert.equal(new Set(cases.map((c) => c.id)).size, cases.length);
  for (const c of cases) assert.match(c.sha256, /^[0-9a-f]{64}$/, c.id);
  // Vector cases have their file's roles; read-request cases carry the file's hello (README "Messages").
  const byId = new Map(cases.map((c) => [c.id, c]));
  assert.deepEqual(cases.find((c) => c.set === "canonical.syntax").roles, ["producer"]);
  assert.deepEqual(byId.get("messages.requests.hello").roles, ["producer"]);
  assert.equal(byId.get("messages.requests.hello").body.hello.kind, "hello");
  assert.deepEqual(cases.find((c) => c.set === "values.integers").roles, ["producer", "client"]);
  assert.deepEqual(cases.find((c) => c.set === "messages.pushes").roles, ["client"]);
  const text = formatCases(names.cases, "views/4", cases);
  assert.equal(text, formatCases(names.cases, "views/4", loadViewsCorpus(corpus)));
  assert.equal(coreFormatCases(names.cases, "views/4", cases), text, "the core writes the Views case list unchanged");
  assert.equal(JSON.parse(text).cases.length, cases.length);
});

test("the adapter client speaks JSON lines, reports errors and survives a hang", async () => {
  const dir = mkdtempSync(join(tmpdir(), "rax-conformance-adapter-"));
  try {
    const script = join(dir, "adapter.ts");
    writeFileSync(script, `
      import { createInterface } from "node:readline";
      for await (const line of createInterface({ input: process.stdin })) {
        const { seq, op, args } = JSON.parse(line);
        if (op === "hang") continue;
        if (op === "noise") process.stdout.write("not protocol\\n");
        const reply = op === "describe" ? { seq, ok: { protocol: "${names.adapterProtocol}" } }
          : op === "missing" ? { seq, error: { code: "unsupported", message: "no such op" } }
          : { seq, ok: { echo: args } };
        process.stdout.write(JSON.stringify(reply) + "\\n");
        if (op === "shutdown") break;
      }`);
    const adapter = new Adapter({ command: ["bun", script] });
    assert.equal((await adapter.start()).protocol, names.adapterProtocol);
    assert.deepEqual(await adapter.call("noise", { a: 1 }), { echo: { a: 1 } });
    await assert.rejects(adapter.call("missing", {}), (e) => e instanceof AdapterError && e.code === "unsupported");
    await assert.rejects(adapter.call("hang", {}, 200), (e) => e instanceof AdapterError && e.code === "timeout");
    await Bun.sleep(50);
    assert.equal(adapter.alive, false);
    await adapter.stop();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// runic-cli-sdk and runic-translations-sdk vendor eng/conformance/core unchanged (core/README.md, R7 G11).
test("the driver core imports only its own files, node:* and bun", () => {
  const dir = resolve(root, "eng/conformance/core");
  const files = readdirSync(dir).filter((f) => f.endsWith(".ts"));
  assert.ok(files.includes("driver.ts") && files.includes("index.ts"));
  for (const file of files) {
    const text = readFileSync(join(dir, file), "utf8");
    for (const [, specifier] of text.matchAll(/\bfrom\s+["']([^"']+)["']|\bimport\s*\(\s*["']([^"']+)["']/g)) {
      const ok = /^\.\/[\w-]+$/.test(specifier) ? existsSync(join(dir, `${specifier.slice(2)}.ts`)) : specifier === "bun" || specifier.startsWith("node:");
      assert.ok(ok, `${file} imports ${specifier}`);
    }
  }
});

test("a driver without corpus modules runs and reports that it has none", async () => {
  const dir = mkdtempSync(join(tmpdir(), "rax-conformance-empty-"));
  const log = console.log;
  const lines = [];
  console.log = (line) => lines.push(String(line));
  try {
    const driver = createDriver({ root: dir, dir, corpora: {}, command: "bun eng/conformance/cli.ts" });
    assert.equal(await driver.main([]), 0);
    assert.equal(await driver.main(["list"]), 0);
    assert.equal(await driver.main(["cases"]), 0);
    assert.equal(await driver.main(["lint"]), 0);
    assert.equal(await driver.main(["dotnet-commandline"]), 2);
  } finally {
    console.log = log;
    rmSync(dir, { recursive: true, force: true });
  }
  assert.match(lines.join("\n"), /no corpus modules yet/);
  assert.match(lines.join("\n"), /no implementations yet/);
  assert.match(lines.join("\n"), /usage: bun eng\/conformance\/cli\.ts <implementation>/, "the messages name the repository's command");
  assert.match(lines.join("\n"), /unknown implementation dotnet-commandline \(this repository has no corpus modules yet\)/);
});
