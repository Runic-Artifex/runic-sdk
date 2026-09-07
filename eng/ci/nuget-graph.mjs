import assert from "node:assert/strict";

export function assertCsWebUiDependencies(assets) {
  assert.ok(!Object.keys(assets.libraries).some(name => name.startsWith("Runic.Desktop/")),
    "The CS-WebUI consumer resolved Runic.Desktop.");
  const names = value => Array.isArray(value) ? value : Object.keys(value ?? {});
  const references = new Set([
    ...Object.values(assets.project.frameworks).flatMap(framework => names(framework.frameworkReferences)),
    ...Object.values(assets.targets).flatMap(libraries => Object.values(libraries)
      .flatMap(library => names(library.frameworkReferences))),
  ]);
  // SDK download/pruning metadata can mention frameworks the app never references.
  assert.ok(!references.has("Microsoft.AspNetCore.App"),
    `The CS-WebUI consumer references ASP.NET Core: ${[...references].join(", ")}`);
}
