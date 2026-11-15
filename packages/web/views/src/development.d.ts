// `true` in the development build (`dist/development/`, the `development` export condition) and `false` in the
// production build (`dist/`), which leaves out the code it guards: scripts/build.mjs replaces it. Only the development
// build carries the development tools that run on every page: Bridge-call reports and async stack tagging (calls.ts) and
// the Runic Translations text channel (translated-text.ts).
// Bundle dist/ (or the package), never src/: only scripts/build.mjs defines the constant, and a module that still names it
// throws a ReferenceError when it runs. The build fails when a shipped file of either build names it.
declare const __RUNIC_DEVELOPMENT__: boolean;
