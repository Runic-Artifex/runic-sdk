- **Trusted component mode moved to `./experimental`** (`RUNICDESK103`). Its npm
  side was the stable-looking `@runic-artifex/views/component` entry and the
  `component` option of `runic()`, although the mode is experimental. Before 1.0
  an experimental npm API is exported only from `./experimental`, so 0.21 removes
  the `./component` entry without a deprecated alias:
  - Import `defineRunicComponent`, `connectComponentBridge` and the
    `RunicComponent*` types from `@runic-artifex/views/experimental` instead of
    `@runic-artifex/views/component`.
  - In `vite.config.ts`, import `runic` from
    `@runic-artifex/vite-plugin-runic/experimental` to use
    `runic({ component: { entry, name } })`; it takes the root entry's options
    too. The root entry's `runic` now throws for `component`, and no longer
    exports `RunicComponentOptions`, which the experimental entry exports with
    `RunicExperimentalViteOptions`.
