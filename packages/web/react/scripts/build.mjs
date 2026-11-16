// Writes the package's development build (dist/development/, the `development` export condition) and its production
// build (dist/) from one TypeScript compilation, like @runic-artifex/views (eng/web/development-build.mjs). Only the
// development build labels the hooks for React DevTools (src/debug-labels.ts, #1041).
//
//   bun scripts/build.mjs
import { fileURLToPath } from "node:url";
import { buildDevelopment } from "../../../../eng/web/development-build.mjs";

await buildDevelopment({
  root: fileURLToPath(new URL("..", import.meta.url)),
  markers: ["useDebugValue", "describeViewClient"],
});
