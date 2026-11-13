import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { check, measurement, metrics } from "../scaling/aot-size/measure.mjs";
import { viewModelsSource } from "../scaling/aot-size/fixture.mjs";
import { budgetChanges, report, suiteCases } from "../scaling/report.mjs";
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

test("the managed report and the NativeAOT size budgets each take their own entries of budgets.json", () => {
  const cases = { "bridge/a": { class: "O(1)" }, "aot/bridge/b": { class: "O(n)" }, "aot/c": { class: "total" } };
  expect(Object.keys(suiteCases(cases, "managed"))).toEqual(["bridge/a"]);
  expect(Object.keys(suiteCases(cases, "aot-size"))).toEqual(["aot/bridge/b", "aot/c"]);
  const budgets = JSON.parse(readFileSync(resolve(root, "tests/scaling/budgets.json"), "utf8")).cases;
  expect(Object.keys(suiteCases(budgets, "aot-size")).sort()).toEqual(Object.keys(metrics).sort());
  for (const [name, entry] of Object.entries(suiteCases(budgets, "aot-size"))) {
    expect(entry.limit, name).toBeNumber();
    // #901 is fixed (A1-b): no aot/ entry is a known cost any more.
    expect(entry.known, name).toBeUndefined();
  }
  // D064.24: no generated-code support type is closed over a ViewModel.
  expect(budgets["aot/bridge/generated-generic-types"].limit).toBe(0);
});

test("the NativeAOT size measurement counts the records that name a fixture ViewModel", () => {
  const records = [
    "Types\t100\tRunic.Application.Views.Generated.PropertyDescriptor<AotSize.Models.Vm0>",
    "Types\t60\tSystem.Collections.Generic.Dictionary<System.String, Runic.Application.Views.Generated.PropertyDescriptor<AotSize.Models.Vm12>>",
    "Types\t40\tSystem.Func<AotSize.Models.Vm3, System.Object>",
    // Not a fixture ViewModel: the shell, a window type, and a Bridge over the shell.
    "Types\t500\tAotSize.Models.Vm0Window",
    "Types\t500\tRunic.Application.Views.Generated.PropertyDescriptor<AotSize.Models.Shell>",
    "Methods\t7\tAotSize.Models.Vm0.get_Step",
    "Blobs\t9\tDehydratedData",
  ].join("\n");
  expect(measurement(1000, records)).toEqual({
    executableBytes: 1000, viewModelTypes: 3, viewModelTypeBytes: 200, generatedGenericTypes: 2, viewModelMethodBytes: 7, types: 5,
  });
  const source = viewModelsSource(2);
  expect(source.match(/public sealed partial class \w+ : ObservableObject/g)).toEqual([
    "public sealed partial class Shell : ObservableObject", "public sealed partial class Vm0 : ObservableObject",
    "public sealed partial class Vm1 : ObservableObject"]);
  expect(source).toContain("public ViewRequest<FocusRow> Focus { get; } = new();");
});

test("the NativeAOT size check adds its noise before the class factor and at the larger size", () => {
  // #924: section alignment put K = 1 at 20960 while K = 9 stayed at 225328; 9 x 20960 x 1.02 + 32768 failed it.
  const executable = { class: "O(n)", sizes: [1, 9], limit: 229835, margin: 0.02, noise: 8192 };
  expect(check("e", executable, [20960, 225328]).result.status).toBe("ok");
  expect(check("e", executable, [20960, 280000]).failure).toContain("grows faster than O(n)");
});

test("the NativeAOT size check applies the class with its slack, the limit and the total's margin", () => {
  const perViewModel = { class: "O(n)", sizes: [1, 9], limit: 1000, slack: 50 };
  expect(check("a", perViewModel, [100, 940]).result.status).toBe("ok");
  expect(check("a", perViewModel, [100, 960]).failure).toContain("grows faster than O(n)");
  expect(check("a", perViewModel, [100, 960], true).failure).toBeNull();
  expect(check("a", { ...perViewModel, limit: 900 }, [100, 940]).failure).toContain("over its limit 900");
  expect(check("a", perViewModel, [10, 40]).result.message).toBe("grows slower than O(n); below 75 % of its limit 1000");
  const total = { class: "total", sizes: [0, 9], limit: 1020, margin: 0.02 };
  expect(check("t", total, [600, 1000]).result.status).toBe("ok");
  expect(check("t", total, [600, 1021]).failure).toContain("over its limit 1020");
  expect(check("t", { ...total, class: "O(n log n)" }, [1, 1]).failure).toContain("unknown class");
});
