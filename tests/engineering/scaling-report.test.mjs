import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { budgetChanges, report } from "../scaling/report.mjs";
import { format, updatedBudgets } from "../scaling/run.mjs";

const root = resolve(import.meta.dir, "../..");

test("the report lists budget changes against the base, improvements and known costs", () => {
  const base = { a: { class: "O(1)", sizes: [1, 4], limit: 2 }, gone: { class: "O(n)", sizes: [1, 4], limit: 9 } };
  const budgets = {
    a: { class: "O(1)", sizes: [1, 4], limit: 3 },
    b: { class: "O(n)", sizes: [1, 4], limit: 8, known: "O(n) per event", issue: "sdk#1" },
  };
  expect(budgetChanges(base, budgets)).toEqual([
    { name: "a", change: "limit", before: "2", after: "3" },
    { name: "b", change: "added", before: "", after: "class O(n), sizes 1 → 4, limit 8" },
    { name: "gone", change: "removed", before: "9", after: "" },
  ]);
  const results = {
    a: { class: "O(1)", sizes: [1, 4], values: [3, 3], limit: 3, status: "ok" },
    b: { class: "O(n)", sizes: [1, 4], values: [1, 4], limit: 8, status: "improved", message: "below 75 % of its limit 8" },
  };
  const { markdown, problems } = report({ results, budgets, base, requireAll: true });
  expect(problems).toEqual([]);
  expect(markdown).toContain("- `b`: O(n) per event (O(n); sdk#1)");
  expect(markdown).toContain("- `b`: below 75 % of its limit 8");
  expect(markdown).toContain("| `a` | limit | 2 | 3 |");
});

test("the report fails on a budget entry without a result and on a result without an entry", () => {
  const budgets = { a: { class: "O(1)", sizes: [1, 4], limit: 1 } };
  const results = { b: { class: "O(1)", sizes: [1, 4], values: [1, 1], limit: 1, status: "ok" } };
  expect(report({ results, budgets, requireAll: true }).problems).toHaveLength(2);
  expect(report({ results: {}, budgets, requireAll: false }).problems).toEqual([]);
});

test("an update records the values and the limit, with the margin, and keeps the class and sizes", () => {
  const budgets = {
    schema: 1,
    cases: {
      count: { class: "O(n)", sizes: [1, 4], limit: 1, measured: [1, 1], note: "kept" },
      bytes: { class: "O(1)", sizes: [1, 4], margin: 0.25 },
      unmeasured: { class: "O(1)", sizes: [1, 4], limit: 5 },
    },
  };
  const results = { count: { values: [2, 8] }, bytes: { values: [100, 100] } };
  expect(updatedBudgets(budgets, results).cases).toEqual({
    count: { class: "O(n)", sizes: [1, 4], limit: 8, measured: [2, 8], note: "kept" },
    bytes: { class: "O(1)", sizes: [1, 4], limit: 125, measured: [100, 100], margin: 0.25 },
    unmeasured: { class: "O(1)", sizes: [1, 4], limit: 5 },
  });
});

test("budgets.json is written as bun run scaling --update writes it", () => {
  const text = readFileSync(resolve(root, "tests/scaling/budgets.json"), "utf8");
  expect(format(JSON.parse(text))).toBe(text);
});
