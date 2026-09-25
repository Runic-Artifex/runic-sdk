import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve, join } from "node:path";

const feed = resolve(process.argv[2] ?? "artifacts/packages/nuget");
const cache = resolve(process.env.NUGET_PACKAGES ?? ".cache/nuget");
const assets = JSON.parse(readFileSync("apps/translations-editor/obj/project.assets.json", "utf8"));
const version = process.env.RUNIC_EDITOR_PACKAGE_VERSION
  ?? JSON.parse(readFileSync("eng/workspace.json", "utf8")).version;

for (const id of ["Runic.Application", "Runic.Application.CsWebUi", "Runic.Application.ReactiveUI"]) {
  const identity = `${id}/${version}`;
  assert.equal(assets.libraries[identity]?.type, "package", `${identity} must be a package dependency of the editor`);
  const metadata = JSON.parse(readFileSync(join(cache, id.toLowerCase(), version, ".nupkg.metadata"), "utf8"));
  assert.equal(resolve(metadata.source), feed, `${identity} must come from the local package feed`);
}

console.log("EDITOR_LOCAL_PACKAGES_OK");
