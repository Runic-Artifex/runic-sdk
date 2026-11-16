// Writes the package's two builds from one TypeScript compilation (README, "Development build"; eng/web/development-build.mjs).
// Run after `tsc -p tsconfig.build.json`, which emits dist/*.js and dist/*.d.ts:
//
// - dist/development/*.js, the `development` export condition: `__RUNIC_DEVELOPMENT__` (src/development.d.ts) is `true`;
// - dist/*.js, every other condition: it is `false`, and the code it guards is gone. A module that no production entry
//   reaches any more, such as calls.js, is left out.
//
//   bun scripts/build.mjs
import { fileURLToPath } from "node:url";
import { buildDevelopment } from "../../../../eng/web/development-build.mjs";

await buildDevelopment({
  root: fileURLToPath(new URL("..", import.meta.url)),
  // What only the development build may contain: the Bridge-call channel and its async stack tasks (calls.ts), with the
  // development tools' Views protocol features and trace context (calls.ts, #958), the Runic Translations text channel
  // (translated-text.ts), the View descriptions that the bindings' DevTools labels read (describe.ts, #1041), and the
// console's custom object formatters (formatters.ts, #1042).
  markers: ["runic.views.calls", "createTask", "runic.translations.text", "trace-context/1", "handler-timing/1", "traceparent",
    "runic.views.describe", "devtoolsFormatters"],
});
