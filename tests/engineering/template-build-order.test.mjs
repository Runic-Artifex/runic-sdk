import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { templateFrontendDirectory, templateFrontends } from "../../eng/release/template-locks.mjs";

// A Vite plugin can generate modules a type check needs, such as the Runic Translations
// virtual:runic-translations/... declarations, which exist only once Vite has run. On a clean
// checkout the template's build must therefore bundle before it type-checks
// (runic-translations-sdk#84).
for (const framework of templateFrontends.filter(name => name !== "angular")) {
  test(`${framework} template builds with Vite before its type check`, () => {
    const manifest = JSON.parse(readFileSync(new URL(`../../tools/Runic.Application.Templates/content/${templateFrontendDirectory(framework)}/package.json`, import.meta.url), "utf8"));
    // The build bundles, then runs the same check as the typecheck script.
    assert.deepEqual(manifest.scripts.build.split("&&").map(step => step.trim()), ["vite build", manifest.scripts.typecheck]);
  });
}
