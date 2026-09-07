import assert from "node:assert/strict";
import { test } from "node:test";
import { assertCsWebUiDependencies } from "./nuget-graph.mjs";

const graph = () => ({
  libraries: { "Runic.Application.CsWebUi/1.0.0-preview.1": { type: "package" } },
  project: { frameworks: { "net10.0": {
    frameworkReferences: { "Microsoft.NETCore.App": {} },
    downloadDependencies: [{ name: "Microsoft.AspNetCore.App.Ref", version: "10.0.10" }],
    packagesToPrune: { "Microsoft.AspNetCore.App": "(,10.0]" },
  } } },
  targets: { "net10.0/linux-x64": { "Runic.Application.CsWebUi/1.0.0-preview.1": {} } },
});

test("SDK download and pruning metadata does not imply a runtime framework reference", () => {
  assertCsWebUiDependencies(graph());
});
test("a direct ASP.NET Core framework reference fails the CS-WebUI boundary", () => {
  const assets = graph();
  assets.project.frameworks["net10.0"].frameworkReferences["Microsoft.AspNetCore.App"] = {};
  assert.throws(() => assertCsWebUiDependencies(assets), /references ASP.NET Core/);
});
test("a package's transitive ASP.NET Core framework reference also fails", () => {
  const assets = graph();
  assets.targets["net10.0/linux-x64"]["Accidental.Dependency/1.0"] = {
    frameworkReferences: ["Microsoft.AspNetCore.App"],
  };
  assert.throws(() => assertCsWebUiDependencies(assets), /references ASP.NET Core/);
});
test("resolving the Desktop package fails even without a framework reference", () => {
  const assets = graph();
  assets.libraries["Runic.Desktop/1.0.0-preview.1"] = { type: "package" };
  assert.throws(() => assertCsWebUiDependencies(assets), /resolved Runic.Desktop/);
});
