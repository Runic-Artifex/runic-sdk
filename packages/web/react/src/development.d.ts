// `true` in the development build (`dist/development/`, the `development` export condition) and `false` in the
// production build (`dist/`), which leaves out the code it guards: scripts/build.mjs replaces it
// (eng/web/development-build.mjs). Only the development build labels the hooks for React DevTools (debug-labels.ts).
// Bundle dist/ (or the package), never src/: only scripts/build.mjs defines the constant.
declare const __RUNIC_DEVELOPMENT__: boolean;
