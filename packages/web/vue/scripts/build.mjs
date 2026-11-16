// Writes the package's development build (dist/development/, the `development` export condition) and its production
// build (dist/) from one TypeScript compilation, like @runic-artifex/views (eng/web/development-build.mjs). Only the
// development build adds the Runic inspector and timeline layer to Vue DevTools (src/devtools.ts, #1041).
//
//   bun scripts/build.mjs
import { fileURLToPath } from "node:url";
import { buildDevelopment } from "../../../../eng/web/development-build.mjs";

await buildDevelopment({
  root: fileURLToPath(new URL("..", import.meta.url)),
  markers: ["@vue/devtools-api", "runic.views.calls", "runic.views.inspector", "attachDevtools"],
});
