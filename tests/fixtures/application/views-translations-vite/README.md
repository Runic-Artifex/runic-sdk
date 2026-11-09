# views-translations Vite consumer

A page's Vite build that uses the packed `@runic-artifex/views-translations` with `runic.json` `overrides` of the
`runic-views` catalog, through `@runic-artifex/vite-plugin-runic-translations` with `dependencies: "auto"`: the plugin
regenerates the package's prebuilt catalog modules from its packed sources (`dist/catalog`) with the application's
overrides. `eng/verify-packages.mjs` copies this directory into the packed npm consumer, builds it with the
`dotnet-runic-translations` of `.config/dotnet-tools.json`, and runs `check.mjs` on the bundle; CI runs it with every
package consumer.

- `translations/overrides/runic-views/en.rmf2`: the application's English for one message; German keeps the package's.
- `translations/overrides/runic-views/fr.rmf2`: French, which the package lacks, with English for the rest.
