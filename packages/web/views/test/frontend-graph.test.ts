import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { describePackage, resolveFrontendGraph } from "@runic-artifex/views/frontend-graph";

// devtools-library-dx M4: the resolver lists the frontend packages that a frontend depends on, dependencies first, and
// describes each one's build, exports and output folders, which the Views targets build and check.
function folder(root: string, path: string, manifest: object): string {
  mkdirSync(join(root, path), { recursive: true });
  writeFileSync(join(root, path, "package.json"), JSON.stringify(manifest));
  return join(root, path);
}

test("the graph lists workspace, named and folder dependencies, transitively and dependencies first", () => {
  const root = realpathSync(mkdtempSync(join(tmpdir(), "runic-frontend-graph-")));
  try {
    folder(root, ".", { name: "root", private: true, workspaces: ["app", "packages/*"] });
    const app = folder(root, "app", { name: "@acme/app", dependencies: { "@acme/ui": "workspace:^", "local": "file:../vendor/local", "vite": "8.3.4" } });
    folder(root, "packages/ui", { name: "@acme/ui", scripts: { build: "tsc", watch: "tsc -w" }, exports: { ".": "./dist/index.js" },
      dependencies: { "@acme/tokens": "1.0.0", "@acme/app": "workspace:*" } });
    folder(root, "packages/tokens", { name: "@acme/tokens", main: "index.js" });
    folder(root, "vendor/local", { name: "local", exports: "./src/index.ts" });
    const graph = resolveFrontendGraph({ frontend: app, packageManager: "pnpm" });
    assert.equal(graph.workspaceRoot, root);
    assert.deepEqual(graph.packages.map(entry => [entry.package, entry.kind, entry.build]), [
      ["@acme/tokens", "source", null],
      ["@acme/ui", "compiled", "pnpm run build"],
      ["local", "source", null],
    ], "A member named with a version range counts in a workspace; the cycle back to the frontend is cut.");
    assert.deepEqual(graph.application.exports, []);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a package's exports, generated export and output folders", () => {
  const root = realpathSync(mkdtempSync(join(tmpdir(), "runic-frontend-graph-")));
  try {
    const compiled = folder(root, "compiled", { name: "@acme/shared-ui", scripts: { build: "tsc -p tsconfig.build.json" }, types: "dist/index.d.ts",
      exports: { ".": { types: "./dist/index.d.ts", default: "./dist/index.js" }, "./generated": { types: "./dist/generated/runic-clients.d.ts", default: "./dist/generated/runic-clients.js" }, "./*": "./dist/*.js" } });
    assert.deepEqual(describePackage(compiled, "bun"), {
      directory: compiled, package: "@acme/shared-ui", kind: "compiled", build: "bun run --bun build", watch: null,
      exports: ["./dist/index.d.ts", "./dist/index.js", "./dist/generated/runic-clients.d.ts", "./dist/generated/runic-clients.js"],
      generatedExport: "./dist/generated", outputs: ["./dist"],
    });
    const source = folder(root, "source", { name: "@acme/source", scripts: { typecheck: "tsc" }, exports: { "./generated": "./src/generated/runic-clients.ts" } });
    const described = describePackage(source, "npm");
    assert.deepEqual([described.kind, described.build, described.generatedExport, described.outputs], ["source", null, "./src/generated", []]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
